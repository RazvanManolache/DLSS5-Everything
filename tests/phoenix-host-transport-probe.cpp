#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d12.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <functional>
#include <memory>
#include <cstring>
#include <vector>
#include <array>
#include <string>
#include "../source/src/feed_ipc.h"
#include "../source/host/phoenix-host-config.h"

template<class T> using Ptr = Microsoft::WRL::ComPtr<T>;
struct Handle {
    HANDLE value = nullptr;
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    Handle() = default;
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
};
static void Require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }
static void Hr(HRESULT result) {
    if (FAILED(result)) throw std::runtime_error("Windows API failed: " + std::to_string(static_cast<unsigned>(result)));
}
static void Transfer(HANDLE pipe, void* buffer, DWORD size, bool write) {
    auto* bytes = static_cast<BYTE*>(buffer);
    while (size) {
        DWORD count = 0;
        const BOOL ok = write ? WriteFile(pipe, bytes, size, &count, nullptr) : ReadFile(pipe, bytes, size, &count, nullptr);
        Require(ok && count, "IPC transfer failed");
        bytes += count; size -= count;
    }
}
struct Image { UINT width = 0, height = 0; std::vector<BYTE> pixels; };
static Image Load(const wchar_t* path) {
    Ptr<IWICImagingFactory> factory;
    Hr(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory)));
    Ptr<IWICBitmapDecoder> decoder; Ptr<IWICBitmapFrameDecode> frame; Ptr<IWICFormatConverter> converter;
    Hr(factory->CreateDecoderFromFilename(path, nullptr, GENERIC_READ, WICDecodeMetadataCacheOnLoad, &decoder));
    Hr(decoder->GetFrame(0, &frame)); Hr(factory->CreateFormatConverter(&converter));
    Hr(converter->Initialize(frame.Get(), GUID_WICPixelFormat32bppRGBA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom));
    Image image; Hr(converter->GetSize(&image.width, &image.height));
    Require(image.width && image.height && image.width <= 8192 && image.height <= 8192, "Invalid probe image size");
    image.pixels.resize(size_t(image.width) * image.height * 4);
    Hr(converter->CopyPixels(nullptr, image.width * 4, static_cast<UINT>(image.pixels.size()), image.pixels.data()));
    return image;
}
static void Save(const std::filesystem::path& path, const Image& image) {
    Ptr<IWICImagingFactory> factory; Ptr<IWICStream> stream; Ptr<IWICBitmapEncoder> encoder;
    Ptr<IWICBitmapFrameEncode> frame;
    Hr(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory)));
    Hr(factory->CreateStream(&stream)); Hr(stream->InitializeFromFilename(path.c_str(), GENERIC_WRITE));
    Hr(factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder));
    Hr(encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache));
    Hr(encoder->CreateNewFrame(&frame, nullptr)); Hr(frame->Initialize(nullptr));
    Hr(frame->SetSize(image.width, image.height));
    WICPixelFormatGUID format = GUID_WICPixelFormat32bppBGRA;
    Hr(frame->SetPixelFormat(&format));
    Require(IsEqualGUID(format, GUID_WICPixelFormat32bppBGRA) || IsEqualGUID(format, GUID_WICPixelFormat32bppRGBA), "Unexpected PNG format");
    auto pixels = image.pixels;
    if (IsEqualGUID(format, GUID_WICPixelFormat32bppBGRA))
        for (size_t i = 0; i < pixels.size(); i += 4) std::swap(pixels[i], pixels[i + 2]);
    Hr(frame->WritePixels(image.height, image.width * 4, static_cast<UINT>(pixels.size()), pixels.data()));
    Hr(frame->Commit()); Hr(encoder->Commit());
}
struct OwnHost {
    Handle job, process;
    DWORD pid = 0;
    OwnHost(const std::filesystem::path& binary) {
        job.value = CreateJobObjectW(nullptr, nullptr); Require(job.value != nullptr, "CreateJobObject failed");
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        Require(SetInformationJobObject(job.value, JobObjectExtendedLimitInformation, &limits, sizeof(limits)), "Job limits failed");
        std::wstring command = L"\"" + binary.wstring() + L"\" " + std::to_wstring(GetCurrentProcessId()) + L" --hide";
        STARTUPINFOW startup{}; startup.cb = sizeof(startup); startup.dwFlags = STARTF_USESHOWWINDOW; startup.wShowWindow = SW_HIDE;
        PROCESS_INFORMATION info{};
        Require(CreateProcessW(binary.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW | CREATE_SUSPENDED,
                               nullptr, binary.parent_path().c_str(), &startup, &info), "Cannot start isolated host");
        process.value = info.hProcess; pid = info.dwProcessId;
        if (!AssignProcessToJobObject(job.value, process.value)) {
            TerminateProcess(process.value, 1); CloseHandle(info.hThread); throw std::runtime_error("Cannot contain probe host in job");
        }
        ResumeThread(info.hThread); CloseHandle(info.hThread);
    }
    void Finish() {
        Require(WaitForSingleObject(process.value, 10000) == WAIT_OBJECT_0, "Host did not exit after pipe closure");
        DWORD code = 1; GetExitCodeProcess(process.value, &code); Require(code == 0, "Host exited unsuccessfully");
    }
};
struct SharedGpu {
    Ptr<ID3D12Device> device; Ptr<ID3D12CommandQueue> queue; Ptr<ID3D12CommandAllocator> allocator;
    Ptr<ID3D12GraphicsCommandList> list; Ptr<ID3D12Fence> fence, in, out;
    Ptr<ID3D12Resource> textures[FEED_SLOTS]; Handle handles[FEED_SLOTS], event, in_handle, out_handle;
    UINT64 serial = 0;
    SharedGpu() {
        Hr(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device)));
        D3D12_COMMAND_QUEUE_DESC desc{}; Hr(device->CreateCommandQueue(&desc, IID_PPV_ARGS(&queue)));
        Hr(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator)));
        Hr(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator.Get(), nullptr, IID_PPV_ARGS(&list)));
        Hr(list->Close()); Hr(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence)));
        event.value = CreateEventW(nullptr, FALSE, FALSE, nullptr); Require(event.value != nullptr, "Fence event failed");
    }
    void Wait(ID3D12Fence* target, UINT64 value, HANDLE peer = nullptr) {
        Require(target->GetCompletedValue() != UINT64_MAX, "Synthetic device removed");
        if (target->GetCompletedValue() >= value) return;
        Hr(target->SetEventOnCompletion(value, event.value));
        const auto deadline = GetTickCount64() + 120000;
        for (;;) {
            const auto completed = target->GetCompletedValue();
            Require(completed != UINT64_MAX, "Synthetic device removed");
            if (completed >= value) return;
            Require(GetTickCount64() < deadline, "Synthetic GPU fence timed out");
            const auto wake = WaitForSingleObject(event.value, 100);
            Require(wake == WAIT_OBJECT_0 || wake == WAIT_TIMEOUT, "Synthetic event failed");
            Require(!peer || WaitForSingleObject(peer, 0) != WAIT_OBJECT_0, "Host exited without publishing shared output");
        }
    }
    void Submit(const std::function<void()>& record) {
        Hr(allocator->Reset()); Hr(list->Reset(allocator.Get(), nullptr)); record(); Hr(list->Close());
        ID3D12CommandList* lists[]{list.Get()}; queue->ExecuteCommandLists(1, lists);
        Hr(queue->Signal(fence.Get(), ++serial)); Wait(fence.Get(), serial);
    }
    static D3D12_RESOURCE_BARRIER Barrier(ID3D12Resource* resource, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after) {
        D3D12_RESOURCE_BARRIER result{}; result.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        result.Transition = {resource, D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES, before, after}; return result;
    }
    Ptr<ID3D12Resource> Buffer(UINT64 bytes, D3D12_HEAP_TYPE type) {
        D3D12_HEAP_PROPERTIES heap{}; heap.Type = type;
        D3D12_RESOURCE_DESC desc{}; desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        desc.Width = bytes; desc.Height = 1; desc.DepthOrArraySize = 1; desc.MipLevels = 1;
        desc.SampleDesc.Count = 1; desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        Ptr<ID3D12Resource> result;
        Hr(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc,
            type == D3D12_HEAP_TYPE_UPLOAD ? D3D12_RESOURCE_STATE_GENERIC_READ : D3D12_RESOURCE_STATE_COPY_DEST,
            nullptr, IID_PPV_ARGS(&result))); return result;
    }
    void Upload(unsigned slot, const void* pixels, UINT row_bytes) {
        const auto desc = textures[slot]->GetDesc();
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint{}; UINT64 size = 0;
        device->GetCopyableFootprints(&desc, 0, 1, 0, &footprint, nullptr, nullptr, &size);
        auto buffer = Buffer(size, D3D12_HEAP_TYPE_UPLOAD);
        BYTE* mapped = nullptr; Hr(buffer->Map(0, nullptr, reinterpret_cast<void**>(&mapped)));
        for (UINT y = 0; y < desc.Height; ++y)
            memcpy(mapped + size_t(y) * footprint.Footprint.RowPitch, static_cast<const BYTE*>(pixels) + size_t(y) * row_bytes, row_bytes);
        buffer->Unmap(0, nullptr);
        Submit([&] {
            auto barrier = Barrier(textures[slot].Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_DEST);
            list->ResourceBarrier(1, &barrier);
            D3D12_TEXTURE_COPY_LOCATION src{}, dst{};
            src.pResource = buffer.Get(); src.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT; src.PlacedFootprint = footprint;
            dst.pResource = textures[slot].Get(); dst.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
            list->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
            std::swap(barrier.Transition.StateBefore, barrier.Transition.StateAfter); list->ResourceBarrier(1, &barrier);
        });
    }
    void Build(FeedBuild& build, const Image& image) {
        const DXGI_FORMAT formats[]{static_cast<DXGI_FORMAT>(build.color_fmt), DXGI_FORMAT_R8G8B8A8_UNORM,
                                    DXGI_FORMAT_R32_FLOAT, DXGI_FORMAT_R16G16_FLOAT, DXGI_FORMAT_R8_UNORM};
        for (unsigned i = 0; i < FEED_SLOTS; ++i) {
            D3D12_HEAP_PROPERTIES heap{}; heap.Type = D3D12_HEAP_TYPE_DEFAULT;
            D3D12_RESOURCE_DESC desc{}; desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
            desc.Width = image.width; desc.Height = image.height; desc.DepthOrArraySize = 1; desc.MipLevels = 1;
            desc.SampleDesc.Count = 1; desc.Format = formats[i];
            Hr(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_SHARED, &desc, D3D12_RESOURCE_STATE_COMMON,
                                                nullptr, IID_PPV_ARGS(&textures[i])));
            Hr(device->CreateSharedHandle(textures[i].Get(), nullptr, GENERIC_ALL, nullptr, &handles[i].value));
            build.tex[i] = reinterpret_cast<uint64_t>(handles[i].value);
        }
        auto color = image.pixels;
        if (build.color_fmt == DXGI_FORMAT_B8G8R8A8_UNORM)
            for (size_t i = 0; i < color.size(); i += 4) std::swap(color[i], color[i + 2]);
        Upload(FEED_COLOR, color.data(), image.width * 4);
        std::vector<float> depth(size_t(image.width) * image.height, 1.0f);
        std::vector<uint32_t> motion(depth.size(), 0);
        Upload(FEED_DEPTH, depth.data(), image.width * 4); Upload(FEED_MV, motion.data(), image.width * 4);
    }
    void Open(const FeedBuildAck& ack) {
        in_handle.value = reinterpret_cast<HANDLE>(ack.fence_in); out_handle.value = reinterpret_cast<HANDLE>(ack.fence_out);
        Hr(device->OpenSharedHandle(in_handle.value, IID_PPV_ARGS(&in)));
        Hr(device->OpenSharedHandle(out_handle.value, IID_PPV_ARGS(&out)));
    }
    Image Download(UINT width, UINT height) {
        const auto desc = textures[FEED_OUTPUT]->GetDesc();
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint{}; UINT64 size = 0;
        device->GetCopyableFootprints(&desc, 0, 1, 0, &footprint, nullptr, nullptr, &size);
        auto buffer = Buffer(size, D3D12_HEAP_TYPE_READBACK);
        Submit([&] {
            auto barrier = Barrier(textures[FEED_OUTPUT].Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_SOURCE);
            list->ResourceBarrier(1, &barrier);
            D3D12_TEXTURE_COPY_LOCATION src{}, dst{};
            src.pResource = textures[FEED_OUTPUT].Get(); src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
            dst.pResource = buffer.Get(); dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT; dst.PlacedFootprint = footprint;
            list->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
            std::swap(barrier.Transition.StateBefore, barrier.Transition.StateAfter); list->ResourceBarrier(1, &barrier);
        });
        Image image{width, height, std::vector<BYTE>(size_t(width) * height * 4)};
        BYTE* mapped = nullptr; Hr(buffer->Map(0, nullptr, reinterpret_cast<void**>(&mapped)));
        for (UINT y = 0; y < height; ++y)
            memcpy(image.pixels.data() + size_t(y) * width * 4, mapped + size_t(y) * footprint.Footprint.RowPitch, width * 4);
        buffer->Unmap(0, nullptr); return image;
    }
};

