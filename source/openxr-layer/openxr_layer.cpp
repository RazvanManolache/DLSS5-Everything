// DLSS5 Everything OpenXR API layer.
//
// Intercepts D3D11 OpenXR headset swapchains and feeds each eye through the
// shared DLSS5 host. The layer is deliberately fail-open: if a frame takes too
// long it disables processing and lets the VR app continue normally.

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define XR_USE_PLATFORM_WIN32
#define XR_USE_GRAPHICS_API_D3D11

#include <windows.h>
#include <d3d11.h>
#include <d3d11_4.h>
#include <dxgi1_2.h>

#include <algorithm>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <condition_variable>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>
#include <openxr/openxr_loader_negotiation.h>

#include "../src/feed_ipc.h"

extern "C" IMAGE_DOS_HEADER __ImageBase;

namespace
{
constexpr char kLayerName[] = "XR_APILAYER_DLSS5_everything";
constexpr int kDefaultOpenXrProcessedFrameMaxAgeMs = 250;

std::string Narrow(const wchar_t *value);
void Log(const char *fmt, ...);

int EnvInt(const wchar_t *name, int fallback, int min_value, int max_value)
{
    wchar_t value[64] = {};
    DWORD n = GetEnvironmentVariableW(name, value, static_cast<DWORD>(std::size(value)));
    if (n == 0 || n >= std::size(value))
        return fallback;

    wchar_t *end = nullptr;
    long parsed = std::wcstol(value, &end, 10);
    if (end == value)
        return fallback;

    parsed = std::max<long>(min_value, std::min<long>(max_value, parsed));
    return static_cast<int>(parsed);
}

bool EnvFlag(const wchar_t *name, bool fallback)
{
    wchar_t value[32] = {};
    DWORD n = GetEnvironmentVariableW(name, value, static_cast<DWORD>(std::size(value)));
    if (n == 0 || n >= std::size(value))
        return fallback;

    return _wcsicmp(value, L"0") != 0 &&
           _wcsicmp(value, L"false") != 0 &&
           _wcsicmp(value, L"off") != 0 &&
           _wcsicmp(value, L"no") != 0;
}

struct InstanceDispatch
{
    PFN_xrGetInstanceProcAddr next_gipa = nullptr;
    PFN_xrDestroyInstance destroy_instance = nullptr;
    PFN_xrCreateSession create_session = nullptr;
    PFN_xrDestroySession destroy_session = nullptr;
    PFN_xrCreateSwapchain create_swapchain = nullptr;
    PFN_xrDestroySwapchain destroy_swapchain = nullptr;
    PFN_xrEnumerateSwapchainImages enumerate_swapchain_images = nullptr;
    PFN_xrAcquireSwapchainImage acquire_swapchain_image = nullptr;
    PFN_xrWaitSwapchainImage wait_swapchain_image = nullptr;
    PFN_xrReleaseSwapchainImage release_swapchain_image = nullptr;
    PFN_xrEndFrame end_frame = nullptr;
};

struct SessionState
{
    XrInstance instance = XR_NULL_HANDLE;
    std::string graphics_api = "unknown";
    ID3D11Device *d3d11_device = nullptr;
};

struct SwapchainState
{
    XrSession session = XR_NULL_HANDLE;
    uint32_t width = 0;
    uint32_t height = 0;
    uint32_t array_size = 0;
    uint32_t face_count = 0;
    uint32_t mip_count = 0;
    uint32_t sample_count = 0;
    int64_t format = 0;
    XrSwapchainUsageFlags usage_flags = 0;
    std::vector<ID3D11Texture2D *> d3d11_images;
    uint32_t active_index = UINT32_MAX;
    uint64_t frame_count = 0;
};

template <typename T>
void SafeRelease(T *&value)
{
    if (value != nullptr)
    {
        value->Release();
        value = nullptr;
    }
}

DXGI_FORMAT TypedColorFormat(DXGI_FORMAT fmt)
{
    switch (fmt)
    {
    case DXGI_FORMAT_R8G8B8A8_TYPELESS:
    case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
        return DXGI_FORMAT_R8G8B8A8_UNORM;
    case DXGI_FORMAT_B8G8R8A8_TYPELESS:
    case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
        return DXGI_FORMAT_B8G8R8A8_UNORM;
    case DXGI_FORMAT_R10G10B10A2_TYPELESS:
        return DXGI_FORMAT_R10G10B10A2_UNORM;
    case DXGI_FORMAT_R16G16B16A16_TYPELESS:
        return DXGI_FORMAT_R16G16B16A16_FLOAT;
    default:
        return fmt;
    }
}

struct OpenXrBridge
{
    ID3D11Device *dev = nullptr;
    ID3D11DeviceContext *ctx = nullptr;
    ID3D11DeviceContext4 *ctx4 = nullptr;
    ID3D11Multithread *multithread = nullptr;
    ID3D11Texture2D *tex[FEED_SLOTS] = {};
    ID3D11RenderTargetView *rtv[FEED_SLOTS] = {};
    ID3D11Texture2D *capture_stereo = nullptr;
    ID3D11Texture2D *processed_stereo = nullptr;
    HANDLE tex_handle[FEED_SLOTS] = {};
    ID3D11Fence *fence_in = nullptr;
    ID3D11Fence *fence_out = nullptr;
    HANDLE fence_event = nullptr;
    HANDLE pipe = nullptr;
    HANDLE host_process = nullptr;
    XrSession xr_session = XR_NULL_HANDLE;
    XrSwapchain source_swapchain = XR_NULL_HANDLE;
    XrSwapchain output_swapchain = XR_NULL_HANDLE;
    PFN_xrCreateSwapchain xr_create_swapchain = nullptr;
    PFN_xrDestroySwapchain xr_destroy_swapchain = nullptr;
    PFN_xrEnumerateSwapchainImages xr_enumerate_swapchain_images = nullptr;
    PFN_xrAcquireSwapchainImage xr_acquire_swapchain_image = nullptr;
    PFN_xrWaitSwapchainImage xr_wait_swapchain_image = nullptr;
    PFN_xrReleaseSwapchainImage xr_release_swapchain_image = nullptr;
    std::vector<XrSwapchainImageD3D11KHR> output_image_headers;
    std::vector<ID3D11Texture2D *> output_images;
    std::vector<uint64_t> output_image_frames;
    uint32_t width = 0;
    uint32_t height = 0;
    int64_t xr_format = 0;
    DXGI_FORMAT color_fmt = DXGI_FORMAT_UNKNOWN;
    uint64_t frame_n = 0;
    uint64_t frames_done = 0;
    bool settings_loaded = false;
    bool enabled = true;
    bool disabled_slow = false;
    int process_every = 120;
    int warmup_releases = 90;
    int max_block_ms = 1500;
    int process_timeout_ms = 5000;
    int max_display_age_ms = kDefaultOpenXrProcessedFrameMaxAgeMs;
    std::mutex state_mutex;
    std::mutex d3d_mutex;
    std::condition_variable state_cv;
    std::thread worker;
    bool worker_started = false;
    bool worker_stop = false;
    bool worker_busy = false;
    bool work_pending = false;
    bool processed_ready = false;
    uint64_t captured_frame = 0;
    uint64_t processed_frame = 0;
    ULONGLONG processed_ready_tick = 0;

    void StopWorker()
    {
        {
            std::lock_guard<std::mutex> lock(state_mutex);
            worker_stop = true;
            work_pending = false;
        }
        state_cv.notify_all();
        if (worker.joinable())
            worker.join();
        worker_started = false;
        worker_stop = false;
        worker_busy = false;
        processed_ready = false;
        processed_ready_tick = 0;
    }

    void DestroyOutputSwapchain()
    {
        for (auto *texture : output_images)
            if (texture != nullptr)
                texture->Release();
        output_images.clear();
        output_image_headers.clear();
        output_image_frames.clear();

        if (output_swapchain != XR_NULL_HANDLE && xr_destroy_swapchain != nullptr)
            xr_destroy_swapchain(output_swapchain);
        output_swapchain = XR_NULL_HANDLE;
    }

    void ReleaseShared()
    {
        StopWorker();
        DestroyOutputSwapchain();
        for (int i = 0; i < FEED_SLOTS; ++i)
        {
            SafeRelease(rtv[i]);
            SafeRelease(tex[i]);
            if (tex_handle[i] != nullptr)
            {
                CloseHandle(tex_handle[i]);
                tex_handle[i] = nullptr;
            }
        }
        SafeRelease(capture_stereo);
        SafeRelease(processed_stereo);
        SafeRelease(fence_in);
        SafeRelease(fence_out);
        if (fence_event != nullptr)
        {
            CloseHandle(fence_event);
            fence_event = nullptr;
        }
        xr_session = XR_NULL_HANDLE;
        source_swapchain = XR_NULL_HANDLE;
        xr_create_swapchain = nullptr;
        xr_destroy_swapchain = nullptr;
        xr_enumerate_swapchain_images = nullptr;
        xr_acquire_swapchain_image = nullptr;
        xr_wait_swapchain_image = nullptr;
        xr_release_swapchain_image = nullptr;
        width = 0;
        height = 0;
        xr_format = 0;
        color_fmt = DXGI_FORMAT_UNKNOWN;
        frame_n = 0;
        frames_done = 0;
        disabled_slow = false;
        work_pending = false;
        processed_ready = false;
        captured_frame = 0;
        processed_frame = 0;
        processed_ready_tick = 0;
    }

