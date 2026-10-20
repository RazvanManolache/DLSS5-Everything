// Independent Windows capture frontend for the existing Phoenix feeder protocol.
// No NeuralScreen code or runtime is used.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <dwmapi.h>
#include <d3d11_4.h>
#include <dxgi1_6.h>
#include <d3dcompiler.h>
#include <wincodec.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <wrl/client.h>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <vector>
#include <chrono>
#include <d3d10.h>
#include <SpoutDX.h>
#include <nlohmann/json.hpp>
#include "screen-contract.h"
#include "../src/feed_ipc.h"

using Microsoft::WRL::ComPtr;
using Json = nlohmann::json;
using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
namespace fs = std::filesystem;

struct Handle {
    HANDLE value = nullptr;
    ~Handle() { reset(); }
    void reset(HANDLE next = nullptr) { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); value = next; }
    Handle() = default;
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
};
static void check(HRESULT hr, const char* action) {
    if (FAILED(hr)) { char text[240]; sprintf_s(text, "%s (0x%08X)", action, static_cast<unsigned>(hr)); throw std::runtime_error(text); }
}
static Json read_json(const fs::path& path) {
    if (fs::file_size(path) > (path.filename()==L"source.json"?4*1024*1024:128*1024)) throw std::runtime_error("Screen configuration too large");
    std::ifstream stream(path); return Json::parse(stream);
}
static fs::path utf8_path(const std::string& path) { return fs::path(winrt::to_hstring(path).c_str()); }
static void atomic_json(const fs::path& path, const Json& value) {
    const auto temp = fs::path(path.wstring() + L".tmp");
    { std::ofstream stream(temp); stream << value.dump(2); if (!stream) throw std::runtime_error("Cannot write screen status"); }
    if (!MoveFileExW(temp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
        throw std::runtime_error("Cannot publish screen status");
}


#include "camera-input.h"
#include "virtual-camera-output.h"

static const char* shader = R"(
Texture2D original : register(t0); Texture2D processed : register(t1); Texture2D smallInput : register(t2);
SamplerState linearSampler : register(s0);
cbuffer Settings : register(b0) { float mode, divider, gain, residual; float strength, lens, fov, vignette; float aberration, noise, time, enabled; float2 imageRatio, halfTexel; float hdrInput, hdrOutput, whiteScale, unused; };
struct V { float4 position : SV_Position; float2 uv : TEXCOORD; };
V vs(uint id : SV_VertexID) { V v; v.uv = float2((id << 1) & 2, id & 2); v.position = float4(v.uv * float2(2,-2) + float2(-1,1),0,1); return v; }
float3 linearToSrgb(float3 x) { x=max(x,0); return lerp(12.92*x,1.055*pow(x,1/2.4)-.055,step(.0031308,x)); }
float3 srgbToLinear(float3 x) { x=max(x,0); return lerp(x/12.92,pow((x+.055)/1.055,2.4),step(.04045,x)); }
float3 sdr(float3 x) { return hdrInput>.5 ? linearToSrgb(saturate(x/whiteScale)) : x; }
float4 copy(V v) : SV_Target { return float4(sdr(original.SampleLevel(linearSampler,v.uv,0).rgb),1); }
float4 composite(V v) : SV_Target {
    float2 uv = v.uv;
    if (lens > .5) { float2 p = uv*2-1; float r = length(p); float a = radians(fov)*.5;
        if (r > .0001) uv = .5 + .5*p*(atan(r*tan(a))/a)/r; }
    float3 rawHdr = original.SampleLevel(linearSampler,v.uv,0).rgb;
    float3 raw = sdr(rawHdr);
    float2 imageUv=clamp(uv*imageRatio,halfTexel,imageRatio-halfTexel);
    float3 enhanced = processed.SampleLevel(linearSampler,imageUv,0).rgb;
    float3 small = smallInput.SampleLevel(linearSampler,imageUv,0).rgb;
    float3 neural = enhanced;
    if (residual > .5) enhanced = sdr(original.SampleLevel(linearSampler,uv,0).rgb) + (enhanced - small)*strength;
    if (aberration > 0) { float2 shift=(uv-.5)*aberration*.012*imageRatio;
        enhanced.r = lerp(enhanced.r,processed.SampleLevel(linearSampler,clamp(imageUv+shift,halfTexel,imageRatio-halfTexel),0).r,aberration);
        enhanced.b = lerp(enhanced.b,processed.SampleLevel(linearSampler,clamp(imageUv-shift,halfTexel,imageRatio-halfTexel),0).b,aberration); }
    enhanced *= 1 - vignette*saturate(dot(uv-.5,uv-.5)*2);
    enhanced += (frac(sin(dot(v.position.xy+time,float2(12.9898,78.233)))*43758.5453)-.5)*noise*.08;
    float3 hdrEnhanced = residual > .5 ? original.SampleLevel(linearSampler,uv,0).rgb + (srgbToLinear(saturate(neural))-srgbToLinear(small))*whiteScale*strength : srgbToLinear(saturate(enhanced))*whiteScale;
    // Apply camera treatment as a residual, preserving HDR highlights outside the SDR conditioning range.
    if(residual > .5) hdrEnhanced += (srgbToLinear(saturate(enhanced))-srgbToLinear(saturate(sdr(original.SampleLevel(linearSampler,uv,0).rgb)+(neural-small)*strength)))*whiteScale;
    if(hdrOutput>.5) { raw=rawHdr; enhanced=hdrEnhanced; }
    float3 color = enabled < .5 ? raw : enhanced;
    if (mode > .5 && mode < 1.5) color = raw;
    if (enabled > .5 && mode > 1.5 && mode < 2.5 && v.uv.x < divider) color = raw;
    if (enabled > .5 && mode > 2.5) color = abs(enhanced-raw)*gain;
    if (enabled > .5 && mode > 1.5 && mode < 2.5 && abs(v.uv.x-divider) < .0006) color = 1;
    return float4(hdrOutput>.5 ? color : saturate(color),1);
}
)";

class Studio {
    fs::path root;
    Json initial;
    screen::Options options;
    Handle parent, host, job, pipe, fence_event;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11Device5> device5;
    ComPtr<ID3D11DeviceContext4> context4;
    ComPtr<ID3D11Fence> input_fence, output_fence;
    ComPtr<IDXGISwapChain1> swapchain;
    ComPtr<ID3D11VertexShader> vertex_shader;
    ComPtr<ID3D11PixelShader> copy_shader, composite_shader;
    ComPtr<ID3D11SamplerState> sampler;
    ComPtr<ID3D11Buffer> constants;
    ComPtr<ID3D11Texture2D> textures[FEED_SLOTS];
    Handle texture_handles[FEED_SLOTS];
    ComPtr<ID3D11Texture2D> raw;
    ComPtr<ID3D11ShaderResourceView> raw_view, processed_view, small_view;
    GraphicsCaptureItem item{nullptr};
    Direct3D11CaptureFramePool pool{nullptr};
    GraphicsCaptureSession capture{nullptr};
    winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice capture_device{nullptr};
    HWND overlay = nullptr, target = nullptr;
    bool capture_excluded = false;
    HMONITOR monitor = nullptr;
    UINT width = 0, height = 0, process_width = 0, process_height = 0;
    UINT image_width = 0, image_height = 0;
    uint64_t frames = 0;
    uint64_t presented_frames = 0;
    HRESULT last_present = S_OK;
    uint64_t evaluated_revision = 0;
    bool have_raw = false;
    int saved_screenshot = 0;
    bool reset = true;
    bool paused = false;
    spoutDX spout_sender;
    bool spout_open = false;
    std::string spout_name;
    std::vector<fs::path> file_inputs;
    size_t file_index=0;
    std::unique_ptr<CameraInput> camera_input;
    std::vector<BYTE> camera_pixels;
    camera_output::VirtualCamera virtual_camera;
    bool camera_closed = false;
    bool camera() const { return initial.at("kind") == "camera"; }
    bool offline() const { return initial.at("kind")=="files"; }
    ULONGLONG status_time = 0;
    ULONGLONG hdr_check_time = 0;
    std::chrono::steady_clock::time_point started = std::chrono::steady_clock::now();

    bool stopping() const {
        return camera_closed || fs::exists(root / L"stop.signal") || WaitForSingleObject(parent.value, 0) != WAIT_TIMEOUT;
    }
    bool host_alive() const { return host.value && WaitForSingleObject(host.value, 0) == WAIT_TIMEOUT; }
    void pump_guard(ULONGLONG waiting_since) {
        MSG message{};
        while (PeekMessageW(&message,nullptr,0,0,PM_REMOVE)) { TranslateMessage(&message); DispatchMessageW(&message); }
        if (overlay && ((target && (GetForegroundWindow()!=target || IsIconic(target) || !IsWindowVisible(target))) ||
            (!camera() && GetTickCount64()-waiting_since>500))) ShowWindow(overlay,SW_HIDE);
    }
    void transfer(bool write, void* data, DWORD bytes) {
        auto* cursor = static_cast<BYTE*>(data);
        const auto deadline = GetTickCount64() + 30000;
        while (bytes) {
            Handle event; event.value = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (!event.value) throw std::runtime_error("Cannot allocate pipe event");
            OVERLAPPED op{}; op.hEvent = event.value;
            DWORD done = 0;
            BOOL ok = write ? WriteFile(pipe.value, cursor, bytes, &done, &op) : ReadFile(pipe.value, cursor, bytes, &done, &op);
            if (!ok && GetLastError() == ERROR_IO_PENDING) {
                bool cancelled = false;
                while (WaitForSingleObject(event.value, 20) == WAIT_TIMEOUT) {
                    pump_guard(deadline-30000);
                    if (stopping() || !host_alive() || GetTickCount64() > deadline) { cancelled = true; break; }
                }
                if (cancelled) { CancelIoEx(pipe.value, &op); GetOverlappedResult(pipe.value, &op, &done, TRUE); throw std::runtime_error("Screen host exchange stopped or timed out"); }
                ok = GetOverlappedResult(pipe.value, &op, &done, FALSE);
            }
            if (!ok || !done) throw std::runtime_error("Screen host pipe disconnected");
            cursor += done; bytes -= done;
        }
    }
    template<class T> void send(char tag, const T& value) {
        transfer(true, &tag, 1); transfer(true, const_cast<T*>(&value), sizeof(T));
    }
    template<class T> void receive(T& value) { transfer(false, &value, sizeof(T)); }
    void status(const std::string& state, const std::string& error = "") {
        const double seconds = std::chrono::duration<double>(std::chrono::steady_clock::now()-started).count();
        atomic_json(root / L"status.json", {{"schema",1},{"state",state},{"error",error},{"frames",frames},
            {"width",width},{"height",height},{"processing_width",image_width},{"processing_height",image_height},
            {"canvas_width",process_width},{"canvas_height",process_height},
            {"average_fps", seconds > 0 ? frames / seconds : 0},{"applied_revision",options.revision},
            {"screenshot",saved_screenshot},{"transport","shared-gpu"},{"guides","flat depth, zero motion"},
            {"hdr",options.hdr},{"spout",spout_open},{"converted",file_index},
            {"camera_fps",camera_input ? camera_input->rate : 0},{"virtual_camera",virtual_camera.active()},
            {"presented_frames",presented_frames},{"overlay_visible",overlay && IsWindowVisible(overlay)},
            {"capture_excluded",capture_excluded},
            {"last_present",static_cast<uint32_t>(last_present)},{"target_foreground",target && GetForegroundWindow()==target}});
        status_time = GetTickCount64();
    }
    void load_options(bool first = false) {
        try {
            const auto data = read_json(root / L"settings.json");
            screen::Options next;
            next.schema=data.at("schema"); next.revision=data.at("revision"); next.enabled=data.at("enabled");
            next.iterations=data.at("iterations"); next.fps=data.at("fps"); next.scale=data.at("scale");
            next.comparison=data.at("comparison"); next.divider=data.at("divider"); next.gain=data.at("gain");
            next.residual=data.at("residual"); next.residual_strength=data.at("residual_strength");
            next.lens=data.at("lens"); next.fov=data.at("fov"); next.vignette=data.at("vignette");
            next.aberration=data.at("aberration"); next.noise=data.at("noise"); next.screenshot=data.at("screenshot");
            next.hdr=data.value("hdr",false); next.paper_white=data.value("paper_white",203.f);
            next.spout=data.value("spout",false); next.spout_name=data.value("spout_name",std::string("DLSS5 Everything"));
            next.virtual_camera=data.value("virtual_camera",false);
            next.validate();
            if (camera() && next.hdr) throw std::runtime_error("Camera capture uses SDR; HDR transport is unavailable");
            if (next.virtual_camera && !camera()) throw std::runtime_error("Virtual webcam output is available from the Camera tab");
            if(!first && next.hdr!=options.hdr) throw std::runtime_error("Stop and restart capture to change HDR transport");
            if (next.revision < options.revision) throw std::runtime_error("Stale screen settings revision");
            if (next.enabled != options.enabled) reset = true;
            options = next;
        } catch (const std::exception& error) {
            if (first) throw;
            status("settings-rejected", error.what());
        }
    }
    void launch_host() {
        job.value = CreateJobObjectW(nullptr, nullptr);
        if (!job.value) throw std::runtime_error("Cannot create host lifetime job");
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(job.value, JobObjectExtendedLimitInformation, &limits, sizeof(limits)))
            throw std::runtime_error("Cannot set host lifetime policy");
        auto executable = root / L"host64/dlss5-feed-host64.exe";
        std::wstring command = L"\"" + executable.wstring() + L"\" " + std::to_wstring(GetCurrentProcessId()) + L" --hide";
        STARTUPINFOW si{}; si.cb = sizeof(si); PROCESS_INFORMATION pi{};
        if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW | CREATE_SUSPENDED,
                            nullptr, executable.parent_path().c_str(), &si, &pi)) throw std::runtime_error("Cannot start Phoenix DLSS5 screen host");
        host.value = pi.hProcess;
        Handle thread; thread.value = pi.hThread;
        if (!AssignProcessToJobObject(job.value, host.value)) { TerminateProcess(host.value, 1); throw std::runtime_error("Cannot own screen host lifetime"); }
        if (ResumeThread(thread.value) == static_cast<DWORD>(-1)) throw std::runtime_error("Cannot resume screen host");
        const std::wstring name = L"\\\\.\\pipe\\dlss5-feed." + std::to_wstring(GetCurrentProcessId());
        const auto deadline = GetTickCount64() + 30000;
        while (!stopping() && host_alive() && GetTickCount64() < deadline) {
            auto value = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);
            if (value != INVALID_HANDLE_VALUE) { pipe.value = value; break; }
            Sleep(30);
        }
        if (!pipe.value) throw std::runtime_error("Phoenix DLSS5 host did not become ready; inspect its local diagnostic log");
        FeedHello hello{FEED_IPC_MAGIC, FEED_IPC_VERSION, GetCurrentProcessId()};
        transfer(true, &hello, sizeof(hello)); FeedHelloAck ack{}; receive(ack);
        if (ack.magic != FEED_IPC_MAGIC || ack.version != FEED_IPC_VERSION) throw std::runtime_error("Screen host IPC version mismatch");
    }
    void initialize_gpu() {
        D3D_FEATURE_LEVEL level{};
        check(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nullptr, 0, D3D11_SDK_VERSION, &device, &level, &context), "Create capture GPU device");
        check(device.As(&device5), "D3D11 shared fences unavailable");
        check(context.As(&context4), "D3D11 fence context unavailable");
        ComPtr<ID3D10Multithread> multithread; check(context.As(&multithread),"Protect encoder GPU access");
        multithread->SetMultithreadProtected(TRUE);
        auto compile = [&](const char* entry, const char* profile) {
            ComPtr<ID3DBlob> code, errors;
            check(D3DCompile(shader, strlen(shader), nullptr, nullptr, nullptr, entry, profile, D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &errors), "Compile screen shader");
            return code;
        };
        auto vs = compile("vs", "vs_5_0"), ps = compile("copy", "ps_5_0"), cs = compile("composite", "ps_5_0");
        check(device->CreateVertexShader(vs->GetBufferPointer(), vs->GetBufferSize(), nullptr, &vertex_shader), "Create screen vertex shader");
        check(device->CreatePixelShader(ps->GetBufferPointer(), ps->GetBufferSize(), nullptr, &copy_shader), "Create copy shader");
        check(device->CreatePixelShader(cs->GetBufferPointer(), cs->GetBufferSize(), nullptr, &composite_shader), "Create compositor");
        D3D11_SAMPLER_DESC sd{}; sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP; sd.MaxLOD = D3D11_FLOAT32_MAX;
        check(device->CreateSamplerState(&sd, &sampler), "Create screen sampler");
        D3D11_BUFFER_DESC cb{}; cb.ByteWidth = 80; cb.Usage = D3D11_USAGE_DEFAULT; cb.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        check(device->CreateBuffer(&cb, nullptr, &constants), "Create compositor settings");
        fence_event.value = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (!fence_event.value) throw std::runtime_error("Cannot create GPU completion event");
    }
    void initialize_capture() {
        if(offline()) {
            if(options.hdr || options.spout) throw std::runtime_error("File conversion uses SDR with no sharing");
            for(const auto& value:initial.at("files")) file_inputs.push_back(utf8_path(value.get<std::string>()));
            if(file_inputs.empty() || file_inputs.size()>10000) throw std::runtime_error("Invalid conversion frame count");
            return;
        }
        if (camera()) {
            camera_input = std::make_unique<CameraInput>(initial.at("device_id").get<std::string>(),
                initial.at("capture_width").get<UINT>(), initial.at("capture_height").get<UINT>(), initial.at("capture_fps").get<UINT>());
            return;
        }
        if (!GraphicsCaptureSession::IsSupported()) throw std::runtime_error("Windows Graphics Capture is unavailable");
        auto factory = winrt::get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        const auto handle = initial.at("target").get<uint64_t>();
        if (initial.at("kind") == "window") {
            target = reinterpret_cast<HWND>(handle);
            DWORD pid = 0; GetWindowThreadProcessId(target, &pid);
            if (!IsWindow(target) || pid != initial.at("target_pid").get<DWORD>() || pid == GetCurrentProcessId())
                throw std::runtime_error("The selected capture window has closed or changed owner");
            check(factory->CreateForWindow(target, winrt::guid_of<GraphicsCaptureItem>(), winrt::put_abi(item)), "Capture selected window");
        } else if (initial.at("kind") == "monitor") {
            monitor = reinterpret_cast<HMONITOR>(handle);
            MONITORINFO info{}; info.cbSize = sizeof(info);
            if (!GetMonitorInfoW(monitor, &info)) throw std::runtime_error("Selected monitor is no longer available");
            check(factory->CreateForMonitor(monitor, winrt::guid_of<GraphicsCaptureItem>(), winrt::put_abi(item)), "Capture selected monitor");
        } else throw std::runtime_error("Unknown screen capture source");
        check_sdr();
        ComPtr<IDXGIDevice> dxgi; check(device.As(&dxgi), "Get capture DXGI device");
        winrt::com_ptr<IInspectable> inspectable;
        check(CreateDirect3D11DeviceFromDXGIDevice(dxgi.Get(), inspectable.put()), "Create Windows capture device");
        capture_device = inspectable.as<winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice>();
        pool = Direct3D11CaptureFramePool::CreateFreeThreaded(capture_device, capture_format(), 2, item.Size());
        capture = pool.CreateCaptureSession(item);
        // Keep the system capture border; do not bypass its consent indication.
        capture.IsCursorCaptureEnabled(initial.value("cursor", true));
        capture.StartCapture();
    }
    static LRESULT CALLBACK window_proc(HWND hwnd, UINT message, WPARAM w, LPARAM l) {
        auto* self = reinterpret_cast<Studio*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (self && self->camera()) {
            if (message == WM_CLOSE) { self->camera_closed = true; ShowWindow(hwnd, SW_HIDE); return 0; }
            return DefWindowProcW(hwnd, message, w, l);
        }
        if (message == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
        if (message == WM_NCHITTEST) return HTTRANSPARENT;
        return DefWindowProcW(hwnd, message, w, l);
    }
    void initialize_overlay() {
        WNDCLASSW wc{}; wc.lpfnWndProc = window_proc; wc.hInstance = GetModuleHandleW(nullptr); wc.lpszClassName = L"DLSS5ScreenStudio";
        RegisterClassW(&wc);
        if (camera()) {
            // Keep the preview's aspect correct; publishing always stays at the camera's size.
            const auto style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
            RECT bounds{0, 0, 960, static_cast<LONG>(960ull * camera_input->height / camera_input->width)};
            AdjustWindowRect(&bounds, style, FALSE);
            overlay = CreateWindowExW(0, wc.lpszClassName, L"DLSS5 Everything - Camera Preview", style,
                CW_USEDEFAULT, CW_USEDEFAULT, bounds.right-bounds.left, bounds.bottom-bounds.top, nullptr, nullptr, wc.hInstance, nullptr);
            if (!overlay) throw std::runtime_error("Cannot create camera preview");
            SetWindowLongPtrW(overlay, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(this));
        } else overlay = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_LAYERED,
            wc.lpszClassName, L"DLSS5 Screen Studio Output", WS_POPUP, 0, 0, 1, 1, nullptr, nullptr, wc.hInstance, nullptr);
        if (!overlay) throw std::runtime_error("Cannot create capture output window");
        if (!camera()) {
        if (!SetLayeredWindowAttributes(overlay,0,255,LWA_ALPHA)) throw std::runtime_error("Cannot configure click-through output");
        if (!SetWindowDisplayAffinity(overlay, WDA_EXCLUDEFROMCAPTURE))
            throw std::runtime_error("Cannot exclude output from capture; refusing a feedback loop");
        DWORD affinity = 0;
        if (!GetWindowDisplayAffinity(overlay, &affinity) || affinity != WDA_EXCLUDEFROMCAPTURE)
            throw std::runtime_error("Output capture exclusion was not retained; refusing monitor capture");
        capture_excluded = true;
        }
        ComPtr<IDXGIDevice> dxgi; check(device.As(&dxgi), "Overlay DXGI device");
        ComPtr<IDXGIAdapter> adapter; check(dxgi->GetAdapter(&adapter), "Overlay adapter");
        ComPtr<IDXGIFactory2> factory; check(adapter->GetParent(IID_PPV_ARGS(&factory)), "Overlay factory");
        DXGI_SWAP_CHAIN_DESC1 desc{}; desc.Width=1; desc.Height=1; desc.Format=options.hdr?DXGI_FORMAT_R16G16B16A16_FLOAT:DXGI_FORMAT_R8G8B8A8_UNORM;
        desc.SampleDesc.Count=1; desc.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT; desc.BufferCount=2;
        desc.SwapEffect=DXGI_SWAP_EFFECT_FLIP_DISCARD;
        check(factory->CreateSwapChainForHwnd(device.Get(), overlay, &desc, nullptr, nullptr, &swapchain), "Create screen presenter");
        check(factory->MakeWindowAssociation(overlay, DXGI_MWA_NO_ALT_ENTER), "Disable overlay fullscreen hotkey");
        if(options.hdr) {
            ComPtr<IDXGISwapChain3> modern; check(swapchain.As(&modern),"HDR swapchain unavailable");
            UINT support=0; check(modern->CheckColorSpaceSupport(DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709,&support),"Check scRGB presentation");
            if(!(support&DXGI_SWAP_CHAIN_COLOR_SPACE_SUPPORT_FLAG_PRESENT)) throw std::runtime_error("This display cannot present scRGB");
            check(modern->SetColorSpace1(DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709),"Enable scRGB presentation");
        }
    }
    DirectXPixelFormat capture_format() const { return options.hdr?DirectXPixelFormat::R16G16B16A16Float:DirectXPixelFormat::B8G8R8A8UIntNormalized; }
    void check_sdr() {
        auto selected = target ? MonitorFromWindow(target,MONITOR_DEFAULTTONEAREST) : monitor;
        ComPtr<IDXGIFactory1> factory; check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)), "Inspect display color space");
        for (UINT a=0;;++a) {
            ComPtr<IDXGIAdapter1> adapter;
            auto hr=factory->EnumAdapters1(a,&adapter); if(hr==DXGI_ERROR_NOT_FOUND) break; check(hr,"Inspect display adapter");
            for (UINT o=0;;++o) {
                ComPtr<IDXGIOutput> output; hr=adapter->EnumOutputs(o,&output); if(hr==DXGI_ERROR_NOT_FOUND) break; check(hr,"Inspect display");
                DXGI_OUTPUT_DESC desc{}; check(output->GetDesc(&desc),"Read display metadata");
                if(desc.Monitor!=selected) continue;
                ComPtr<IDXGIOutput6> modern;
                if(SUCCEEDED(output.As(&modern))) {
                    DXGI_OUTPUT_DESC1 color{}; check(modern->GetDesc1(&color),"Read display HDR state");
                    if(!options.hdr && (color.ColorSpace==DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 || color.ColorSpace==DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709))
                        throw std::runtime_error("Enable HDR transport and restart capture for this HDR display.");
                }
                hdr_check_time=GetTickCount64(); return;
            }
        }
        throw std::runtime_error("Capture display is no longer available");
    }
    bool position_overlay(bool output_ready = false) {
        if (camera()) {
            if (camera_closed) return false;
            if (frames || output_ready) ShowWindow(overlay, SW_SHOWNOACTIVATE);
            return true;
        }
        if(offline()) return true;
        RECT bounds{};
        bool visible = true;
        if(GetTickCount64()-hdr_check_time>2000) check_sdr();
        if (target) {
            DWORD pid=0; GetWindowThreadProcessId(target,&pid);
            if (!IsWindow(target) || pid != initial.at("target_pid").get<DWORD>()) throw std::runtime_error("Capture window closed");
            if (IsIconic(target) || !IsWindowVisible(target)) { ShowWindow(overlay, SW_HIDE); return false; }
            visible = GetForegroundWindow()==target;
            if (!visible) ShowWindow(overlay,SW_HIDE);
            check(DwmGetWindowAttribute(target, DWMWA_EXTENDED_FRAME_BOUNDS, &bounds, sizeof(bounds)), "Read captured window bounds");
        } else {
            MONITORINFO info{}; info.cbSize=sizeof(info);
            if (!GetMonitorInfoW(monitor,&info)) throw std::runtime_error("Capture monitor disconnected");
            bounds=info.rcMonitor;
        }
        if (bounds.right <= bounds.left || bounds.bottom <= bounds.top) return false;
        SetWindowPos(overlay, HWND_TOPMOST, bounds.left, bounds.top, bounds.right-bounds.left, bounds.bottom-bounds.top,
            SWP_NOACTIVATE | ((frames || output_ready) && visible ? SWP_SHOWWINDOW : 0));
        return true;
    }
    ComPtr<ID3D11Texture2D> make_texture(UINT w, UINT h, DXGI_FORMAT format, bool shared, const void* data=nullptr, UINT pitch=0) {
        D3D11_TEXTURE2D_DESC td{}; td.Width=w; td.Height=h; td.ArraySize=1; td.MipLevels=1; td.Format=format;
        td.SampleDesc.Count=1; td.Usage=D3D11_USAGE_DEFAULT;
        td.BindFlags=D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
        if (shared) td.MiscFlags=D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_SHARED;
        D3D11_SUBRESOURCE_DATA init{}; init.pSysMem=data; init.SysMemPitch=pitch;
        ComPtr<ID3D11Texture2D> result;
        check(device->CreateTexture2D(&td, data?&init:nullptr, &result), "Allocate screen texture"); return result;
    }
    void rebuild(UINT w, UINT h) {
        ShowWindow(overlay,SW_HIDE);
        auto size = screen::processing_size(w,h,options.scale);
        auto canvas=screen::native_canvas(size);
        const bool changed_capture = w!=width || h!=height || !raw;
        width=w; height=h; image_width=size.width; image_height=size.height; process_width=canvas.width; process_height=canvas.height;
        ID3D11ShaderResourceView* clear[3]{}; context->PSSetShaderResources(0,3,clear);
        context->OMSetRenderTargets(0,nullptr,nullptr); context->Flush();
        if(changed_capture) {
            raw_view.Reset(); raw=make_texture(w,h,options.hdr?DXGI_FORMAT_R16G16B16A16_FLOAT:DXGI_FORMAT_B8G8R8A8_UNORM,false); have_raw=false;
            check(device->CreateShaderResourceView(raw.Get(),nullptr,&raw_view), "View captured texture");
        }
        processed_view.Reset(); small_view.Reset();
        for (int slot=0; slot<FEED_SLOTS; ++slot) {
            textures[slot].Reset(); texture_handles[slot].reset();
            const auto format = slot==FEED_DEPTH ? DXGI_FORMAT_R32_FLOAT : slot==FEED_MV ? DXGI_FORMAT_R16G16_FLOAT :
                slot==FEED_MASK ? DXGI_FORMAT_R8_UNORM : DXGI_FORMAT_R8G8B8A8_UNORM;
            std::vector<float> depth;
            std::vector<BYTE> zero;
            const void* data=nullptr; UINT pitch=0;
            if (slot==FEED_DEPTH) { depth.resize(static_cast<size_t>(process_width)*process_height,1.f); data=depth.data(); pitch=process_width*4; }
            if (slot==FEED_MV || slot==FEED_MASK) { pitch=process_width*(slot==FEED_MV?4:1); zero.resize(static_cast<size_t>(pitch)*process_height); data=zero.data(); }
            textures[slot]=make_texture(process_width,process_height,format,true,data,pitch);
            ComPtr<IDXGIResource1> resource; check(textures[slot].As(&resource), "Share screen texture");
            check(resource->CreateSharedHandle(nullptr,DXGI_SHARED_RESOURCE_READ|DXGI_SHARED_RESOURCE_WRITE,nullptr,&texture_handles[slot].value), "Create shared screen handle");
        }
        check(device->CreateShaderResourceView(textures[FEED_OUTPUT].Get(),nullptr,&processed_view), "View enhanced texture");
        check(device->CreateShaderResourceView(textures[FEED_COLOR].Get(),nullptr,&small_view), "View processing input");
        FeedBuild build{}; build.width=build.output_width=process_width; build.height=build.output_height=process_height;
        build.color_fmt=build.output_fmt=DXGI_FORMAT_R8G8B8A8_UNORM; build.flags_override=-1;
        build.mv_scale_x=build.mv_scale_y=1; build.present_width=w; build.present_height=h;
        for (int slot=0;slot<FEED_SLOTS;++slot) build.tex[slot]=reinterpret_cast<uint64_t>(texture_handles[slot].value);
        send('B',build); FeedBuildAck ack{}; receive(ack);
        if (!ack.ok || !ack.fence_in || !ack.fence_out) throw std::runtime_error("Phoenix DLSS5 rejected screen shared textures; HDR/cross-adapter sharing is unsupported");
        // Host duplicates these handles once per connection, not once per build.
        if (!input_fence || !output_fence) {
            Handle incoming, outgoing; incoming.value=reinterpret_cast<HANDLE>(ack.fence_in); outgoing.value=reinterpret_cast<HANDLE>(ack.fence_out);
            check(device5->OpenSharedFence(incoming.value,IID_PPV_ARGS(&input_fence)), "Open host input fence");
            check(device5->OpenSharedFence(outgoing.value,IID_PPV_ARGS(&output_fence)), "Open host output fence");
        }
        check(swapchain->ResizeBuffers(0,w,h,DXGI_FORMAT_UNKNOWN,0), "Resize screen output");
        reset=true;
    }
    void draw(ID3D11Texture2D* destination, bool composite, bool source_extent=false) {
        ComPtr<ID3D11RenderTargetView> rtv; check(device->CreateRenderTargetView(destination,nullptr,&rtv), "Render screen frame");
        D3D11_TEXTURE2D_DESC td{}; destination->GetDesc(&td);
        D3D11_VIEWPORT vp{}; vp.Width=static_cast<float>(td.Width); vp.Height=static_cast<float>(td.Height); vp.MaxDepth=1;
        if(!composite) {
            const float clear[]{0,0,0,1}; context->ClearRenderTargetView(rtv.Get(),clear);
            if(!source_extent) { vp.Width=static_cast<float>(image_width); vp.Height=static_cast<float>(image_height); }
        }
        if(composite && source_extent) {
            const float clear[]{0,0,0,1}; context->ClearRenderTargetView(rtv.Get(),clear);
            vp.Width=static_cast<float>(width); vp.Height=static_cast<float>(height);
        }
        context->RSSetViewports(1,&vp); context->OMSetRenderTargets(1,rtv.GetAddressOf(),nullptr);
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(vertex_shader.Get(),nullptr,0); context->PSSetShader(composite?composite_shader.Get():copy_shader.Get(),nullptr,0);
        ID3D11ShaderResourceView* views[]{raw_view.Get(),processed_view.Get(),small_view.Get()};
        context->PSSetShaderResources(0,composite?3:1,views); context->PSSetSamplers(0,1,sampler.GetAddressOf());
        const float values[]{static_cast<float>(options.comparison),options.divider,options.gain,options.residual?1.f:0.f,
            options.residual_strength,options.lens?1.f:0.f,options.fov,options.vignette,options.aberration,options.noise,
            static_cast<float>(frames%10000),options.enabled?1.f:0.f,
            static_cast<float>(image_width)/process_width,static_cast<float>(image_height)/process_height,.5f/process_width,.5f/process_height,
            options.hdr?1.f:0.f,td.Format==DXGI_FORMAT_R16G16B16A16_FLOAT?1.f:0.f,options.paper_white/80.f,0};
        context->UpdateSubresource(constants.Get(),0,nullptr,values,0,0); context->PSSetConstantBuffers(0,1,constants.GetAddressOf());
        context->Draw(3,0);
        ID3D11ShaderResourceView* clear[3]{}; context->PSSetShaderResources(0,3,clear); context->OMSetRenderTargets(0,nullptr,nullptr);
    }
    void wait_output(uint64_t value) {
        check(output_fence->SetEventOnCompletion(value,fence_event.value), "Wait for processed screen frame");
        const auto deadline=GetTickCount64()+30000;
        for (;;) {
            pump_guard(deadline-30000);
            const auto result=WaitForSingleObject(fence_event.value,20);
            if (result==WAIT_OBJECT_0) break;
            if (result!=WAIT_TIMEOUT || stopping() || !host_alive() || GetTickCount64()>deadline)
                throw std::runtime_error("Phoenix DLSS5 screen frame did not complete");
        }
        if (output_fence->GetCompletedValue()==UINT64_MAX || output_fence->GetCompletedValue()<value)
            throw std::runtime_error("Screen output fence invalid or device removed");
        check(context4->Wait(output_fence.Get(),value), "Order output GPU read");
    }
    void png(ID3D11Texture2D* texture, const fs::path& path) {
        D3D11_TEXTURE2D_DESC td{}; texture->GetDesc(&td);
        if(td.Format!=DXGI_FORMAT_R8G8B8A8_UNORM && td.Format!=DXGI_FORMAT_B8G8R8A8_UNORM)
            throw std::runtime_error("Screenshot requires explicit SDR conversion");
        td.Usage=D3D11_USAGE_STAGING; td.BindFlags=td.MiscFlags=0; td.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
        ComPtr<ID3D11Texture2D> staging; check(device->CreateTexture2D(&td,nullptr,&staging), "Allocate screenshot readback");
        context->CopyResource(staging.Get(),texture);
        D3D11_MAPPED_SUBRESOURCE map{}; check(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&map), "Read screenshot");
        std::vector<BYTE> pixels(static_cast<size_t>(td.Width)*td.Height*4);
        for (UINT y=0;y<td.Height;++y) memcpy(pixels.data()+static_cast<size_t>(y)*td.Width*4, static_cast<BYTE*>(map.pData)+static_cast<size_t>(y)*map.RowPitch,td.Width*4);
        context->Unmap(staging.Get(),0);
        // WIC's PNG encoder can negotiate RGBA to BGRA. Give it BGRA explicitly.
        if (td.Format==DXGI_FORMAT_R8G8B8A8_UNORM)
            for (size_t offset=0;offset<pixels.size();offset+=4) std::swap(pixels[offset],pixels[offset+2]);
        ComPtr<IWICImagingFactory> wic; check(CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&wic)), "Initialize PNG encoder");
        ComPtr<IWICStream> stream; check(wic->CreateStream(&stream), "Create screenshot stream");
        check(stream->InitializeFromFilename(path.c_str(),GENERIC_WRITE), "Open screenshot file");
        ComPtr<IWICBitmapEncoder> encoder; check(wic->CreateEncoder(GUID_ContainerFormatPng,nullptr,&encoder), "Create PNG encoder");
        check(encoder->Initialize(stream.Get(),WICBitmapEncoderNoCache), "Initialize PNG encoder");
        ComPtr<IWICBitmapFrameEncode> frame; check(encoder->CreateNewFrame(&frame,nullptr), "Create PNG frame");
        check(frame->Initialize(nullptr), "Initialize PNG frame"); check(frame->SetSize(td.Width,td.Height), "Size PNG frame");
        auto format=GUID_WICPixelFormat32bppBGRA;
        check(frame->SetPixelFormat(&format), "Set PNG pixel format");
        if (format!=GUID_WICPixelFormat32bppBGRA) throw std::runtime_error("PNG encoder negotiated an unsupported pixel format");
        check(frame->WritePixels(td.Height,td.Width*4,static_cast<UINT>(pixels.size()),pixels.data()), "Encode screenshot pixels");
        check(frame->Commit(), "Commit PNG frame"); check(encoder->Commit(), "Commit PNG");
    }
    void next_file() {
        ComPtr<IWICImagingFactory> wic; check(CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&wic)),"Initialize image decoder");
        ComPtr<IWICBitmapDecoder> decoder; check(wic->CreateDecoderFromFilename(file_inputs.at(file_index).c_str(),nullptr,GENERIC_READ,WICDecodeMetadataCacheOnDemand,&decoder),"Decode conversion image");
        ComPtr<IWICBitmapFrameDecode> frame; check(decoder->GetFrame(0,&frame),"Decode image frame");
        UINT w=0,h=0; check(frame->GetSize(&w,&h),"Read conversion size"); screen::native_canvas(screen::processing_size(w,h,options.scale));
        ComPtr<IWICFormatConverter> converter; check(wic->CreateFormatConverter(&converter),"Create image conversion");
        check(converter->Initialize(frame.Get(),GUID_WICPixelFormat32bppBGRA,WICBitmapDitherTypeNone,nullptr,0,WICBitmapPaletteTypeCustom),"Convert image colors");
        std::vector<BYTE> data(static_cast<size_t>(w)*h*4); check(converter->CopyPixels(nullptr,w*4,static_cast<UINT>(data.size()),data.data()),"Read conversion image");
        if(width!=w || height!=h || !raw) rebuild(w,h);
        context->UpdateSubresource(raw.Get(),0,nullptr,data.data(),w*4,0); have_raw=true;
    }
    void media(ID3D11Texture2D* output) {
        if (options.virtual_camera) virtual_camera.send(device.Get(), context.Get(), output, static_cast<UINT>(std::lround(camera_input->rate)));
        else virtual_camera.stop();
        if(options.spout && !spout_open) {
            DisableSpoutLog();
            if(!spout_sender.OpenDirectX11(device.Get()) || !spout_sender.SetSenderName(options.spout_name.c_str())) throw std::runtime_error("Spout sender could not start");
            spout_sender.SetSenderFormat(options.hdr?DXGI_FORMAT_R16G16B16A16_FLOAT:DXGI_FORMAT_R8G8B8A8_UNORM); spout_open=true; spout_name=options.spout_name;
        }
        if(!options.spout && spout_open) { spout_sender.ReleaseSender(); spout_sender.CloseDirectX11(); spout_open=false; }
        if(spout_open && !spout_sender.SendTexture(output)) throw std::runtime_error("Spout texture publication failed");
    }
    int finish(const std::string& reason = "") {
        if (overlay) ShowWindow(overlay,SW_HIDE);
        if(spout_open) { spout_sender.ReleaseSender(); spout_sender.CloseDirectX11(); spout_open=false; }
        virtual_camera.stop(); camera_input.reset();
        auto error=reason;
        status(error.empty()?"stopped":"failed",error); return error.empty()?0:1;
    }
