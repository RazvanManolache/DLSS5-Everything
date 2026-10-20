#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <d3d10.h>
#include <wrl/client.h>
#include <SpoutDX.h>
#include <filesystem>
#include <chrono>
#include <vector>
#include <iostream>
#include <fstream>
using Microsoft::WRL::ComPtr;
namespace fs=std::filesystem;
void check(HRESULT result,const char* text) { if(FAILED(result)) throw std::runtime_error(std::string(text)+" HRESULT "+std::to_string(static_cast<unsigned>(result))); }

int wmain() {
    CoInitializeEx(nullptr,COINIT_MULTITHREADED);
    try {
        ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context; D3D_FEATURE_LEVEL level;
        check(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&device,&level,&context),"GPU");
        ComPtr<ID3D10Multithread> multithread; check(context.As(&multithread),"Multithread context"); multithread->SetMultithreadProtected(TRUE);
        for (const auto format : { DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_FORMAT_R8G8B8A8_UNORM }) {
        D3D11_TEXTURE2D_DESC desc{}; desc.Width=320; desc.Height=240; desc.ArraySize=desc.MipLevels=1; desc.Format=format;
        desc.SampleDesc.Count=1; desc.BindFlags=D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_RENDER_TARGET;
        std::vector<uint32_t> pixels(320*240,0xff4080c0); D3D11_SUBRESOURCE_DATA initial{pixels.data(),320*4,0};
        ComPtr<ID3D11Texture2D> texture; check(device->CreateTexture2D(&desc,&initial,&texture),"Texture");
        DisableSpoutLog(); spoutDX sender,receiver;
        if(!sender.OpenDirectX11(device.Get()) || !receiver.OpenDirectX11(device.Get())) throw std::runtime_error("Spout GPU init");
        const auto name="DLSS5 Synthetic "+std::to_string(GetCurrentProcessId()); sender.SetSenderName(name.c_str()); receiver.SetReceiverName(name.c_str()); sender.SetSenderFormat(desc.Format);
        ComPtr<ID3D11Texture2D> received; check(device->CreateTexture2D(&desc,nullptr,&received),"Receiver texture"); bool matched=false;
        for(int i=0;i<100 && !matched;++i) {
            if(!sender.SendTexture(texture.Get())) throw std::runtime_error("Spout send failed");
            const auto connected=receiver.ReceiveTexture(received.GetAddressOf());
            if(receiver.IsUpdated()) { Sleep(10); continue; }
            if(connected && received) {
                D3D11_TEXTURE2D_DESC read{}; received->GetDesc(&read);
                if(read.Width==320 && read.Height==240) {
                    read.Usage=D3D11_USAGE_STAGING; read.CPUAccessFlags=D3D11_CPU_ACCESS_READ; read.BindFlags=read.MiscFlags=0;
                    ComPtr<ID3D11Texture2D> staging; check(device->CreateTexture2D(&read,nullptr,&staging),"Readback"); context->CopyResource(staging.Get(),received.Get());
                    D3D11_MAPPED_SUBRESOURCE map{}; check(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&map),"Map"); matched=*static_cast<uint32_t*>(map.pData)==pixels[0]; context->Unmap(staging.Get(),0);
                }
            } Sleep(10);
        }
        receiver.ReleaseReceiver(); sender.ReleaseSender(); receiver.CloseDirectX11(); sender.CloseDirectX11();
        if(!matched) throw std::runtime_error("Spout received no matching pixels");
        }
        std::cout<<"Spout BGRA and RGBA GPU pixel roundtrips passed. No desktop captured.\n";
        CoUninitialize(); return 0;
    } catch(const std::exception& error) { std::cerr<<error.what()<<'\n'; CoUninitialize(); return 1; }
}