    void CloseHost()
    {
        if (pipe != nullptr)
        {
            CloseHandle(pipe);
            pipe = nullptr;
        }
        if (host_process != nullptr)
        {
            CloseHandle(host_process);
            host_process = nullptr;
        }
    }

    void Reset()
    {
        ReleaseShared();
        CloseHost();
        SafeRelease(ctx4);
        SafeRelease(ctx);
        SafeRelease(multithread);
        SafeRelease(dev);
    }

    bool HostAlive() const
    {
        if (host_process == nullptr)
            return false;
        DWORD code = 0;
        return GetExitCodeProcess(host_process, &code) && code == STILL_ACTIVE;
    }

    void LoadSettings()
    {
        if (settings_loaded)
            return;

        settings_loaded = true;
        enabled = EnvFlag(L"DLSS5_OPENXR_ENABLED", true);
        process_every = EnvInt(L"DLSS5_OPENXR_PROCESS_EVERY", 1, 1, 1000000);
        warmup_releases = EnvInt(L"DLSS5_OPENXR_WARMUP_RELEASES", 60, 0, 1000000);
        max_block_ms = EnvInt(L"DLSS5_OPENXR_MAX_BLOCK_MS", 1500, 0, 60000);
        process_timeout_ms = EnvInt(L"DLSS5_OPENXR_PROCESS_TIMEOUT_MS", 5000, 250, 60000);
        max_display_age_ms = EnvInt(L"DLSS5_OPENXR_MAX_DISPLAY_AGE_MS", kDefaultOpenXrProcessedFrameMaxAgeMs, 16, 2000);
        Log("OpenXR bridge settings: enabled=%d warmup=%d every=%d max_block_ms=%d process_timeout_ms=%d max_display_age_ms=%d",
            enabled ? 1 : 0, warmup_releases, process_every, max_block_ms, process_timeout_ms, max_display_age_ms);
    }

    bool ShouldProcess(uint64_t release_frame)
    {
        LoadSettings();
        if (!enabled || disabled_slow)
            return false;
        if (release_frame <= static_cast<uint64_t>(warmup_releases))
            return false;
        return ((release_frame - static_cast<uint64_t>(warmup_releases) - 1) %
                static_cast<uint64_t>(process_every)) == 0;
    }

    bool PipeWrite(const void *data, DWORD bytes)
    {
        DWORD done = 0;
        return pipe != nullptr && WriteFile(pipe, data, bytes, &done, nullptr) && done == bytes;
    }

    bool PipeRead(void *data, DWORD bytes)
    {
        DWORD done = 0;
        return pipe != nullptr && ReadFile(pipe, data, bytes, &done, nullptr) && done == bytes;
    }

    std::wstring ModuleDirectory() const
    {
        wchar_t path[MAX_PATH] = {};
        GetModuleFileNameW(reinterpret_cast<HMODULE>(&__ImageBase), path, static_cast<DWORD>(std::size(path)));
        std::wstring dir(path);
        const auto slash = dir.find_last_of(L"\\/");
        if (slash != std::wstring::npos)
            dir.resize(slash + 1);
        return dir;
    }

    bool EnsureHost()
    {
        if (pipe != nullptr && HostAlive())
            return true;
        CloseHost();

        const std::wstring dir = ModuleDirectory();
        const std::wstring host_dir = dir + L"..\\host64";
        const std::wstring exe = host_dir + L"\\dlss5-feed-host64.exe";
        if (GetFileAttributesW(exe.c_str()) == INVALID_FILE_ATTRIBUTES)
        {
            Log("OpenXR bridge host missing: %s", Narrow(exe.c_str()).c_str());
            return false;
        }

        wchar_t cmd[1024] = {};
        swprintf_s(cmd, L"\"%s\" %lu --hide", exe.c_str(), GetCurrentProcessId());
        STARTUPINFOW si = { sizeof(si) };
        PROCESS_INFORMATION pi = {};
        if (!CreateProcessW(nullptr, cmd, nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, host_dir.c_str(), &si, &pi))
        {
            Log("OpenXR bridge CreateProcess failed %lu", GetLastError());
            return false;
        }
        CloseHandle(pi.hThread);
        host_process = pi.hProcess;
        Log("OpenXR bridge host spawned pid=%lu", pi.dwProcessId);

        char pipe_name[128] = {};
        sprintf_s(pipe_name, FEED_PIPE_FMT, static_cast<unsigned long>(GetCurrentProcessId()));
        for (int i = 0; i < 150 && pipe == nullptr; ++i)
        {
            HANDLE p = CreateFileA(pipe_name, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
            if (p != INVALID_HANDLE_VALUE)
            {
                pipe = p;
                break;
            }
            if (!HostAlive())
            {
                Log("OpenXR bridge host exited during startup");
                return false;
            }
            Sleep(100);
        }

        if (pipe == nullptr)
        {
            Log("OpenXR bridge pipe never appeared");
            return false;
        }

        FeedHello hello = { FEED_IPC_MAGIC, FEED_IPC_VERSION, GetCurrentProcessId() };
        FeedHelloAck ack = {};
        if (!PipeWrite(&hello, sizeof(hello)) || !PipeRead(&ack, sizeof(ack)) ||
            ack.magic != FEED_IPC_MAGIC || ack.version != FEED_IPC_VERSION)
        {
            Log("OpenXR bridge handshake failed");
            CloseHost();
            return false;
        }

        Log("OpenXR bridge host connected protocol=%u", ack.version);
        return true;
    }

    bool MakeShared(int slot, uint32_t w, uint32_t h, DXGI_FORMAT fmt, bool uav, bool make_rtv)
    {
        D3D11_TEXTURE2D_DESC td = {};
        td.Width = w;
        td.Height = h;
        td.MipLevels = 1;
        td.ArraySize = 1;
        td.Format = fmt;
        td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_DEFAULT;
        td.BindFlags = D3D11_BIND_SHADER_RESOURCE | (uav ? D3D11_BIND_UNORDERED_ACCESS : 0) |
                       (make_rtv ? D3D11_BIND_RENDER_TARGET : 0);
        td.MiscFlags = D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_SHARED;
        HRESULT hr = dev->CreateTexture2D(&td, nullptr, &tex[slot]);
        if (FAILED(hr))
        {
            Log("OpenXR bridge texture slot=%d create failed hr=0x%08X", slot, hr);
            return false;
        }

        IDXGIResource1 *resource = nullptr;
        hr = tex[slot]->QueryInterface(__uuidof(IDXGIResource1), reinterpret_cast<void **>(&resource));
        if (SUCCEEDED(hr))
        {
            hr = resource->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr, &tex_handle[slot]);
            resource->Release();
        }
        if (FAILED(hr))
        {
            Log("OpenXR bridge texture slot=%d shared handle failed hr=0x%08X", slot, hr);
            return false;
        }

        if (make_rtv)
        {
            hr = dev->CreateRenderTargetView(tex[slot], nullptr, &rtv[slot]);
            if (FAILED(hr))
            {
                Log("OpenXR bridge texture slot=%d RTV failed hr=0x%08X", slot, hr);
                return false;
            }
        }
        return true;
    }

