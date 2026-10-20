#pragma once
#include <d3d12.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <cstring>
#include <utility>
#include "phoenix-host-pixels.h"

inline constexpr char kPhoenixUnpackShader[] = R"(
Texture2D<float4> source : register(t0);
RWTexture2D<float4> target : register(u0);
cbuffer Size : register(b0) { uint width, height, pitch, bgra; }
[numthreads(8,8,1)] void main(uint3 p : SV_DispatchThreadID) {
    if (p.x < width && p.y < height) target[p.xy] = source.Load(int3(p.xy,0));
})";
inline constexpr char kPhoenixPackShader[] = R"(
Texture2D<float4> source : register(t0);
RWByteAddressBuffer target : register(u0);
cbuffer Size : register(b0) { uint width, height, pitch, bgra; }
[numthreads(8,8,1)] void main(uint3 p : SV_DispatchThreadID) {
    if (p.x >= width || p.y >= height) return;
    float4 v = source.Load(int3(p.xy,0));
    v = float4(isfinite(v.x)?saturate(v.x):0, isfinite(v.y)?saturate(v.y):0,
               isfinite(v.z)?saturate(v.z):0, isfinite(v.w)?saturate(v.w):0);
    uint4 b = (uint4)(v*255.0+0.5);
    if (bgra) b = b.zyxw;
    target.Store(p.y*pitch+p.x*4, b.x | (b.y<<8) | (b.z<<16) | (b.w<<24));
})";

// All resources enter/leave COMMON. The caller submits and waits before reuse.
class PhoenixFormatAdapter {
    template<class T> using Ptr = Microsoft::WRL::ComPtr<T>;
    Ptr<ID3D12RootSignature> root_;
    Ptr<ID3D12PipelineState> unpack_, pack_;
    Ptr<ID3D12DescriptorHeap> heap_;
    Ptr<ID3D12Resource> color_, output_, bytes_;
    UINT width_ = 0, height_ = 0, pitch_ = 0, increment_ = 0;
    DXGI_FORMAT format_ = DXGI_FORMAT_UNKNOWN;