static FeedFrameMsg ProbeFrame(unsigned frame, unsigned iterations, bool stereo) {
    const unsigned count = frame <= 1 ? 1 : iterations;
    const unsigned eye = stereo && frame ? (frame - 1) % 2 : 0;
    const unsigned reset = !stereo || frame <= 2 ? 1 : 0;
    return FeedFrameMsg{frame + 1ull, reset, frame ? 1u : 0u, count, eye};
}

static bool StereoCountersMatch(const nlohmann::json& summary, unsigned frames) {
    try {
        const auto& recorded = summary.at("feeder_stream_recorded");
        const auto& resets = summary.at("feeder_stream_resets");
        if (!frames || !recorded.is_array() || recorded.size() != 3 || !resets.is_array() || resets.size() != 3)
            return false;
        const std::array<uint64_t, 3> expected{0, (frames + 1ull) / 2, frames / 2ull};
        for (unsigned stream = 0; stream < expected.size(); ++stream)
            if (recorded[stream].get<uint64_t>() != expected[stream] ||
                resets[stream].get<uint64_t>() != (expected[stream] ? 1ull : 0ull)) return false;
        return true;
    } catch (const std::exception&) { return false; }
}

static int StereoCpuSelftest() {
    unsigned checks = 0;
    auto expect = [&](bool condition) { ++checks; Require(condition, "Stereo CPU contract failed"); };
    const std::array<unsigned, 5> eyes{0, 0, 1, 0, 1};
    const std::array<unsigned, 5> resets{1, 1, 1, 0, 0};
    for (unsigned frame = 0; frame < eyes.size(); ++frame) {
        const auto message = ProbeFrame(frame, 1, true);
        expect(message.n == frame + 1ull && message.eye_index == eyes[frame]);
        expect(message.reset == resets[frame] && message.iterations == 1);
        expect(message.nr_enabled == (frame ? 1u : 0u));
    }
    expect(!StereoCountersMatch(nlohmann::json::object(), 4));
    for (unsigned frames = 4; frames <= 8; ++frames) {
        nlohmann::json summary{{"feeder_stream_recorded", {0, (frames + 1) / 2, frames / 2}},
                               {"feeder_stream_resets", {0, 1, 1}}};
        expect(StereoCountersMatch(summary, frames));
        auto changed = summary; changed["feeder_stream_resets"][1] = 2; expect(!StereoCountersMatch(changed, frames));
        changed = summary; changed["feeder_stream_resets"][2] = 2; expect(!StereoCountersMatch(changed, frames));
        changed = summary; changed["feeder_stream_recorded"][0] = 1; expect(!StereoCountersMatch(changed, frames));
        changed = summary; changed["feeder_stream_recorded"][1] = 0; expect(!StereoCountersMatch(changed, frames));
        changed = summary; changed["feeder_stream_recorded"][2] = 0; expect(!StereoCountersMatch(changed, frames));
        changed = summary; changed["feeder_stream_resets"][2] = 0; expect(!StereoCountersMatch(changed, frames));
    }
    std::cout << checks << " stereo schedule/counter CPU checks passed; no GPU executed\n";
    return 0;
}