    bool Build(ID3D11Device *device, uint32_t w, uint32_t h, DXGI_FORMAT swap_fmt)
    {
        if (device == nullptr || w == 0 || h == 0)
            return false;

        const DXGI_FORMAT fmt = TypedColorFormat(swap_fmt);
        if (dev == device && width == w && height == h && color_fmt == fmt && pipe != nullptr && HostAlive())
            return true;

        Reset();
        dev = device;
        dev->AddRef();
        if (SUCCEEDED(dev->QueryInterface(__uuidof(ID3D11Multithread), reinterpret_cast<void **>(&multithread))) && multithread != nullptr)
            multithread->SetMultithreadProtected(TRUE);
        dev->GetImmediateContext(&ctx);
        if (ctx == nullptr)
        {
            Log("OpenXR bridge immediate context unavailable");
            return false;
        }
        HRESULT hr = ctx->QueryInterface(__uuidof(ID3D11DeviceContext4), reinterpret_cast<void **>(&ctx4));
        if (FAILED(hr) || ctx4 == nullptr)
        {
            Log("OpenXR bridge ID3D11DeviceContext4 unavailable hr=0x%08X", hr);
            return false;
        }

        width = w;
        height = h;
        color_fmt = fmt;

        D3D11_TEXTURE2D_DESC stereo = {};
        stereo.Width = w;
        stereo.Height = h;
        stereo.MipLevels = 1;
        stereo.ArraySize = 2;
        stereo.Format = fmt;
        stereo.SampleDesc.Count = 1;
        stereo.Usage = D3D11_USAGE_DEFAULT;
        stereo.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
        hr = dev->CreateTexture2D(&stereo, nullptr, &capture_stereo);
        if (FAILED(hr))
        {
            Log("OpenXR bridge capture stereo texture failed hr=0x%08X", hr);
            ReleaseShared();
            return false;
        }
        hr = dev->CreateTexture2D(&stereo, nullptr, &processed_stereo);
        if (FAILED(hr))
        {
            Log("OpenXR bridge processed stereo texture failed hr=0x%08X", hr);
            ReleaseShared();
            return false;
        }

        if (!MakeShared(FEED_COLOR, w, h, fmt, false, false) ||
            !MakeShared(FEED_OUTPUT, w, h, fmt, true, false) ||
            !MakeShared(FEED_DEPTH, w, h, DXGI_FORMAT_R32_FLOAT, false, true) ||
            !MakeShared(FEED_MV, w, h, DXGI_FORMAT_R16G16_FLOAT, false, true) ||
            !MakeShared(FEED_MASK, w, h, DXGI_FORMAT_R8_UNORM, false, true))
        {
            ReleaseShared();
            return false;
        }

        if (!EnsureHost())
            return false;

        FeedBuild build = {};
        build.width = w;
        build.height = h;
        build.output_width = w;
        build.output_height = h;
        build.color_fmt = fmt;
        build.output_fmt = fmt;
        build.hdr = 0;
        build.depth_inverted = 1;
        build.flags_override = -1;
        build.transport = 0;
        build.has_mask = 1;
        build.cpu_bridge = 0;
        build.mv_scale_x = 1.0f;
        build.mv_scale_y = 1.0f;
        build.game_side_vr_split = 1;
        build.present_width = w;
        build.present_height = h;
        for (int i = 0; i < FEED_SLOTS; ++i)
            build.tex[i] = reinterpret_cast<uintptr_t>(tex_handle[i]);

        BYTE tag = 'B';
        FeedBuildAck ack = {};
        if (!PipeWrite(&tag, 1) || !PipeWrite(&build, sizeof(build)) || !PipeRead(&ack, sizeof(ack)) || !ack.ok)
        {
            Log("OpenXR bridge build exchange failed ok=%d ngx=0x%08X", ack.ok, ack.ngx_result);
            CloseHost();
            return false;
        }

        ID3D11Device5 *dev5 = nullptr;
        hr = dev->QueryInterface(__uuidof(ID3D11Device5), reinterpret_cast<void **>(&dev5));
        if (FAILED(hr) || dev5 == nullptr)
        {
            Log("OpenXR bridge ID3D11Device5 unavailable hr=0x%08X", hr);
            return false;
        }
        HRESULT h1 = dev5->OpenSharedFence(reinterpret_cast<HANDLE>(static_cast<uintptr_t>(ack.fence_in)), __uuidof(ID3D11Fence), reinterpret_cast<void **>(&fence_in));
        HRESULT h2 = dev5->OpenSharedFence(reinterpret_cast<HANDLE>(static_cast<uintptr_t>(ack.fence_out)), __uuidof(ID3D11Fence), reinterpret_cast<void **>(&fence_out));
        dev5->Release();
        if (FAILED(h1) || FAILED(h2))
        {
            Log("OpenXR bridge OpenSharedFence failed 0x%08X/0x%08X", h1, h2);
            return false;
        }
        fence_event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (fence_event == nullptr)
        {
            Log("OpenXR bridge fence event create failed %lu", GetLastError());
            return false;
        }

        Log("OpenXR bridge ready: per-eye %ux%u fmt=%u ngx=0x%08X", w, h, fmt, ack.ngx_result);
        return true;
    }

    bool ProcessEye(ID3D11Texture2D *source, ID3D11Texture2D *destination, uint32_t mip_levels, uint32_t image_index, uint32_t eye)
    {
        if (source == nullptr || destination == nullptr || ctx == nullptr || ctx4 == nullptr || pipe == nullptr ||
            eye >= 2 || width == 0 || height == 0 || fence_event == nullptr)
        {
            return false;
        }

        const UINT subresource = D3D11CalcSubresource(0, eye, mip_levels);
        uint64_t n = 0;
        {
            std::lock_guard<std::mutex> gpu_lock(d3d_mutex);
            ctx->CopySubresourceRegion(tex[FEED_COLOR], 0, 0, 0, 0, source, subresource, nullptr);

            const FLOAT depth[4] = { 1.0f, 0.0f, 0.0f, 0.0f };
            const FLOAT zero[4] = {};
            if (rtv[FEED_DEPTH] != nullptr) ctx->ClearRenderTargetView(rtv[FEED_DEPTH], depth);
            if (rtv[FEED_MV] != nullptr) ctx->ClearRenderTargetView(rtv[FEED_MV], zero);
            if (rtv[FEED_MASK] != nullptr) ctx->ClearRenderTargetView(rtv[FEED_MASK], zero);

            n = ++frame_n;
            ctx4->Signal(fence_in, n);
            ctx->Flush();
        }

        BYTE tag = 'F';
        FeedFrameMsg frame = {};
        frame.n = n;
        frame.reset = frames_done < 2 ? 1u : 0u;
        frame.nr_enabled = 1;
        frame.iterations = 1;
        frame.eye_index = eye;
        if (!PipeWrite(&tag, 1) || !PipeWrite(&frame, sizeof(frame)))
        {
            Log("OpenXR bridge frame message failed");
            CloseHost();
            return false;
        }

        HRESULT wait_hr = fence_out->SetEventOnCompletion(n, fence_event);
        if (FAILED(wait_hr) || WaitForSingleObject(fence_event, static_cast<DWORD>(process_timeout_ms)) != WAIT_OBJECT_0)
        {
            Log("OpenXR bridge timed out waiting for host frame=%llu eye=%u hr=0x%08X",
                static_cast<unsigned long long>(n), eye, wait_hr);
            disabled_slow = true;
            return false;
        }

        {
            std::lock_guard<std::mutex> gpu_lock(d3d_mutex);
            ctx->CopySubresourceRegion(destination, subresource, 0, 0, 0, tex[FEED_OUTPUT], 0, nullptr);
            ctx->Flush();
        }

        ++frames_done;
        if (frames_done <= 6 || (frames_done % 900) == 0)
            Log("OpenXR bridge frame=%llu image=%u eye=%u processed %ux%u", static_cast<unsigned long long>(frames_done),
                image_index, eye, width, height);
        return true;
    }

    bool ProcessStereo(ID3D11Device *device, ID3D11Texture2D *swapchain_image, const D3D11_TEXTURE2D_DESC &desc,
                       uint32_t image_index)
    {
        const ULONGLONG start = GetTickCount64();
        if (!Build(device, desc.Width, desc.Height, desc.Format))
            return false;

        bool ok = ProcessEye(swapchain_image, processed_stereo, desc.MipLevels, image_index, 0);
        ok = ProcessEye(swapchain_image, processed_stereo, desc.MipLevels, image_index, 1) && ok;

        const ULONGLONG elapsed = GetTickCount64() - start;
        if (max_block_ms > 0 && elapsed > static_cast<ULONGLONG>(max_block_ms))
        {
            disabled_slow = true;
            Log("OpenXR bridge disabled after slow stereo pass: %llums over %dms budget",
                static_cast<unsigned long long>(elapsed), max_block_ms);
        }
        else if (frames_done <= 6 || (frames_done % 900) == 0)
        {
            Log("OpenXR bridge stereo pass took %llums", static_cast<unsigned long long>(elapsed));
        }

        return ok;
    }