    static D3D12_RESOURCE_BARRIER Barrier(ID3D12Resource* resource,
                                         D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after) {
        D3D12_RESOURCE_BARRIER result{};
        result.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        result.Transition = {resource, D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES, before, after};
        return result;
    }
    HRESULT Pipeline(ID3D12Device* device, const char* source, Ptr<ID3D12PipelineState>& pipeline) {
        Ptr<ID3DBlob> code, errors;
        HRESULT hr = D3DCompile(source, strlen(source), "phoenix-format-adapter", nullptr, nullptr,
                               "main", "cs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &errors);
        if (FAILED(hr)) return hr;
        D3D12_COMPUTE_PIPELINE_STATE_DESC desc{};
        desc.pRootSignature = root_.Get();
        desc.CS = {code->GetBufferPointer(), code->GetBufferSize()};
        return device->CreateComputePipelineState(&desc, IID_PPV_ARGS(&pipeline));
    }
    HRESULT Initialize(ID3D12Device* device) {
        if (root_) return S_OK;
        D3D12_DESCRIPTOR_RANGE ranges[2]{};
        ranges[0] = {D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 1, 0, 0, 0};
        ranges[1] = {D3D12_DESCRIPTOR_RANGE_TYPE_UAV, 1, 0, 0, 0};
        D3D12_ROOT_PARAMETER parameters[3]{};
        for (unsigned i = 0; i < 2; ++i) {
            parameters[i].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
            parameters[i].DescriptorTable = {1, &ranges[i]};
        }
        parameters[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        parameters[2].Constants = {0, 0, 4};
        D3D12_ROOT_SIGNATURE_DESC desc{3, parameters, 0, nullptr, D3D12_ROOT_SIGNATURE_FLAG_NONE};
        Ptr<ID3DBlob> blob, errors;
        HRESULT hr = D3D12SerializeRootSignature(&desc, D3D_ROOT_SIGNATURE_VERSION_1, &blob, &errors);
        if (FAILED(hr)) return hr;
        hr = device->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(), IID_PPV_ARGS(&root_));
        if (FAILED(hr)) return hr;
        hr = Pipeline(device, kPhoenixUnpackShader, unpack_);
        if (SUCCEEDED(hr)) hr = Pipeline(device, kPhoenixPackShader, pack_);
        if (FAILED(hr)) { root_.Reset(); return hr; }
        D3D12_DESCRIPTOR_HEAP_DESC heap{};
        heap.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        heap.NumDescriptors = 2;
        heap.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        hr = device->CreateDescriptorHeap(&heap, IID_PPV_ARGS(&heap_));
        if (FAILED(hr)) { root_.Reset(); return hr; }
        increment_ = device->GetDescriptorHandleIncrementSize(heap.Type);
        return S_OK;
    }
    void Bind(ID3D12Device* device, ID3D12GraphicsCommandList* list,
              ID3D12Resource* source, bool pack) {
        D3D12_SHADER_RESOURCE_VIEW_DESC srv{};
        srv.Format = source->GetDesc().Format;
        srv.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
        srv.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
        srv.Texture2D.MipLevels = 1;
        auto cpu = heap_->GetCPUDescriptorHandleForHeapStart();
        device->CreateShaderResourceView(source, &srv, cpu);
        cpu.ptr += increment_;
        D3D12_UNORDERED_ACCESS_VIEW_DESC uav{};
        if (pack) {
            uav.Format = DXGI_FORMAT_R32_TYPELESS;
            uav.ViewDimension = D3D12_UAV_DIMENSION_BUFFER;
            uav.Buffer.NumElements = pitch_ * height_ / 4;
            uav.Buffer.Flags = D3D12_BUFFER_UAV_FLAG_RAW;
        } else {
            uav.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
            uav.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
        }
        device->CreateUnorderedAccessView(pack ? bytes_.Get() : color_.Get(), nullptr, &uav, cpu);
        ID3D12DescriptorHeap* heaps[]{heap_.Get()};
        list->SetDescriptorHeaps(1, heaps);
        list->SetComputeRootSignature(root_.Get());
        list->SetPipelineState(pack ? pack_.Get() : unpack_.Get());
        auto gpu = heap_->GetGPUDescriptorHandleForHeapStart();
        list->SetComputeRootDescriptorTable(0, gpu);
        gpu.ptr += increment_;
        list->SetComputeRootDescriptorTable(1, gpu);
        const UINT constants[]{width_, height_, pitch_, format_ == DXGI_FORMAT_B8G8R8A8_UNORM ? 1u : 0u};
        list->SetComputeRoot32BitConstants(2, 4, constants, 0);
        list->Dispatch((width_ + 7) / 8, (height_ + 7) / 8, 1);
    }
public:
    ID3D12Resource* Color() const { return color_.Get(); }
    ID3D12Resource* Output() const { return output_.Get(); }
    HRESULT Ensure(ID3D12Device* device, UINT width, UINT height, DXGI_FORMAT format) {
        if (!width || !height || width > D3D12_REQ_TEXTURE2D_U_OR_V_DIMENSION ||
            height > D3D12_REQ_TEXTURE2D_U_OR_V_DIMENSION || !PhoenixByteFormat(format)) return E_INVALIDARG;
        HRESULT hr = Initialize(device);
        if (FAILED(hr)) return hr;
        if (color_ && output_ && bytes_ && width_ == width && height_ == height && format_ == format) return S_OK;
        color_.Reset(); output_.Reset(); bytes_.Reset();
        width_ = width; height_ = height; format_ = format; pitch_ = PhoenixByteRowPitch(width);
        D3D12_HEAP_PROPERTIES heap{};
        heap.Type = D3D12_HEAP_TYPE_DEFAULT;
        D3D12_RESOURCE_DESC desc{};
        desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        desc.Width = width; desc.Height = height; desc.DepthOrArraySize = 1;
        desc.MipLevels = 1; desc.SampleDesc.Count = 1;
        desc.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
        desc.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
        hr = device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc,
                                             D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&color_));
        if (FAILED(hr)) return hr;
        hr = device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc,
                                             D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&output_));
        if (FAILED(hr)) return hr;
        desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        desc.Width = UINT64(pitch_) * height; desc.Height = 1;
        desc.Format = DXGI_FORMAT_UNKNOWN; desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        return device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc,
                                               D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&bytes_));
    }
    void RecordInput(ID3D12Device* device, ID3D12GraphicsCommandList* list, ID3D12Resource* source) {
        D3D12_RESOURCE_BARRIER barriers[]{
            Barrier(source, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE),
            Barrier(color_.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_UNORDERED_ACCESS)};
        list->ResourceBarrier(2, barriers);
        Bind(device, list, source, false);
        for (auto& barrier : barriers) std::swap(barrier.Transition.StateBefore, barrier.Transition.StateAfter);
        list->ResourceBarrier(2, barriers);
    }
    void RecordOutput(ID3D12Device* device, ID3D12GraphicsCommandList* list, ID3D12Resource* destination,
                      ID3D12Resource* converted_input = nullptr) {
        auto* source = converted_input ? converted_input : output_.Get();
        D3D12_RESOURCE_BARRIER barriers[]{
            Barrier(source, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE),
            Barrier(bytes_.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_UNORDERED_ACCESS)};
        list->ResourceBarrier(2, barriers);
        Bind(device, list, source, true);
        barriers[0] = Barrier(source, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COMMON);
        barriers[1] = Barrier(bytes_.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COPY_SOURCE);
        list->ResourceBarrier(2, barriers);
        auto to_copy = Barrier(destination, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_DEST);
        list->ResourceBarrier(1, &to_copy);
        D3D12_TEXTURE_COPY_LOCATION src{}, dst{};
        src.pResource = bytes_.Get(); src.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
        src.PlacedFootprint.Footprint = {format_, width_, height_, 1, pitch_};
        dst.pResource = destination; dst.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        list->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
        barriers[0] = Barrier(destination, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_COMMON);
        barriers[1] = Barrier(bytes_.Get(), D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_COMMON);
        list->ResourceBarrier(2, barriers);
    }
};