public:
    explicit Studio(fs::path directory) : root(std::move(directory)),initial(read_json(root/L"source.json")) {
        parent.value=OpenProcess(SYNCHRONIZE,FALSE,initial.at("parent_pid").get<DWORD>());
        if (!parent.value) throw std::runtime_error("Screen Studio owner is no longer running");
        load_options(true);
    }
    ~Studio() {
        // Stop publishing before releasing capture/resources. A job owns only our host.
        if (overlay) ShowWindow(overlay,SW_HIDE);
        virtual_camera.stop(); camera_input.reset();
        pipe.reset();
        try { if (capture) capture.Close(); if (pool) pool.Close(); } catch (...) { }
        if (overlay) DestroyWindow(overlay);
        if (job.value) TerminateJobObject(job.value,0);
    }
    int run() {
        try {
            status("starting"); initialize_gpu(); initialize_capture(); initialize_overlay(); launch_host();
            started=std::chrono::steady_clock::now();
            ULONGLONG options_tick=0;
            while (!stopping() && !camera_closed) {
                MSG msg{}; while (PeekMessageW(&msg,nullptr,0,0,PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
                if(GetTickCount64()-options_tick>=100) {
                    load_options(); options_tick=GetTickCount64();
                    if (!options.virtual_camera) virtual_camera.stop();
                    // Output shutdown must not depend on receiving another capture frame.
                    if(spout_open && (!options.spout || spout_name!=options.spout_name)) { spout_sender.ReleaseSender(); spout_sender.CloseDirectX11(); spout_open=false; }
                }
                if (!host_alive()) throw std::runtime_error("Phoenix DLSS5 screen host exited");
                if (!position_overlay()) { if (!paused || GetTickCount64()-status_time>500) status("paused"); paused=true; Sleep(100); continue; }
                if (paused) { paused=false; reset=true; }
                if(offline()) { if(file_index==file_inputs.size()) { status("completed"); return 0; } next_file(); }
                Direct3D11CaptureFrame frame{nullptr}; if(pool) frame=pool.TryGetNextFrame();
                bool camera_frame = false;
                if (camera()) {
                    camera_frame = camera_input->frame(camera_pixels);
                    if (camera_frame) {
                        if (!raw || width != camera_input->width || height != camera_input->height) rebuild(camera_input->width, camera_input->height);
                        context->UpdateSubresource(raw.Get(),0,nullptr,camera_pixels.data(),width*4,0); have_raw=true;
                    }
                }
                if (!offline() && !frame && !camera_frame && (!have_raw || (options.revision==evaluated_revision && options.screenshot<=saved_screenshot))) {
                    if (GetTickCount64()-status_time>1000) status(frames?"running":"waiting-for-frame"); Sleep(4); continue;
                }
                const auto begin=GetTickCount64();
                if(frame) {
                const auto size=frame.ContentSize();
                if (size.Width<1 || size.Height<1) { frame.Close(); Sleep(20); continue; }
                auto surface=frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
                ComPtr<ID3D11Texture2D> captured; check(surface->GetInterface(IID_PPV_ARGS(&captured)), "Read Windows capture surface");
                D3D11_TEXTURE2D_DESC desc{}; captured->GetDesc(&desc);
                if (desc.Width<static_cast<UINT>(size.Width) || desc.Height<static_cast<UINT>(size.Height) ||
                    static_cast<UINT>(size.Width)!=width || static_cast<UINT>(size.Height)!=height) {
                    // Resize the frame pool before using a potentially clipped old surface.
                    const auto next=frame.ContentSize(); frame.Close();
                    pool.Recreate(capture_device,capture_format(),2,next);
                    rebuild(next.Width,next.Height); continue;
                }
                D3D11_BOX box{0,0,0,width,height,1}; context->CopySubresourceRegion(raw.Get(),0,0,0,0,captured.Get(),0,&box);
                frame.Close(); have_raw=true;
                }
                const auto processing=screen::processing_size(width,height,options.scale);
                if (processing.width!=image_width || processing.height!=image_height) rebuild(width,height);
                draw(textures[FEED_COLOR].Get(),false);
                const auto value=frames+1;
                check(context4->Signal(input_fence.Get(),value), "Signal captured input"); context->Flush();
                FeedFrameMsg request{value,reset?1u:0u,options.enabled?1u:0u,static_cast<uint32_t>(options.iterations),0};
                send('F',request); wait_output(value); reset=false;
                ComPtr<ID3D11Texture2D> output; check(swapchain->GetBuffer(0,IID_PPV_ARGS(&output)), "Get presentation buffer");
                draw(output.Get(),true);
                if(offline()) {
                    fs::create_directories(root/L"converted"); wchar_t name[40]; swprintf_s(name,L"%09llu.png",static_cast<unsigned long long>(file_index+1));
                    png(output.Get(),root/L"converted"/name); ++file_index;
                } else media(output.Get());
                if (options.screenshot>saved_screenshot) {
                    fs::create_directories(root/L"screenshots");
                    const auto prefix=root/L"screenshots"/std::to_wstring(options.screenshot);
                    auto snapshot=make_texture(width,height,DXGI_FORMAT_R8G8B8A8_UNORM,false);
                    if(options.hdr) { draw(snapshot.Get(),false,true); png(snapshot.Get(),prefix.wstring()+L"-normal.png"); }
                    else png(raw.Get(),prefix.wstring()+L"-normal.png");
                    auto cropped=make_texture(image_width,image_height,DXGI_FORMAT_R8G8B8A8_UNORM,false);
                    D3D11_BOX image_box{0,0,0,image_width,image_height,1};
                    context->CopySubresourceRegion(cropped.Get(),0,0,0,0,textures[FEED_OUTPUT].Get(),0,&image_box);
                    png(cropped.Get(),prefix.wstring()+L"-enhanced.png");
                    if(options.hdr) { draw(snapshot.Get(),true); png(snapshot.Get(),prefix.wstring()+L"-view.png"); }
                    else png(output.Get(),prefix.wstring()+L"-view.png"); saved_screenshot=options.screenshot;
                }
                // Show completed output before Present, including the first/static frame.
                position_overlay(true);
                last_present=swapchain->Present(0,0);
                check(last_present, "Present enhanced screen frame");
                if (last_present==S_OK) ++presented_frames;
                ++frames; evaluated_revision=options.revision;
                if (GetTickCount64()-status_time>500) status("running");
                if (options.fps>0) { const auto elapsed=GetTickCount64()-begin; const auto budget=(1000u+options.fps-1)/options.fps;
                    if (elapsed<budget) Sleep(static_cast<DWORD>(budget-elapsed)); }
            }
            return finish();
        } catch (const std::exception& error) {
            return finish(stopping()?"":error.what());
        } catch (const winrt::hresult_error& error) {
            return finish(winrt::to_string(error.message()));
        }
    }
};

int wmain(int argc,wchar_t** argv) {
    if (argc == 3 && std::wstring(argv[1]) == L"--camera-modes") {
        try {
            winrt::init_apartment(winrt::apartment_type::multi_threaded);
            std::cout << Json({{"schema",1},{"modes",camera_modes(winrt::to_string(argv[2]))}}).dump(); return 0;
        } catch (const std::exception& error) { std::cout << Json({{"schema",1},{"error",error.what()}}).dump(); return 1; }
        catch (const winrt::hresult_error& error) { std::cout << Json({{"schema",1},{"error",winrt::to_string(error.message())}}).dump(); return 1; }
    }
    if (argc!=2) return 2;
    if (std::wstring(argv[1]) == L"--list-cameras") {
        try {
            winrt::init_apartment(winrt::apartment_type::multi_threaded);
            std::cout << Json({{"schema",1},{"devices",camera_devices()}}).dump(); return 0;
        } catch (const std::exception& error) { std::cout << Json({{"schema",1},{"error",error.what()}}).dump(); return 1; }
    }
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    const auto fail = [&](const std::string& error) {
        try { atomic_json(fs::absolute(argv[1])/L"status.json",{{"schema",1},{"state","failed"},{"error",error},
            {"frames",0},{"width",0},{"height",0},{"processing_width",0},{"processing_height",0},
            {"average_fps",0},{"applied_revision",0},{"screenshot",0}}); } catch (...) { }
        std::cerr<<error<<'\n'; return 1;
    };
    try { winrt::init_apartment(winrt::apartment_type::multi_threaded); Studio studio(fs::absolute(argv[1])); return studio.run(); }
    catch (const winrt::hresult_error& error) { return fail(winrt::to_string(error.message())); }
    catch (const std::exception& error) { return fail(error.what()); }
}