    bool EnsureOutputSwapchain(const InstanceDispatch &dispatch, XrSession session, XrSwapchain source, const SwapchainState &state)
    {
        if (output_swapchain != XR_NULL_HANDLE && xr_session == session && source_swapchain == source &&
            width == state.width && height == state.height && xr_format == state.format)
            return true;

        DestroyOutputSwapchain();
        xr_session = session;
        source_swapchain = source;
        xr_format = state.format;
        xr_create_swapchain = dispatch.create_swapchain;
        xr_destroy_swapchain = dispatch.destroy_swapchain;
        xr_enumerate_swapchain_images = dispatch.enumerate_swapchain_images;
        xr_acquire_swapchain_image = dispatch.acquire_swapchain_image;
        xr_wait_swapchain_image = dispatch.wait_swapchain_image;
        xr_release_swapchain_image = dispatch.release_swapchain_image;

        if (xr_create_swapchain == nullptr || xr_enumerate_swapchain_images == nullptr ||
            xr_acquire_swapchain_image == nullptr || xr_wait_swapchain_image == nullptr ||
            xr_release_swapchain_image == nullptr)
        {
            Log("OpenXR bridge cannot create output swapchain: incomplete dispatch");
            return false;
        }

        XrSwapchainCreateInfo create = { XR_TYPE_SWAPCHAIN_CREATE_INFO };
        create.usageFlags = state.usage_flags;
        create.format = state.format;
        create.sampleCount = state.sample_count;
        create.width = state.width;
        create.height = state.height;
        create.faceCount = state.face_count;
        create.arraySize = state.array_size;
        create.mipCount = state.mip_count;

        XrResult result = xr_create_swapchain(session, &create, &output_swapchain);
        if (XR_FAILED(result))
        {
            Log("OpenXR bridge output swapchain create failed result=%d", result);
            output_swapchain = XR_NULL_HANDLE;
            return false;
        }

        uint32_t count = 0;
        result = xr_enumerate_swapchain_images(output_swapchain, 0, &count, nullptr);
        if (XR_FAILED(result) || count == 0)
        {
            Log("OpenXR bridge output image count failed result=%d count=%u", result, count);
            DestroyOutputSwapchain();
            return false;
        }

        output_image_headers.assign(count, XrSwapchainImageD3D11KHR{ XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR });
        result = xr_enumerate_swapchain_images(output_swapchain, count, &count,
            reinterpret_cast<XrSwapchainImageBaseHeader *>(output_image_headers.data()));
        if (XR_FAILED(result))
        {
            Log("OpenXR bridge output image enumerate failed result=%d", result);
            DestroyOutputSwapchain();
            return false;
        }

        output_images.clear();
        output_image_frames.clear();
        for (uint32_t i = 0; i < count; ++i)
        {
            ID3D11Texture2D *texture = output_image_headers[i].texture;
            if (texture != nullptr)
                texture->AddRef();
            output_images.push_back(texture);
            output_image_frames.push_back(0);
        }

        Log("OpenXR bridge output swapchain ready=%p images=%zu %ux%u array=%u",
            reinterpret_cast<void *>(output_swapchain), output_images.size(), state.width, state.height, state.array_size);
        return true;
    }

    bool Prepare(const InstanceDispatch &dispatch, XrSession session, XrSwapchain source, ID3D11Device *device,
                 const SwapchainState &state, DXGI_FORMAT swap_fmt)
    {
        LoadSettings();
        if (!enabled || disabled_slow)
            return false;
        if (state.array_size < 2 || state.width < 1024 || state.height < 1024)
            return false;
        if (!Build(device, state.width, state.height, swap_fmt))
            return false;
        if (!EnsureOutputSwapchain(dispatch, session, source, state))
            return false;
        StartWorker();
        return true;
    }

    void StartWorker()
    {
        if (worker_started)
            return;

        worker_stop = false;
        worker = std::thread([this]() { WorkerMain(); });
        worker_started = true;
        Log("OpenXR bridge worker started");
    }

    void WorkerMain()
    {
        for (;;)
        {
            uint64_t frame = 0;
            {
                std::unique_lock<std::mutex> lock(state_mutex);
                state_cv.wait(lock, [this]() { return worker_stop || work_pending; });
                if (worker_stop)
                    return;
                work_pending = false;
                worker_busy = true;
                frame = captured_frame;
            }

            const ULONGLONG start = GetTickCount64();
            bool ok = false;
            ok = ProcessEye(capture_stereo, processed_stereo, 1, 0, 0);
            ok = ProcessEye(capture_stereo, processed_stereo, 1, 0, 1) && ok;
            const ULONGLONG elapsed = GetTickCount64() - start;

            {
                std::lock_guard<std::mutex> lock(state_mutex);
                worker_busy = false;
                if (ok)
                {
                    processed_frame = frame;
                    processed_ready = true;
                    processed_ready_tick = GetTickCount64();
                }
            }

            static uint64_t worker_log_count = 0;
            if (++worker_log_count <= 20 || (worker_log_count % 120) == 0 || !ok)
                Log("OpenXR bridge worker frame=%llu ok=%d elapsed=%llums",
                    static_cast<unsigned long long>(frame), ok ? 1 : 0, static_cast<unsigned long long>(elapsed));
            if (max_block_ms > 0 && elapsed > static_cast<ULONGLONG>(max_block_ms))
                Log("OpenXR bridge worker is behind VR budget but render thread was not blocked");
        }
    }

    bool CaptureStereo(const InstanceDispatch &dispatch, XrSession session, XrSwapchain source, ID3D11Device *device,
                       ID3D11Texture2D *swapchain_image, const D3D11_TEXTURE2D_DESC &desc, const SwapchainState &state,
                       uint64_t release_frame)
    {
        if (!ShouldProcess(release_frame))
            return false;
        if (!Prepare(dispatch, session, source, device, state, desc.Format))
            return false;

        {
            std::lock_guard<std::mutex> lock(state_mutex);
            if (processed_ready && processed_ready_tick != 0 &&
                GetTickCount64() - processed_ready_tick > static_cast<ULONGLONG>(max_display_age_ms))
            {
                static uint64_t capture_stale_log_count = 0;
                if (++capture_stale_log_count <= 10 || (capture_stale_log_count % 120) == 0)
                    Log("OpenXR bridge dropped stale processed frame=%llu before capture",
                        static_cast<unsigned long long>(processed_frame));
                processed_ready = false;
                processed_ready_tick = 0;
            }

            if (worker_busy || work_pending)
            {
                if (release_frame <= 30 || release_frame % 300 == 0)
                    Log("OpenXR bridge skipped capture frame=%llu busy=%d pending=%d ready=%d",
                        static_cast<unsigned long long>(release_frame),
                        worker_busy ? 1 : 0, work_pending ? 1 : 0, processed_ready ? 1 : 0);
                return false;
            }
        }

        {
            std::lock_guard<std::mutex> gpu_lock(d3d_mutex);
            for (uint32_t eye = 0; eye < 2; ++eye)
            {
                const UINT src = D3D11CalcSubresource(0, eye, desc.MipLevels);
                const UINT dst = D3D11CalcSubresource(0, eye, 1);
                ctx->CopySubresourceRegion(capture_stereo, dst, 0, 0, 0, swapchain_image, src, nullptr);
            }
            ctx->Flush();
        }

        {
            std::lock_guard<std::mutex> lock(state_mutex);
            captured_frame = release_frame;
            work_pending = true;
        }
        state_cv.notify_one();

        if (release_frame <= 6 || release_frame % 900 == 0)
            Log("OpenXR bridge captured stereo frame=%llu", static_cast<unsigned long long>(release_frame));
        return true;
    }

    void Snapshot(bool &busy, bool &pending, bool &ready, uint64_t &captured, uint64_t &processed)
    {
        std::lock_guard<std::mutex> lock(state_mutex);
        busy = worker_busy;
        pending = work_pending;
        ready = processed_ready;
        captured = captured_frame;
        processed = processed_frame;
    }

    bool ReadyForSubmit(uint64_t &frame, ULONGLONG &age_ms)
    {
        std::lock_guard<std::mutex> lock(state_mutex);
        frame = processed_frame;
        age_ms = processed_ready && processed_ready_tick != 0 ? GetTickCount64() - processed_ready_tick : 0;
        return processed_ready;
    }

    bool DropProcessedIfStale(const char *reason)
    {
        uint64_t frame = 0;
        ULONGLONG age = 0;
        {
            std::lock_guard<std::mutex> lock(state_mutex);
            if (!processed_ready || processed_ready_tick == 0)
                return false;
            age = GetTickCount64() - processed_ready_tick;
            if (age <= static_cast<ULONGLONG>(max_display_age_ms))
                return false;
            frame = processed_frame;
            processed_ready = false;
            processed_ready_tick = 0;
        }
        static uint64_t stale_log_count = 0;
        if (++stale_log_count <= 10 || (stale_log_count % 120) == 0)
            Log("OpenXR bridge dropped stale processed frame=%llu age=%llums reason=%s",
                static_cast<unsigned long long>(frame), static_cast<unsigned long long>(age),
                reason != nullptr ? reason : "unknown");
        return true;
    }