int wmain(int argc, wchar_t** argv) {
    try {
        if (argc == 2 && std::wstring(argv[1]) == L"--stereo-selftest") return StereoCpuSelftest();
        if (argc == 2 && std::wstring(argv[1]) == L"--codec-selftest") {
            Hr(CoInitializeEx(nullptr, COINIT_MULTITHREADED));
            const auto path = std::filesystem::temp_directory_path() / (L"phoenix-probe-codec-" + std::to_wstring(GetCurrentProcessId()) + L".png");
            const Image input{2, 2, {255,0,0,255, 0,255,0,128, 0,0,255,64, 17,81,149,32}};
            Save(path, input); const auto output = Load(path.c_str()); std::filesystem::remove(path);
            Require(output.pixels == input.pixels, "Probe codec roundtrip differs");
            std::cout << "1 PNG codec CPU roundtrip passed; no GPU executed\n"; return 0;
        }
        Require(argc == 7, "Usage: probe host.exe input.jpg output.png cpu|shared|shared-bgra|shared-stereo|shared-bgra-stereo frames iterations");
        Hr(CoInitializeEx(nullptr, COINIT_MULTITHREADED));
        const auto host_path = std::filesystem::absolute(argv[1]);
        const auto output_path = std::filesystem::absolute(argv[3]);
        const std::wstring route = argv[4];
        const bool stereo = route == L"shared-stereo" || route == L"shared-bgra-stereo";
        const bool cpu = route == L"cpu";
        const bool bgra = route == L"shared-bgra" || route == L"shared-bgra-stereo";
        Require(cpu || bgra || route == L"shared" || route == L"shared-stereo", "Unknown synthetic transport");
        const unsigned frames = std::stoul(argv[5]), iterations = std::stoul(argv[6]);
        Require(frames > 0 && frames <= 8 && iterations > 0 && iterations <= 10, "Invalid probe count");
        Require(!stereo || (frames >= 4 && iterations == 1), "Stereo continuity probes require 4..8 frames and one iteration");
        const auto input = Load(argv[2]);
        std::unique_ptr<SharedGpu> gpu;
        FeedBuild build{};
        build.width = build.output_width = input.width; build.height = build.output_height = input.height;
        build.color_fmt = bgra ? DXGI_FORMAT_B8G8R8A8_UNORM : DXGI_FORMAT_R8G8B8A8_UNORM;
        build.output_fmt = DXGI_FORMAT_R8G8B8A8_UNORM;
        build.depth_inverted = 1; build.flags_override = -1; build.cpu_bridge = cpu ? 1 : 0;
        build.mv_scale_x = build.mv_scale_y = 1.0f;
        build.game_side_vr_split = stereo ? 1 : 0;
        build.present_width = stereo ? input.width * 2 : input.width;
        build.present_height = input.height;
        if (!cpu) { gpu = std::make_unique<SharedGpu>(); gpu->Build(build, input); }
        OwnHost host(host_path);
        Handle pipe;
        const std::wstring name = L"\\\\.\\pipe\\dlss5-feed." + std::to_wstring(GetCurrentProcessId());
        const auto deadline = GetTickCount64() + 120000;
        do {
            pipe.value = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
            if (pipe.value != INVALID_HANDLE_VALUE) break;
            Require(WaitForSingleObject(host.process.value, 0) != WAIT_OBJECT_0, "Host exited before pipe connection");
            Sleep(50);
        } while (GetTickCount64() < deadline);
        Require(pipe.value != INVALID_HANDLE_VALUE, "Host pipe connection timed out");
        FeedHello hello{FEED_IPC_MAGIC, FEED_IPC_VERSION, GetCurrentProcessId()}; FeedHelloAck hello_ack{};
        Transfer(pipe.value, &hello, sizeof(hello), true); Transfer(pipe.value, &hello_ack, sizeof(hello_ack), false);
        Require(hello_ack.magic == FEED_IPC_MAGIC && hello_ack.version == FEED_IPC_VERSION, "Protocol handshake failed");
        BYTE tag = 'B'; Transfer(pipe.value, &tag, 1, true); Transfer(pipe.value, &build, sizeof(build), true);
        FeedBuildAck ack{}; Transfer(pipe.value, &ack, sizeof(ack), false); Require(ack.ok == 1, "Real host rejected build");
        if (gpu) gpu->Open(ack);
        Image baseline, processed;
        Image eye_outputs[2];
        nlohmann::json schedule = nlohmann::json::array();
        nlohmann::json frame_trace = nlohmann::json::array();
        for (unsigned frame = 0; frame <= frames; ++frame) {
            auto message = ProbeFrame(frame, iterations, stereo);
            if (gpu) Hr(gpu->queue->Signal(gpu->in.Get(), message.n));
            tag = 'F'; Transfer(pipe.value, &tag, 1, true); Transfer(pipe.value, &message, sizeof(message), true);
            Image result{input.width, input.height, std::vector<BYTE>(input.pixels.size())};
            if (cpu) {
                Transfer(pipe.value, const_cast<BYTE*>(input.pixels.data()), static_cast<DWORD>(input.pixels.size()), true);
                FeedCpuFrameAck reply{}; Transfer(pipe.value, &reply, sizeof(reply), false);
                Require(reply.ok == 1 && reply.row_bytes == input.width * 4, "CPU frame failed");
                Transfer(pipe.value, result.pixels.data(), static_cast<DWORD>(result.pixels.size()), false);
            } else {
                gpu->Wait(gpu->out.Get(), message.n, host.process.value); result = gpu->Download(input.width, input.height);
            }
            if (frame == 0) baseline = std::move(result);
            else {
                schedule.push_back(message.iterations);
                processed = std::move(result);
                if (frame == 1) { auto path = output_path; path += L".iter1.png"; Save(path, processed); }
                if (stereo) {
                    eye_outputs[message.eye_index] = processed;
                    nlohmann::json summary;
                    std::ifstream(host_path.parent_path() / "phoenix-host-output-summary.json") >> summary;
                    frame_trace.push_back({{"frame", frame}, {"stream_id", message.eye_index + 1},
                        {"reset_requested", message.reset}, {"completed_frames", summary.at("completed_frames")},
                        {"feeder_stream_recorded", summary.at("feeder_stream_recorded")},
                        {"feeder_stream_resets", summary.at("feeder_stream_resets")}});
                    std::ofstream(output_path.parent_path() / "transport-probe-frames.json") << frame_trace.dump(2);
                    Require(StereoCountersMatch(summary, frame), "Stereo recording/reset counters do not preserve each eye's history");
                    Require(summary.at("completed_frames").get<unsigned>() == frame,
                            "Stereo completed-frame count did not include both active and parked runners");
                }
            }
        }
        Require(baseline.pixels == input.pixels, "Real transport bypass differs from input");
        auto baseline_path = output_path; baseline_path += L".bypass.png";
        Save(baseline_path, baseline); Save(output_path, processed);
        if (stereo) {
            auto left_path = output_path; left_path += L".left.png"; Save(left_path, eye_outputs[0]);
            auto right_path = output_path; right_path += L".right.png"; Save(right_path, eye_outputs[1]);
        }
        nlohmann::json summary; std::ifstream(host_path.parent_path() / "phoenix-host-output-summary.json") >> summary;
        Require(summary.at("input_provider") == "feeder_d3d12" &&
                summary.at("feeder_calls").get<unsigned>() >= 1 + (frames - 1) * iterations,
                "Direct feeder export was not exercised");
        Require(summary.at("completed_frames").get<unsigned>() == 1 + (frames - 1) * iterations &&
                summary.at("worker_execution").at("kind") == "native" &&
                summary.at("direct_game").at("status") == "Own network output written back to the game DLSS output",
                "Native production completion/writeback proof absent");
        uint64_t changed = 0, absolute = 0;
        for (size_t i = 0; i < input.pixels.size(); ++i) {
            const int difference = int(processed.pixels[i]) - int(baseline.pixels[i]);
            changed += difference != 0; absolute += unsigned(difference < 0 ? -difference : difference);
        }
        Require(changed != 0, "Processed transport image is unchanged");
        nlohmann::json report{{"transport", cpu ? "cpu_rows" : "d3d12_shared_textures_and_fences"},
            {"eye_count", stereo ? 2 : 1},
            {"protocol", FEED_IPC_VERSION}, {"frames", frames}, {"iteration_schedule", schedule}, {"host_pid", host.pid},
            {"input_provider", summary.at("input_provider")}, {"feeder_calls", summary.at("feeder_calls")}, {"super_resolution", false},
            {"input_format", build.color_fmt}, {"output_format", build.output_fmt},
            {"bypass_exact", true}, {"changed_channels", changed}, {"absolute_sum", absolute},
            {"completed_frames", summary.at("completed_frames")}, {"routing", summary.at("phoenix_routing")},
            {"worker", summary.at("worker_execution")}, {"game_run", false}};
        if (stereo) {
            report["feeder_stream_recorded"] = summary.at("feeder_stream_recorded");
            report["feeder_stream_resets"] = summary.at("feeder_stream_resets");
            report["per_eye_reset_once"] = true;
            report["frame_trace"] = frame_trace;
        }
        std::ofstream(output_path.parent_path() / "transport-probe.json") << report.dump(2);
        CloseHandle(pipe.value); pipe.value = nullptr; host.Finish();
        std::cout << report.dump(2) << '\n';
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