    bool CopyProcessedToOpenXrSwapchain(uint64_t &frame)
    {
        {
            std::lock_guard<std::mutex> lock(state_mutex);
            if (!processed_ready || output_swapchain == XR_NULL_HANDLE || output_images.empty())
                return false;
            frame = processed_frame;
        }

        uint32_t index = UINT32_MAX;
        XrSwapchainImageAcquireInfo acquire = { XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO };
        XrResult result = xr_acquire_swapchain_image(output_swapchain, &acquire, &index);
        if (XR_FAILED(result) || index >= output_images.size() || output_images[index] == nullptr)
        {
            Log("OpenXR bridge output acquire failed result=%d index=%u", result, index);
            std::lock_guard<std::mutex> lock(state_mutex);
            processed_ready = false;
            processed_ready_tick = 0;
            return false;
        }

        XrSwapchainImageWaitInfo wait = { XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO };
        wait.timeout = 2 * 1000 * 1000; // 2ms: never stall the VR submit path for output handoff.
        result = xr_wait_swapchain_image(output_swapchain, &wait);
        if (XR_FAILED(result))
        {
            Log("OpenXR bridge output wait failed result=%d", result);
            XrSwapchainImageReleaseInfo release_after_failed_wait = { XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO };
            xr_release_swapchain_image(output_swapchain, &release_after_failed_wait);
            std::lock_guard<std::mutex> lock(state_mutex);
            processed_ready = false;
            processed_ready_tick = 0;
            return false;
        }

        if (index >= output_image_frames.size())
            output_image_frames.resize(output_images.size(), 0);

        if (output_image_frames[index] != frame)
        {
            std::lock_guard<std::mutex> gpu_lock(d3d_mutex);
            D3D11_TEXTURE2D_DESC out_desc = {};
            output_images[index]->GetDesc(&out_desc);
            for (uint32_t eye = 0; eye < 2 && eye < out_desc.ArraySize; ++eye)
            {
                const UINT src = D3D11CalcSubresource(0, eye, 1);
                const UINT dst = D3D11CalcSubresource(0, eye, out_desc.MipLevels);
                ctx->CopySubresourceRegion(output_images[index], dst, 0, 0, 0, processed_stereo, src, nullptr);
            }
            ctx->Flush();
            output_image_frames[index] = frame;
        }

        XrSwapchainImageReleaseInfo release = { XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO };
        result = xr_release_swapchain_image(output_swapchain, &release);
        if (XR_FAILED(result))
        {
            Log("OpenXR bridge output release failed result=%d", result);
            return false;
        }

        static uint64_t submit_log_count = 0;
        if (++submit_log_count <= 20 || (submit_log_count % 300) == 0)
            Log("OpenXR bridge submitted processed frame=%llu via output image=%u",
                static_cast<unsigned long long>(frame), index);
        return true;
    }

    bool TryEndFrame(XrSession session, const XrFrameEndInfo *frameEndInfo, InstanceDispatch &dispatch, XrResult &result)
    {
        uint64_t ready_frame = 0;
        ULONGLONG ready_age = 0;
        const bool ready = ReadyForSubmit(ready_frame, ready_age);
        if (ready && ready_age > static_cast<ULONGLONG>(max_display_age_ms))
        {
            DropProcessedIfStale("processed frame exceeded display age");
            return false;
        }

        if (session != xr_session || frameEndInfo == nullptr || frameEndInfo->layers == nullptr ||
            output_swapchain == XR_NULL_HANDLE || dispatch.end_frame == nullptr)
        {
            static uint64_t reject_log_count = 0;
            if (ready && (++reject_log_count <= 30 || (reject_log_count % 300) == 0))
            {
                Log("OpenXR bridge cannot patch ready frame=%llu age=%llums: session=%p bridge_session=%p frameEndInfo=%p layers=%p layer_count=%u output=%p end_frame=%p",
                    static_cast<unsigned long long>(ready_frame), static_cast<unsigned long long>(ready_age),
                    reinterpret_cast<void *>(session), reinterpret_cast<void *>(xr_session),
                    reinterpret_cast<const void *>(frameEndInfo),
                    frameEndInfo != nullptr ? reinterpret_cast<const void *>(frameEndInfo->layers) : nullptr,
                    frameEndInfo != nullptr ? frameEndInfo->layerCount : 0,
                    reinterpret_cast<void *>(output_swapchain), reinterpret_cast<void *>(dispatch.end_frame));
            }
            if (ready && ready_age > static_cast<ULONGLONG>(max_display_age_ms))
                DropProcessedIfStale("no projection layer available");
            return false;
        }

        uint64_t frame = 0;
        if (!CopyProcessedToOpenXrSwapchain(frame))
            return false;

        std::vector<const XrCompositionLayerBaseHeader *> layers(frameEndInfo->layerCount);
        std::vector<XrCompositionLayerProjection> projection_copies;
        std::vector<std::vector<XrCompositionLayerProjectionView>> projection_view_copies;
        projection_copies.reserve(frameEndInfo->layerCount);
        projection_view_copies.reserve(frameEndInfo->layerCount);

        bool changed = false;
        for (uint32_t i = 0; i < frameEndInfo->layerCount; ++i)
        {
            const XrCompositionLayerBaseHeader *base = frameEndInfo->layers[i];
            layers[i] = base;
            if (base == nullptr || base->type != XR_TYPE_COMPOSITION_LAYER_PROJECTION)
                continue;

            const auto *projection = reinterpret_cast<const XrCompositionLayerProjection *>(base);
            projection_copies.push_back(*projection);
            projection_view_copies.emplace_back(projection->views, projection->views + projection->viewCount);
            auto &copy = projection_copies.back();
            auto &views = projection_view_copies.back();
            bool projection_changed = false;
            for (auto &view : views)
            {
                if (view.subImage.swapchain != source_swapchain)
                    continue;
                view.subImage.swapchain = output_swapchain;
                view.subImage.imageRect.offset = { 0, 0 };
                view.subImage.imageRect.extent = { static_cast<int32_t>(width), static_cast<int32_t>(height) };
                projection_changed = true;
            }

            if (projection_changed)
            {
                copy.views = views.data();
                layers[i] = reinterpret_cast<const XrCompositionLayerBaseHeader *>(&copy);
                changed = true;
            }
        }

        if (!changed)
            return false;

        XrFrameEndInfo patched = *frameEndInfo;
        patched.layers = layers.data();
        result = dispatch.end_frame(session, &patched);
        static uint64_t patch_log_count = 0;
        if (++patch_log_count <= 20 || (patch_log_count % 300) == 0 || XR_FAILED(result))
            Log("OpenXR bridge patched xrEndFrame with processed frame=%llu result=%d",
                static_cast<unsigned long long>(frame), result);
        return true;
    }
};

std::mutex g_mutex;
std::unordered_map<XrInstance, InstanceDispatch> g_instances;
std::unordered_map<XrSession, SessionState> g_sessions;
std::unordered_map<XrSwapchain, SwapchainState> g_swapchains;
OpenXrBridge g_bridge;
std::mutex g_bridge_mutex;

std::wstring LogPath()
{
    wchar_t dir[MAX_PATH] = {};
    DWORD n = GetEnvironmentVariableW(L"LOCALAPPDATA", dir, static_cast<DWORD>(std::size(dir)));
    std::wstring base = (n > 0 && n < std::size(dir)) ? std::wstring(dir) : L".";
    base += L"\\DLSS5 Everything";
    CreateDirectoryW(base.c_str(), nullptr);
    return base + L"\\openxr-layer.log";
}

std::string Narrow(const wchar_t *value)
{
    if (value == nullptr)
        return {};
    int count = WideCharToMultiByte(CP_UTF8, 0, value, -1, nullptr, 0, nullptr, nullptr);
    if (count <= 1)
        return {};
    std::string result(static_cast<size_t>(count - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value, -1, result.data(), count, nullptr, nullptr);
    return result;
}

void Log(const char *fmt, ...)
{
    FILE *f = nullptr;
    _wfopen_s(&f, LogPath().c_str(), L"ab");
    if (f == nullptr)
        return;

    SYSTEMTIME st = {};
    GetLocalTime(&st);
    std::fprintf(f, "%02u:%02u:%02u.%03u  [openxr] ", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);

    va_list ap;
    va_start(ap, fmt);
    std::vfprintf(f, fmt, ap);
    va_end(ap);

    std::fprintf(f, "\n");
    std::fclose(f);
}

void LogProcessOnce()
{
    static bool logged = false;
    if (logged)
        return;
    logged = true;

    wchar_t path[MAX_PATH] = {};
    GetModuleFileNameW(nullptr, path, static_cast<DWORD>(std::size(path)));
    Log("loaded into pid=%lu process=%s", GetCurrentProcessId(), Narrow(path).c_str());
}

PFN_xrVoidFunction ToVoidFunction(void *ptr)
{
    return reinterpret_cast<PFN_xrVoidFunction>(ptr);
}

template <typename T>
void Load(InstanceDispatch &dispatch, XrInstance instance, const char *name, T &fn)
{
    PFN_xrVoidFunction raw = nullptr;
    if (dispatch.next_gipa != nullptr && XR_SUCCEEDED(dispatch.next_gipa(instance, name, &raw)))
        fn = reinterpret_cast<T>(raw);
}

InstanceDispatch *DispatchForInstance(XrInstance instance)
{
    auto it = g_instances.find(instance);
    return it == g_instances.end() ? nullptr : &it->second;
}

InstanceDispatch *DispatchForSession(XrSession session)
{
    auto sit = g_sessions.find(session);
    if (sit == g_sessions.end())
        return nullptr;
    return DispatchForInstance(sit->second.instance);
}

InstanceDispatch *DispatchForSwapchain(XrSwapchain swapchain)
{
    auto swit = g_swapchains.find(swapchain);
    if (swit == g_swapchains.end())
        return nullptr;
    return DispatchForSession(swit->second.session);
}

std::string GraphicsBindingName(const XrSessionCreateInfo *info, ID3D11Device **d3d11_device)
{
    for (const XrBaseInStructure *cur = reinterpret_cast<const XrBaseInStructure *>(info ? info->next : nullptr);
         cur != nullptr;
         cur = reinterpret_cast<const XrBaseInStructure *>(cur->next))
    {
        if (cur->type == XR_TYPE_GRAPHICS_BINDING_D3D11_KHR)
        {
            const auto *binding = reinterpret_cast<const XrGraphicsBindingD3D11KHR *>(cur);
            if (d3d11_device != nullptr)
                *d3d11_device = binding->device;
            return "D3D11";
        }

        if (cur->type == XR_TYPE_GRAPHICS_BINDING_D3D12_KHR)
            return "D3D12";
        if (cur->type == XR_TYPE_GRAPHICS_BINDING_VULKAN_KHR || cur->type == XR_TYPE_GRAPHICS_BINDING_VULKAN2_KHR)
            return "Vulkan";
        if (cur->type == XR_TYPE_GRAPHICS_BINDING_OPENGL_WIN32_KHR)
            return "OpenGL";
    }

    return "unknown";
}

const char *LayerTypeName(XrStructureType type)
{
    switch (type)
    {
    case XR_TYPE_COMPOSITION_LAYER_PROJECTION: return "projection";
    case XR_TYPE_COMPOSITION_LAYER_QUAD: return "quad";
    case XR_TYPE_COMPOSITION_LAYER_CUBE_KHR: return "cube";
    case XR_TYPE_COMPOSITION_LAYER_CYLINDER_KHR: return "cylinder";
    case XR_TYPE_COMPOSITION_LAYER_EQUIRECT_KHR: return "equirect";
    default: return "other";
    }
}

bool FrameReferencesSwapchain(const XrFrameEndInfo *frame, XrSwapchain swapchain)
{
    if (frame == nullptr || frame->layers == nullptr)
        return false;

    for (uint32_t i = 0; i < frame->layerCount; ++i)
    {
        const XrCompositionLayerBaseHeader *base = frame->layers[i];
        if (base == nullptr)
            continue;

        if (base->type == XR_TYPE_COMPOSITION_LAYER_PROJECTION)
        {
            const auto *projection = reinterpret_cast<const XrCompositionLayerProjection *>(base);
            for (uint32_t view = 0; view < projection->viewCount; ++view)
                if (projection->views[view].subImage.swapchain == swapchain)
                    return true;
        }
        else if (base->type == XR_TYPE_COMPOSITION_LAYER_QUAD)
        {
            const auto *quad = reinterpret_cast<const XrCompositionLayerQuad *>(base);
            if (quad->subImage.swapchain == swapchain)
                return true;
        }
    }

    return false;
}

XRAPI_ATTR XrResult XRAPI_CALL LayerGetInstanceProcAddr(XrInstance instance, const char *name, PFN_xrVoidFunction *function);

XRAPI_ATTR XrResult XRAPI_CALL LayerDestroyInstance(XrInstance instance)
{
    PFN_xrDestroyInstance next = nullptr;
    {
        std::scoped_lock lock(g_mutex);
        auto *dispatch = DispatchForInstance(instance);
        if (dispatch != nullptr)
            next = dispatch->destroy_instance;
    }

    XrResult result = next != nullptr ? next(instance) : XR_ERROR_HANDLE_INVALID;
    if (XR_SUCCEEDED(result))
    {
        std::scoped_lock lock(g_mutex);
        g_instances.erase(instance);
        for (auto it = g_sessions.begin(); it != g_sessions.end();)
            it = it->second.instance == instance ? g_sessions.erase(it) : std::next(it);
        g_bridge.Reset();
        Log("destroy instance");
    }
    return result;
}

XRAPI_ATTR XrResult XRAPI_CALL LayerCreateSession(XrInstance instance, const XrSessionCreateInfo *createInfo, XrSession *session)
{
    InstanceDispatch dispatch = {};
    {
        std::scoped_lock lock(g_mutex);
        auto *found = DispatchForInstance(instance);
        if (found != nullptr)
            dispatch = *found;
    }

    if (dispatch.create_session == nullptr)
        return XR_ERROR_HANDLE_INVALID;

    ID3D11Device *d3d11_device = nullptr;
    std::string graphics = GraphicsBindingName(createInfo, &d3d11_device);
    XrResult result = dispatch.create_session(instance, createInfo, session);
    if (XR_SUCCEEDED(result) && session != nullptr)
    {
        std::scoped_lock lock(g_mutex);
        g_sessions[*session] = SessionState{instance, graphics, d3d11_device};
        Log("create session=%p graphics=%s d3d11_device=%p", reinterpret_cast<void *>(*session), graphics.c_str(), d3d11_device);
    }

    return result;
}

XRAPI_ATTR XrResult XRAPI_CALL LayerDestroySession(XrSession session)
{
    PFN_xrDestroySession next = nullptr;
    {
        std::scoped_lock lock(g_mutex);
        auto *dispatch = DispatchForSession(session);
        if (dispatch != nullptr)
            next = dispatch->destroy_session;
    }

    XrResult result = next != nullptr ? next(session) : XR_ERROR_HANDLE_INVALID;
    if (XR_SUCCEEDED(result))
    {
        std::scoped_lock lock(g_mutex);
        g_sessions.erase(session);
        for (auto it = g_swapchains.begin(); it != g_swapchains.end();)
            it = it->second.session == session ? g_swapchains.erase(it) : std::next(it);
        Log("destroy session=%p", reinterpret_cast<void *>(session));
    }
    return result;
}

XRAPI_ATTR XrResult XRAPI_CALL LayerCreateSwapchain(XrSession session, const XrSwapchainCreateInfo *createInfo, XrSwapchain *swapchain)
{
    InstanceDispatch dispatch = {};
    std::string graphics = "unknown";
    {
        std::scoped_lock lock(g_mutex);
        auto *found = DispatchForSession(session);
        if (found != nullptr)
        {
            dispatch = *found;
            auto sit = g_sessions.find(session);
            if (sit != g_sessions.end())
                graphics = sit->second.graphics_api;
        }
    }

    if (dispatch.create_swapchain == nullptr)
        return XR_ERROR_HANDLE_INVALID;

    XrResult result = dispatch.create_swapchain(session, createInfo, swapchain);
    if (XR_SUCCEEDED(result) && createInfo != nullptr && swapchain != nullptr)
    {
        SwapchainState state = {};
        state.session = session;
        state.width = createInfo->width;
        state.height = createInfo->height;
        state.array_size = createInfo->arraySize;
        state.face_count = createInfo->faceCount;
        state.mip_count = createInfo->mipCount;
        state.sample_count = createInfo->sampleCount;
        state.format = createInfo->format;
        state.usage_flags = createInfo->usageFlags;

        std::scoped_lock lock(g_mutex);
        g_swapchains[*swapchain] = std::move(state);
        Log("create swapchain=%p session=%p graphics=%s %ux%u array=%u faces=%u samples=%u mips=%u format=%lld usage=0x%llx",
            reinterpret_cast<void *>(*swapchain), reinterpret_cast<void *>(session), graphics.c_str(),
            createInfo->width, createInfo->height, createInfo->arraySize, createInfo->faceCount,
            createInfo->sampleCount, createInfo->mipCount, static_cast<long long>(createInfo->format),
            static_cast<unsigned long long>(createInfo->usageFlags));
    }

    return result;
}

XRAPI_ATTR XrResult XRAPI_CALL LayerDestroySwapchain(XrSwapchain swapchain)
{
    PFN_xrDestroySwapchain next = nullptr;
    {
        std::scoped_lock lock(g_mutex);
        auto *dispatch = DispatchForSwapchain(swapchain);
        if (dispatch != nullptr)
            next = dispatch->destroy_swapchain;
    }

    XrResult result = next != nullptr ? next(swapchain) : XR_ERROR_HANDLE_INVALID;
    if (XR_SUCCEEDED(result))
    {
        std::scoped_lock lock(g_mutex);
        g_swapchains.erase(swapchain);
        Log("destroy swapchain=%p", reinterpret_cast<void *>(swapchain));
    }
    return result;
}

XRAPI_ATTR XrResult XRAPI_CALL LayerEnumerateSwapchainImages(XrSwapchain swapchain, uint32_t imageCapacityInput,
                                                            uint32_t *imageCountOutput, XrSwapchainImageBaseHeader *images)
{
    InstanceDispatch dispatch = {};
    {
        std::scoped_lock lock(g_mutex);
        auto *found = DispatchForSwapchain(swapchain);
        if (found != nullptr)
            dispatch = *found;
    }

    if (dispatch.enumerate_swapchain_images == nullptr)
        return XR_ERROR_HANDLE_INVALID;

    XrResult result = dispatch.enumerate_swapchain_images(swapchain, imageCapacityInput, imageCountOutput, images);
    bool should_prepare = false;
    SessionState prepare_session = {};
    SwapchainState prepare_state = {};
    DXGI_FORMAT prepare_format = DXGI_FORMAT_UNKNOWN;
    if (XR_SUCCEEDED(result))
    {
        {
            std::scoped_lock lock(g_mutex);
            auto it = g_swapchains.find(swapchain);
            if (it != g_swapchains.end() && imageCapacityInput > 0 && images != nullptr && imageCountOutput != nullptr)
            {
                it->second.d3d11_images.clear();
                auto sit = g_sessions.find(it->second.session);
                if (sit != g_sessions.end())
                    prepare_session = sit->second;
                for (uint32_t i = 0; i < *imageCountOutput; ++i)
                {
                    auto *base = reinterpret_cast<XrSwapchainImageBaseHeader *>(reinterpret_cast<char *>(images) + sizeof(XrSwapchainImageD3D11KHR) * i);
                    if (base->type != XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR)
                        continue;
                    auto *d3d11 = reinterpret_cast<XrSwapchainImageD3D11KHR *>(base);
                    it->second.d3d11_images.push_back(d3d11->texture);

                    D3D11_TEXTURE2D_DESC desc = {};
                    if (d3d11->texture != nullptr)
                        d3d11->texture->GetDesc(&desc);
                    Log("swapchain=%p image[%u]=%p type=D3D11 desc=%ux%u array=%u mips=%u fmt=%u bind=0x%x misc=0x%x",
                        reinterpret_cast<void *>(swapchain), i, d3d11->texture, desc.Width, desc.Height,
                        desc.ArraySize, desc.MipLevels, desc.Format, desc.BindFlags, desc.MiscFlags);
                }
                Log("enumerate swapchain=%p images=%u d3d11_images=%zu capacity=%u",
                    reinterpret_cast<void *>(swapchain), *imageCountOutput, it->second.d3d11_images.size(), imageCapacityInput);

                if (prepare_session.d3d11_device != nullptr && it->second.array_size >= 2 && !it->second.d3d11_images.empty())
                {
                    D3D11_TEXTURE2D_DESC desc = {};
                    it->second.d3d11_images[0]->GetDesc(&desc);
                    if (desc.ArraySize >= 2 && desc.Width >= 1024 && desc.Height >= 1024)
                    {
                        should_prepare = true;
                        prepare_state = it->second;
                        prepare_format = desc.Format;
                    }
                }
            }
        }

        if (should_prepare)
        {
            std::scoped_lock bridge_lock(g_bridge_mutex);
            g_bridge.Prepare(dispatch, prepare_state.session, swapchain, prepare_session.d3d11_device, prepare_state, prepare_format);
        }
    }

    return result;
}

XRAPI_ATTR XrResult XRAPI_CALL LayerAcquireSwapchainImage(XrSwapchain swapchain, const XrSwapchainImageAcquireInfo *acquireInfo,
                                                         uint32_t *index)
{
    InstanceDispatch dispatch = {};
    {
        std::scoped_lock lock(g_mutex);
        auto *found = DispatchForSwapchain(swapchain);
        if (found != nullptr)
            dispatch = *found;
    }

    if (dispatch.acquire_swapchain_image == nullptr)
        return XR_ERROR_HANDLE_INVALID;

    XrResult result = dispatch.acquire_swapchain_image(swapchain, acquireInfo, index);
    if (XR_SUCCEEDED(result) && index != nullptr)
    {
        std::scoped_lock lock(g_mutex);
        auto it = g_swapchains.find(swapchain);
        if (it != g_swapchains.end())
            it->second.active_index = *index;
    }
    return result;
}

XRAPI_ATTR XrResult XRAPI_CALL LayerWaitSwapchainImage(XrSwapchain swapchain, const XrSwapchainImageWaitInfo *waitInfo)
{
    PFN_xrWaitSwapchainImage next = nullptr;
    {
        std::scoped_lock lock(g_mutex);
        auto *dispatch = DispatchForSwapchain(swapchain);
        if (dispatch != nullptr)
            next = dispatch->wait_swapchain_image;
    }
    return next != nullptr ? next(swapchain, waitInfo) : XR_ERROR_HANDLE_INVALID;
}

XRAPI_ATTR XrResult XRAPI_CALL LayerReleaseSwapchainImage(XrSwapchain swapchain, const XrSwapchainImageReleaseInfo *releaseInfo)
{
    InstanceDispatch dispatch = {};
    SessionState session = {};
    ID3D11Texture2D *image = nullptr;
    D3D11_TEXTURE2D_DESC desc = {};
    uint32_t index = UINT32_MAX;
    uint64_t frame = 0;
    uint32_t w = 0;
    uint32_t h = 0;
    XrSwapchainUsageFlags usage_flags = 0;
    uint32_t xr_array_size = 0;
    SwapchainState state = {};
    {
        std::scoped_lock lock(g_mutex);
        auto *found = DispatchForSwapchain(swapchain);
        if (found != nullptr)
            dispatch = *found;
        auto it = g_swapchains.find(swapchain);
        if (it != g_swapchains.end())
        {
            index = it->second.active_index;
            frame = ++it->second.frame_count;
            w = it->second.width;
            h = it->second.height;
            usage_flags = it->second.usage_flags;
            xr_array_size = it->second.array_size;
            state = it->second;
            auto sit = g_sessions.find(it->second.session);
            if (sit != g_sessions.end())
                session = sit->second;
            if (index < it->second.d3d11_images.size())
            {
                image = it->second.d3d11_images[index];
                if (image != nullptr)
                    image->AddRef();
            }
        }
    }

    if ((frame <= 6 || frame % 900 == 0) && w != 0)
        Log("release swapchain=%p image=%u frame=%llu size=%ux%u", reinterpret_cast<void *>(swapchain), index,
            static_cast<unsigned long long>(frame), w, h);

    if (image != nullptr)
    {
        image->GetDesc(&desc);
        const bool is_stereo_color =
            session.d3d11_device != nullptr &&
            xr_array_size >= 2 &&
            desc.ArraySize >= 2 &&
            desc.Width >= 1024 &&
            desc.Height >= 1024 &&
            (usage_flags & XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT) != 0 &&
            (desc.BindFlags & D3D11_BIND_RENDER_TARGET) != 0;
        if (is_stereo_color)
        {
            std::scoped_lock bridge_lock(g_bridge_mutex);
            if (g_bridge.ShouldProcess(frame))
                g_bridge.CaptureStereo(dispatch, state.session, swapchain, session.d3d11_device, image, desc, state, frame);
        }
        image->Release();
    }

    return dispatch.release_swapchain_image != nullptr
        ? dispatch.release_swapchain_image(swapchain, releaseInfo)
        : XR_ERROR_HANDLE_INVALID;
}

XRAPI_ATTR XrResult XRAPI_CALL LayerEndFrame(XrSession session, const XrFrameEndInfo *frameEndInfo)
{
    InstanceDispatch dispatch = {};
    {
        std::scoped_lock lock(g_mutex);
        auto *found = DispatchForSession(session);
        if (found != nullptr)
            dispatch = *found;
    }

    if (frameEndInfo != nullptr && frameEndInfo->layers != nullptr)
    {
        static uint64_t end_frame_count = 0;
        const uint64_t n = ++end_frame_count;
        if (n <= 30 || n % 300 == 0)
        {
            Log("end frame session=%p layers=%u env=%d", reinterpret_cast<void *>(session), frameEndInfo->layerCount,
                static_cast<int>(frameEndInfo->environmentBlendMode));
            for (uint32_t i = 0; i < std::min<uint32_t>(frameEndInfo->layerCount, 8); ++i)
            {
                const auto *base = frameEndInfo->layers[i];
                if (base != nullptr)
                    Log("  layer[%u] type=%s(%d)", i, LayerTypeName(base->type), static_cast<int>(base->type));
            }
        }

        std::scoped_lock lock(g_mutex);
        for (const auto &[swapchain, state] : g_swapchains)
        {
            if (state.session == session && FrameReferencesSwapchain(frameEndInfo, swapchain) && (n <= 30 || n % 300 == 0))
                Log("  submitted swapchain=%p size=%ux%u array=%u images=%zu", reinterpret_cast<void *>(swapchain),
                    state.width, state.height, state.array_size, state.d3d11_images.size());
        }
    }

    if (dispatch.end_frame == nullptr)
        return XR_ERROR_HANDLE_INVALID;

    {
        std::scoped_lock bridge_lock(g_bridge_mutex);
        static uint64_t bridge_state_log_count = 0;
        bool busy = false;
        bool pending = false;
        bool ready = false;
        uint64_t captured = 0;
        uint64_t processed = 0;
        g_bridge.Snapshot(busy, pending, ready, captured, processed);
        if (++bridge_state_log_count <= 30 || (bridge_state_log_count % 300) == 0)
        {
            Log("OpenXR bridge submit state busy=%d pending=%d ready=%d captured=%llu processed=%llu",
                busy ? 1 : 0, pending ? 1 : 0, ready ? 1 : 0,
                static_cast<unsigned long long>(captured), static_cast<unsigned long long>(processed));
        }
        XrResult patched_result = XR_SUCCESS;
        if (g_bridge.TryEndFrame(session, frameEndInfo, dispatch, patched_result))
            return patched_result;
    }

    return dispatch.end_frame(session, frameEndInfo);
}

XRAPI_ATTR XrResult XRAPI_CALL LayerGetInstanceProcAddr(XrInstance instance, const char *name, PFN_xrVoidFunction *function)
{
    if (function == nullptr)
        return XR_ERROR_VALIDATION_FAILURE;

    if (name != nullptr)
    {
        if (std::strcmp(name, "xrGetInstanceProcAddr") == 0) { *function = ToVoidFunction(reinterpret_cast<void *>(LayerGetInstanceProcAddr)); return XR_SUCCESS; }
        if (std::strcmp(name, "xrDestroyInstance") == 0) { *function = ToVoidFunction(reinterpret_cast<void *>(LayerDestroyInstance)); return XR_SUCCESS; }
        if (std::strcmp(name, "xrCreateSession") == 0) { *function = ToVoidFunction(reinterpret_cast<void *>(LayerCreateSession)); return XR_SUCCESS; }
        if (std::strcmp(name, "xrDestroySession") == 0) { *function = ToVoidFunction(reinterpret_cast<void *>(LayerDestroySession)); return XR_SUCCESS; }
        if (std::strcmp(name, "xrCreateSwapchain") == 0) { *function = ToVoidFunction(reinterpret_cast<void *>(LayerCreateSwapchain)); return XR_SUCCESS; }
        if (std::strcmp(name, "xrDestroySwapchain") == 0) { *function = ToVoidFunction(reinterpret_cast<void *>(LayerDestroySwapchain)); return XR_SUCCESS; }
        if (std::strcmp(name, "xrEnumerateSwapchainImages") == 0) { *function = ToVoidFunction(reinterpret_cast<void *>(LayerEnumerateSwapchainImages)); return XR_SUCCESS; }
        if (std::strcmp(name, "xrAcquireSwapchainImage") == 0) { *function = ToVoidFunction(reinterpret_cast<void *>(LayerAcquireSwapchainImage)); return XR_SUCCESS; }
        if (std::strcmp(name, "xrWaitSwapchainImage") == 0) { *function = ToVoidFunction(reinterpret_cast<void *>(LayerWaitSwapchainImage)); return XR_SUCCESS; }
        if (std::strcmp(name, "xrReleaseSwapchainImage") == 0) { *function = ToVoidFunction(reinterpret_cast<void *>(LayerReleaseSwapchainImage)); return XR_SUCCESS; }
        if (std::strcmp(name, "xrEndFrame") == 0) { *function = ToVoidFunction(reinterpret_cast<void *>(LayerEndFrame)); return XR_SUCCESS; }
    }

    PFN_xrGetInstanceProcAddr next = nullptr;
    {
        std::scoped_lock lock(g_mutex);
        auto *dispatch = DispatchForInstance(instance);
        if (dispatch != nullptr)
            next = dispatch->next_gipa;
    }

    if (next == nullptr)
    {
        *function = nullptr;
        return XR_ERROR_HANDLE_INVALID;
    }

    return next(instance, name, function);
}

XRAPI_ATTR XrResult XRAPI_CALL LayerCreateApiLayerInstance(const XrInstanceCreateInfo *info,
                                                          const XrApiLayerCreateInfo *apiLayerInfo,
                                                          XrInstance *instance)
{
    LogProcessOnce();
    if (apiLayerInfo == nullptr || apiLayerInfo->nextInfo == nullptr ||
        apiLayerInfo->nextInfo->nextGetInstanceProcAddr == nullptr ||
        apiLayerInfo->nextInfo->nextCreateApiLayerInstance == nullptr)
    {
        Log("create api layer instance failed: invalid loader chain");
        return XR_ERROR_INITIALIZATION_FAILED;
    }

    XrApiLayerCreateInfo next_layer_info = *apiLayerInfo;
    next_layer_info.nextInfo = apiLayerInfo->nextInfo->next;

    XrResult result = apiLayerInfo->nextInfo->nextCreateApiLayerInstance(info, &next_layer_info, instance);
    if (XR_FAILED(result))
    {
        Log("next create api layer instance failed result=%d", result);
        return result;
    }

    InstanceDispatch dispatch = {};
    dispatch.next_gipa = apiLayerInfo->nextInfo->nextGetInstanceProcAddr;
    Load(dispatch, *instance, "xrDestroyInstance", dispatch.destroy_instance);
    Load(dispatch, *instance, "xrCreateSession", dispatch.create_session);
    Load(dispatch, *instance, "xrDestroySession", dispatch.destroy_session);
    Load(dispatch, *instance, "xrCreateSwapchain", dispatch.create_swapchain);
    Load(dispatch, *instance, "xrDestroySwapchain", dispatch.destroy_swapchain);
    Load(dispatch, *instance, "xrEnumerateSwapchainImages", dispatch.enumerate_swapchain_images);
    Load(dispatch, *instance, "xrAcquireSwapchainImage", dispatch.acquire_swapchain_image);
    Load(dispatch, *instance, "xrWaitSwapchainImage", dispatch.wait_swapchain_image);
    Load(dispatch, *instance, "xrReleaseSwapchainImage", dispatch.release_swapchain_image);
    Load(dispatch, *instance, "xrEndFrame", dispatch.end_frame);

    {
        std::scoped_lock lock(g_mutex);
        g_instances[*instance] = dispatch;
    }

    Log("created instance=%p app=%s engine=%s api=%llu",
        reinterpret_cast<void *>(*instance),
        info != nullptr ? info->applicationInfo.applicationName : "",
        info != nullptr ? info->applicationInfo.engineName : "",
        info != nullptr ? static_cast<unsigned long long>(info->applicationInfo.apiVersion) : 0ull);
    return XR_SUCCESS;
}
} // namespace

extern "C"
{
__declspec(dllexport) XRAPI_ATTR XrResult XRAPI_CALL xrNegotiateLoaderApiLayerInterface(
    const XrNegotiateLoaderInfo *loaderInfo,
    const char *layerName,
    XrNegotiateApiLayerRequest *apiLayerRequest)
{
    if (loaderInfo == nullptr || apiLayerRequest == nullptr ||
        loaderInfo->structType != XR_LOADER_INTERFACE_STRUCT_LOADER_INFO ||
        loaderInfo->structVersion != XR_LOADER_INFO_STRUCT_VERSION ||
        loaderInfo->structSize != sizeof(XrNegotiateLoaderInfo) ||
        apiLayerRequest->structType != XR_LOADER_INTERFACE_STRUCT_API_LAYER_REQUEST ||
        apiLayerRequest->structVersion != XR_API_LAYER_INFO_STRUCT_VERSION ||
        apiLayerRequest->structSize != sizeof(XrNegotiateApiLayerRequest) ||
        loaderInfo->minInterfaceVersion > XR_CURRENT_LOADER_API_LAYER_VERSION ||
        loaderInfo->maxInterfaceVersion < XR_CURRENT_LOADER_API_LAYER_VERSION)
    {
        return XR_ERROR_INITIALIZATION_FAILED;
    }

    (void)layerName;
    LogProcessOnce();
    Log("negotiate layer=%s loader_if=%u-%u api=%llu-%llu",
        layerName != nullptr ? layerName : "",
        loaderInfo->minInterfaceVersion,
        loaderInfo->maxInterfaceVersion,
        static_cast<unsigned long long>(loaderInfo->minApiVersion),
        static_cast<unsigned long long>(loaderInfo->maxApiVersion));

    apiLayerRequest->layerInterfaceVersion = XR_CURRENT_LOADER_API_LAYER_VERSION;
    apiLayerRequest->layerApiVersion = XR_MAKE_VERSION(1, 0, 0);
    apiLayerRequest->getInstanceProcAddr = LayerGetInstanceProcAddr;
    apiLayerRequest->createApiLayerInstance = LayerCreateApiLayerInstance;
    return XR_SUCCESS;
}
}
