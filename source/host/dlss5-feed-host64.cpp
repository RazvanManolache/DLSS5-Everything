// dlss5-feed-host64 - the 64-bit half of DLSS5-Feeder for 32-bit games.
//
// The host loads ReShade x64 and Phoenix.addon64 with a minimal D3D12 swapchain.
// It sends native-resolution SDR frames directly to the versioned Phoenix feeder
// export, using shared textures/fences or CPU pipe transport. No SR stage runs.
//
//   dlss5-feed-host64.exe --test   stand-alone: synthetic pattern, no game needed
//                                  (requires Phoenix writeback and completion evidence)
//   dlss5-feed-host64.exe --image input.png output.png [frames]
//                                  internal no-game static-image diagnostic probe
//   dlss5-feed-host64.exe --probe-vr output_w output_h [render_scale]
//                                  legacy sizing probe (rejected; direct route is 1:1)
//   dlss5-feed-host64.exe <pid>    serve the game with that PID over the pipe
//
// Logs to dlss5-feed-host.log next to the exe; the DLSS 5 add-on's own state
// appears in the host's ReShade.log.

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <cstdio>
#include <cstdarg>
#include <cstdint>
#include <cstring>
#include <cstdlib>
#include <cctype>
#include <vector>
#include <string>
#include <wincodec.h>

#include "phoenix-host-config.h"
#include "phoenix-host-pixels.h"
#include "phoenix-format-adapter.h"
#include "phoenix-host-safety.h"
#include "../external/reshade/include/reshade.hpp"
#include <algorithm>

#include "phoenix-feeder-api.h"

#include "../src/feed_ipc.h"

// ---------------------------------------------------------------------------
// Logging
// ---------------------------------------------------------------------------

static char g_log_path[MAX_PATH];
static bool g_show_window = false;   // visible host window = the user's door to the DLSS 5 panel
static bool g_phoenix_enabled = true;
static unsigned g_frame_iterations = 0; // Optional host override for OpenXR's fixed IPC count.
static PhoenixFeederEvaluateProc g_feeder_evaluate = nullptr;
static PhoenixFeederEvaluateLockedProc g_feeder_evaluate_locked = nullptr;
static uint64_t g_capture_sequence = 0;
static bool g_reference_cpu = false;
static std::vector<ID3D12CommandQueue *> g_observed_queues;
static std::vector<ID3D12GraphicsCommandList *> g_observed_lists;
static void Log(const char *fmt, ...);

static bool ConfigurePhoenixHost()
{
    try {
        wchar_t executable[32768]{};
        if (!GetModuleFileNameW(nullptr, executable, 32768)) return false;
        const auto directory = std::filesystem::path(executable).parent_path();
        const auto config_path = directory / L"Phoenix.json";
        std::ifstream input(config_path);
        nlohmann::json config;
        input >> config;
        g_phoenix_enabled = ValidatePhoenixHostConfig(config, directory);
        wchar_t selected_device[256]{};
        GetPrivateProfileStringW(L"Phoenix", L"ExecutionDevice", L"native", selected_device, 256, (directory / L"ReShade.ini").c_str());
        g_reference_cpu = std::wcscmp(selected_device, L"cpu") == 0;
        g_frame_iterations = GetPrivateProfileIntW(L"DLSS5_HOST", L"FrameIterations", 0, (directory / L"ReShade.ini").c_str());
        (void)PhoenixHostIterations(1, g_frame_iterations);
        if (!std::filesystem::is_regular_file(directory / L"Phoenix.addon64"))
            throw std::runtime_error("Phoenix.addon64 is missing");
        // Do not inherit another game's sidecar selection into this process.
        if (!SetEnvironmentVariableW(L"CNR_PHOENIX_GAME_CONFIG", config_path.c_str())) return false;
        Log("[host] phoenix-host/2: direct Phoenix enhancement; SDR 1:1; armed=%d", g_phoenix_enabled);
        return true;
    } catch (const std::exception& error) {
        Log("[host] Phoenix configuration rejected: %s", error.what());
        return false;
    }
}

static void ObserveHostList(reshade::api::command_list *list)
{
    if (list->get_device()->get_api() == reshade::api::device_api::d3d12)
        g_observed_lists.push_back(reinterpret_cast<ID3D12GraphicsCommandList *>(list->get_native()));
}

static void ForgetHostList(reshade::api::command_list *list)
{
    auto *native = reinterpret_cast<ID3D12GraphicsCommandList *>(list->get_native());
    g_observed_lists.erase(std::remove(g_observed_lists.begin(), g_observed_lists.end(), native), g_observed_lists.end());
}

// Match the wrapper and observed native list with private data, without assuming
// a particular ReShade proxy ABI. Phoenix receives the exact observed list.
static ID3D12GraphicsCommandList *NativeHostList(ID3D12GraphicsCommandList *supplied)
{
    static const GUID cookie_id = {0x213df3aa, 0x679a, 0x41ca, {0xb2,0x50,0x72,0x6a,0x6d,0x55,0x63,0x01}};
    static UINT64 counter = 0;
    const UINT64 cookie = ++counter;
    if (FAILED(supplied->SetPrivateData(cookie_id, sizeof(cookie), &cookie))) return nullptr;
    ID3D12GraphicsCommandList *found = nullptr;
    unsigned matches = 0;
    for (auto *list : g_observed_lists) {
        UINT64 candidate = 0; UINT bytes = sizeof(candidate);
        if (SUCCEEDED(list->GetPrivateData(cookie_id, &bytes, &candidate)) && bytes == sizeof(candidate) && candidate == cookie) {
            found = list; ++matches;
        }
    }
    supplied->SetPrivateData(cookie_id, 0, nullptr);
    return matches == 1 ? found : nullptr;
}

static void Log(const char *fmt, ...)
{
    char line[2048];
    va_list ap;
    va_start(ap, fmt);
    _vsnprintf_s(line, sizeof(line), _TRUNCATE, fmt, ap);
    va_end(ap);
    SYSTEMTIME st;
    GetLocalTime(&st);
    printf("%02u:%02u:%02u.%03u  %s\n", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, line);
    FILE *f = nullptr;
    if (fopen_s(&f, g_log_path, "a") == 0 && f != nullptr)
    {
        fprintf(f, "%02u:%02u:%02u.%03u  %s\n", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, line);
        fclose(f);
    }
}

static void PumpPresent();

static void ObserveHostQueue(reshade::api::command_queue *queue)
{
    if (queue->get_device()->get_api() == reshade::api::device_api::d3d12)
        g_observed_queues.push_back(reinterpret_cast<ID3D12CommandQueue *>(queue->get_native()));
}

static void ForgetHostQueue(reshade::api::command_queue *queue)
{
    auto *native = reinterpret_cast<ID3D12CommandQueue *>(queue->get_native());
    g_observed_queues.erase(std::remove(g_observed_queues.begin(), g_observed_queues.end(), native), g_observed_queues.end());
}

static ID3D12CommandQueue *NativeHostQueue(ID3D12CommandQueue *supplied)
{
    static const GUID cookie_id = {0x213df3aa, 0x679a, 0x41ca, {0xb2,0x50,0x72,0x6a,0x6d,0x55,0x63,0x02}};
    static UINT64 counter = 0;
    const UINT64 cookie = ++counter;
    if (FAILED(supplied->SetPrivateData(cookie_id, sizeof(cookie), &cookie))) return nullptr;
    ID3D12CommandQueue *found = nullptr;
    unsigned matches = 0;
    for (auto *queue : g_observed_queues) {
        UINT64 candidate = 0; UINT bytes = sizeof(candidate);
        if (SUCCEEDED(queue->GetPrivateData(cookie_id, &bytes, &candidate)) && bytes == sizeof(candidate) && candidate == cookie) {
            found = queue; ++matches;
        }
    }
    supplied->SetPrivateData(cookie_id, 0, nullptr);
    return matches == 1 ? found : nullptr;
}

static bool ResolvePhoenixFeeder(HMODULE module)
{
    g_feeder_evaluate = nullptr;
    g_feeder_evaluate_locked = nullptr;
    auto version = module ? reinterpret_cast<PhoenixFeederVersionProc>(GetProcAddress(module, "CnrPHOENIXFeederVersion")) : nullptr;
    auto evaluate = module ? reinterpret_cast<PhoenixFeederEvaluateProc>(GetProcAddress(module, "CnrPHOENIXFeederEvaluate")) : nullptr;
    if (!version || !evaluate) return false;
    const auto abi = version();
    if (abi != PhoenixFeederAbiV1) {
        Log("[host] Phoenix feeder ABI mismatch: addon=%u host=%u", abi, PhoenixFeederAbiV1);
        return false;
    }
    g_feeder_evaluate = evaluate;
    g_feeder_evaluate_locked = reinterpret_cast<PhoenixFeederEvaluateLockedProc>(GetProcAddress(module, "CnrPHOENIXFeederEvaluateLocked"));
    return true;
}

static void LogFeederModules(const char *stage)
{
    const bool loaded = GetModuleHandleW(L"nvngx_dlss.dll") != nullptr;
    static int last_loaded = -1;
    if (last_loaded != static_cast<int>(loaded))
        Log("[host] loadedmodules (%s): nvngx_dlss.dll=%s; provider=feeder_d3d12", stage, loaded ? "LOADED" : "absent");
    last_loaded = loaded;
}

static bool WaitForPhoenixFeeder(DWORD timeout_ms)
{
    const UINT64 deadline = GetTickCount64() + timeout_ms;
    do {
        PumpPresent();
        auto module = GetModuleHandleW(L"Phoenix.addon64");
        if (ResolvePhoenixFeeder(module)) {
            Log("[host] Phoenix direct feeder ABI %u available (inference not yet verified)", PhoenixFeederAbiV1);
            return true;
        }
        if (module) break;
        Sleep(16);
    } while (GetTickCount64() < deadline);
    Log("[host] Phoenix.addon64 lacks CnrPHOENIXFeederVersion/CnrPHOENIXFeederEvaluate; direct feeder requires ABI 1");
    return false;
}

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------

struct Host
{
    HWND                       hwnd;
    IDXGISwapChain1           *swap;
    ID3D12Device              *dev;
    ID3D12CommandQueue        *queue;      // Phoenix work
    ID3D12CommandQueue        *pump_queue; // owns the dummy swapchain
    ID3D12GraphicsCommandList *list;
    static const int           kFrames = 3;
    ID3D12CommandAllocator    *alloc[kFrames];
    UINT64                     alloc_fence[kFrames];
    int                        frame_slot;
    ID3D12Fence               *fence;      // internal (allocator ring)
    HANDLE                     fence_event;
    UINT64                     fence_value;

    // cross-process
    ID3D12Fence *fence_in;   // game signals, host waits
    ID3D12Fence *fence_out;  // host signals, game waits

    bool feeder_ready;

    ID3D12Resource *tex[FEED_SLOTS];
    ID3D12Resource *local_tex[FEED_SLOTS];
    ID3D12Resource *eye_tex[2][FEED_SLOTS];
    UINT            width, height;
    UINT            input_width, input_height;
    bool            split_eyes;
    bool            game_side_vr_split;
    UINT            eye_width, eye_height;
    UINT            eye_input_width, eye_input_height;
    DXGI_FORMAT     color_fmt, output_fmt;
    bool            has_mask;
    bool            depth_inverted;
    bool            cpu_bridge;
    ID3D12Resource *iter_scratch[2];
    ID3D12Resource *eye_scratch[2][2];
};

static Host h;
static PhoenixFormatAdapter g_format_adapter;
static bool g_host_terminal = false;
static bool g_queue_retirement_unproven = false;

static void HostTerminal(const char* reason)
{
    g_host_terminal = true;
    Log("[host] TERMINAL: %s; no shared output readiness will be published", reason);
    try {
        wchar_t executable[32768]{};
        if (GetModuleFileNameW(nullptr, executable, 32768)) {
            std::ofstream file(std::filesystem::path(executable).parent_path() / "phoenix-host-terminal.json");
            file << nlohmann::json{{"terminal", true}, {"error", reason}, {"output_ready_published", false}}.dump(2);
        }
    } catch (...) {}
}

static bool WaitFenceValue(ID3D12Fence *f, UINT64 v, DWORD ms);

template <typename T>
static void ReleasePtr(T *&p)
{
    if (p != nullptr) { p->Release(); p = nullptr; }
}

// ---------------------------------------------------------------------------
// Command submission (allocator ring), same shape as the add-on
// ---------------------------------------------------------------------------

static bool BeginCommands()
{
    if (g_host_terminal || !h.list) return false;
    const int slot = h.frame_slot;
    const UINT64 retire = h.alloc_fence[slot];
    if (retire != 0 && h.fence->GetCompletedValue() < retire)
    {
        if (!WaitFenceValue(h.fence, retire, 2000))
        { Log("[host] GPU did not retire allocator slot %d", slot); return false; }
    }
    if (FAILED(h.alloc[slot]->Reset())) return false;
    return SUCCEEDED(h.list->Reset(h.alloc[slot], nullptr));
}

static UINT64 EndCommands()
{
    const UINT64 v = h.fence_value + 1;
    bool closed = false;
    const bool submitted = PhoenixSubmitCommands(
        [&] { closed = SUCCEEDED(h.list->Close()); return closed; },
        [&] {
            ID3D12CommandList *lists[] = { h.list };
            h.queue->ExecuteCommandLists(1, lists);
            h.fence_value = v;
        },
        [&] { return SUCCEEDED(h.queue->Signal(h.fence, v)); });
    if (!submitted) {
        g_queue_retirement_unproven = closed;
        HostTerminal(closed ? "Command queue fence signal failed" : "Command list Close failed; list not submitted");
        return kPhoenixFailedSubmission;
    }
    h.alloc_fence[h.frame_slot] = v;
    h.frame_slot = (h.frame_slot + 1) % Host::kFrames;
    return v;
}

static bool WaitFenceValue(ID3D12Fence *f, UINT64 v, DWORD ms)
{
    if (!f) return false;
    return PhoenixWaitFence(v, ms,
        [&] { return f->GetCompletedValue(); },
        [&] { return SUCCEEDED(f->SetEventOnCompletion(v, h.fence_event)); },
        [&](uint32_t remaining) { return WaitForSingleObject(h.fence_event, remaining) == WAIT_OBJECT_0; },
        [] { return GetTickCount64(); });
}

static void CloseListGuarded()
{
    __try { h.list->Close(); } __except (EXCEPTION_EXECUTE_HANDLER) {}
}

static void AbortCommands()   // discard failed/deferred recordings without submission
{
    if (h.list == nullptr) return;
    CloseListGuarded();
    h.list->Release();
    h.list = nullptr;
    if (SUCCEEDED(h.dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, h.alloc[h.frame_slot], nullptr,
                                           __uuidof(ID3D12GraphicsCommandList), reinterpret_cast<void **>(&h.list))))
        h.list->Close();
}

static HRESULT SafeEvaluatePhoenix(const PhoenixFeederFrameV1 *frame, PhoenixFeederResultV1 *result, DWORD *code, uint64_t capture_token = 0)
{
    *code = 0;
    __try { return g_feeder_evaluate_locked && capture_token ? g_feeder_evaluate_locked(frame,result,capture_token) : g_feeder_evaluate(frame, result); }
    __except (EXCEPTION_EXECUTE_HANDLER) { *code = GetExceptionCode(); return E_UNEXPECTED; }
}

static bool ValidFeederReply(HRESULT hr, const PhoenixFeederFrameV1& frame, const PhoenixFeederResultV1& result)
{
    return PhoenixFeederContractV1(frame, result) &&
        ((hr == S_FALSE && result.status == PhoenixFeederStatusV1::Deferred) ||
         (hr == S_OK && (result.status == PhoenixFeederStatusV1::Recorded || result.status == PhoenixFeederStatusV1::Bypass)));
}

// ---------------------------------------------------------------------------
// The disguise: hidden window + minimal D3D12 swapchain so ReShade x64 loads
// and the DLSS 5 add-on arms itself, exactly as in a real D3D12 game.
// ---------------------------------------------------------------------------

static LRESULT CALLBACK WndProc(HWND w, UINT m, WPARAM wp, LPARAM lp)
{
    if (m == WM_CLOSE) { ShowWindow(w, SW_HIDE); return 0; }   // closing only hides; the feed lives on
    return DefWindowProcW(w, m, wp, lp);
}

// --- banner: "32-bit DLSS 5 Feeder" rendered once with GDI, copied into every frame ---

static ID3D12Resource             *g_banner;
static IDXGISwapChain3            *g_swap3;
static ID3D12CommandAllocator     *g_pump_alloc;
static ID3D12GraphicsCommandList  *g_pump_list;
static ID3D12Fence                *g_pump_fence;
static UINT64                      g_pump_val;
static HANDLE                      g_pump_ev;

static bool BeginCommands();
static UINT64 EndCommands();
static bool WaitFenceValue(ID3D12Fence *f, UINT64 v, DWORD ms);

static void InitBanner()
{
    const int W = 960, H = 540;

    // 1. Render the text with GDI into a 32-bit DIB.
    BITMAPINFO bi = {};
    bi.bmiHeader.biSize        = sizeof(bi.bmiHeader);
    bi.bmiHeader.biWidth       = W;
    bi.bmiHeader.biHeight      = -H;   // top-down
    bi.bmiHeader.biPlanes      = 1;
    bi.bmiHeader.biBitCount    = 32;
    bi.bmiHeader.biCompression = BI_RGB;
    void *bits = nullptr;
    HDC dc = CreateCompatibleDC(nullptr);
    HBITMAP bmp = CreateDIBSection(dc, &bi, DIB_RGB_COLORS, &bits, nullptr, 0);
    if (dc == nullptr || bmp == nullptr || bits == nullptr) return;
    HGDIOBJ old_bmp = SelectObject(dc, bmp);

    RECT full = { 0, 0, W, H };
    HBRUSH bg = CreateSolidBrush(RGB(18, 18, 22));
    FillRect(dc, &full, bg);
    DeleteObject(bg);
    SetBkMode(dc, TRANSPARENT);

    HFONT fnt_big   = CreateFontW(64, 0, 0, 0, FW_BOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET, 0, 0,
                                  CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
    HFONT fnt_small = CreateFontW(26, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, 0, 0,
                                  CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
    HGDIOBJ old_font = SelectObject(dc, fnt_big);
    SetTextColor(dc, RGB(118, 185, 0));
    RECT r1 = { 0, 150, W, 240 };
    DrawTextW(dc, L"32-bit DLSS 5 Feeder", -1, &r1, DT_CENTER | DT_SINGLELINE | DT_VCENTER);
    SelectObject(dc, fnt_small);
    SetTextColor(dc, RGB(200, 200, 205));
    RECT r2 = { 0, 260, W, 300 };
    DrawTextW(dc, L"DLSS 5 neural rendering runs here for your 32-bit game.", -1, &r2,
              DT_CENTER | DT_SINGLELINE | DT_VCENTER);
    RECT r3 = { 0, 305, W, 345 };
    DrawTextW(dc, L"Press  Home  in this window to tune it  \x2022  closing only hides the window", -1, &r3,
              DT_CENTER | DT_SINGLELINE | DT_VCENTER);
    SelectObject(dc, old_font);
    DeleteObject(fnt_big);
    DeleteObject(fnt_small);
    GdiFlush();

    // 2. Upload it (BGRA -> RGBA) and keep it as a copy source.
    D3D12_HEAP_PROPERTIES up = {};
    up.Type = D3D12_HEAP_TYPE_UPLOAD;
    const UINT pitch = (W * 4 + 255) & ~255u;
    D3D12_RESOURCE_DESC bd = {};
    bd.Dimension        = D3D12_RESOURCE_DIMENSION_BUFFER;
    bd.Width            = static_cast<UINT64>(pitch) * H;
    bd.Height           = 1;
    bd.DepthOrArraySize = 1;
    bd.MipLevels        = 1;
    bd.SampleDesc.Count = 1;
    bd.Layout           = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    ID3D12Resource *staging = nullptr;
    D3D12_HEAP_PROPERTIES def = {};
    def.Type = D3D12_HEAP_TYPE_DEFAULT;
    D3D12_RESOURCE_DESC td = {};
    td.Dimension        = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    td.Width            = W;
    td.Height           = H;
    td.DepthOrArraySize = 1;
    td.MipLevels        = 1;
    td.Format           = DXGI_FORMAT_R8G8B8A8_UNORM;
    td.SampleDesc.Count = 1;
    td.Layout           = D3D12_TEXTURE_LAYOUT_UNKNOWN;
    if (FAILED(h.dev->CreateCommittedResource(&up, D3D12_HEAP_FLAG_NONE, &bd, D3D12_RESOURCE_STATE_GENERIC_READ,
                                              nullptr, __uuidof(ID3D12Resource), reinterpret_cast<void **>(&staging))) ||
        FAILED(h.dev->CreateCommittedResource(&def, D3D12_HEAP_FLAG_NONE, &td, D3D12_RESOURCE_STATE_COPY_DEST,
                                              nullptr, __uuidof(ID3D12Resource), reinterpret_cast<void **>(&g_banner))))
    { SelectObject(dc, old_bmp); DeleteObject(bmp); DeleteDC(dc); return; }

    BYTE *dst = nullptr;
    staging->Map(0, nullptr, reinterpret_cast<void **>(&dst));
    const BYTE *srcp = static_cast<const BYTE *>(bits);
    for (int y = 0; y < H; ++y)
        for (int x = 0; x < W; ++x)
        {
            const BYTE *p = srcp + (static_cast<size_t>(y) * W + x) * 4;   // GDI: BGRA
            BYTE *q = dst + static_cast<size_t>(y) * pitch + static_cast<size_t>(x) * 4;
            q[0] = p[2]; q[1] = p[1]; q[2] = p[0]; q[3] = 0xFF;
        }
    staging->Unmap(0, nullptr);
    SelectObject(dc, old_bmp);
    DeleteObject(bmp);
    DeleteDC(dc);

    if (BeginCommands())
    {
        D3D12_TEXTURE_COPY_LOCATION src = {}, dcl = {};
        src.pResource = staging;
        src.Type      = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
        src.PlacedFootprint.Footprint.Format   = DXGI_FORMAT_R8G8B8A8_UNORM;
        src.PlacedFootprint.Footprint.Width    = W;
        src.PlacedFootprint.Footprint.Height   = H;
        src.PlacedFootprint.Footprint.Depth    = 1;
        src.PlacedFootprint.Footprint.RowPitch = pitch;
        dcl.pResource = g_banner;
        dcl.Type      = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        h.list->CopyTextureRegion(&dcl, 0, 0, 0, &src, nullptr);
        D3D12_RESOURCE_BARRIER b = {};
        b.Type                   = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Transition.pResource   = g_banner;
        b.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
        b.Transition.StateAfter  = D3D12_RESOURCE_STATE_COPY_SOURCE;
        b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        h.list->ResourceBarrier(1, &b);
        const UINT64 v = EndCommands();
        WaitFenceValue(h.fence, v, 2000);
    }
    staging->Release();

    // 3. A tiny allocator/list/fence pair on the pump queue for the per-frame copy.
    h.dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, __uuidof(ID3D12CommandAllocator),
                                  reinterpret_cast<void **>(&g_pump_alloc));
    if (g_pump_alloc != nullptr)
        h.dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, g_pump_alloc, nullptr,
                                 __uuidof(ID3D12GraphicsCommandList), reinterpret_cast<void **>(&g_pump_list));
    if (g_pump_list != nullptr) g_pump_list->Close();
    h.dev->CreateFence(0, D3D12_FENCE_FLAG_NONE, __uuidof(ID3D12Fence), reinterpret_cast<void **>(&g_pump_fence));
    g_pump_ev = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    h.swap->QueryInterface(__uuidof(IDXGISwapChain3), reinterpret_cast<void **>(&g_swap3));
    Log("[host] banner ready");
}

typedef HRESULT (WINAPI *PFN_D3D12CreateDevice_)(IUnknown *, D3D_FEATURE_LEVEL, REFIID, void **);
typedef HRESULT (WINAPI *PFN_CreateDXGIFactory1_)(REFIID, void **);

static void PumpPresent()
{
    MSG msg;
    while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
    if (h.swap == nullptr) return;

    // Paint the banner into the backbuffer (ReShade's overlay composites on top at Present).
    if (g_banner != nullptr && g_pump_list != nullptr && g_swap3 != nullptr)
    {
        ID3D12Resource *bb = nullptr;
        if (SUCCEEDED(g_swap3->GetBuffer(g_swap3->GetCurrentBackBufferIndex(), __uuidof(ID3D12Resource),
                                         reinterpret_cast<void **>(&bb))) && bb != nullptr)
        {
            if (SUCCEEDED(g_pump_alloc->Reset()) && SUCCEEDED(g_pump_list->Reset(g_pump_alloc, nullptr)))
            {
                D3D12_RESOURCE_BARRIER b = {};
                b.Type                   = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
                b.Transition.pResource   = bb;
                b.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
                b.Transition.StateAfter  = D3D12_RESOURCE_STATE_COPY_DEST;
                b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
                g_pump_list->ResourceBarrier(1, &b);
                g_pump_list->CopyResource(bb, g_banner);
                b.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
                b.Transition.StateAfter  = D3D12_RESOURCE_STATE_PRESENT;
                g_pump_list->ResourceBarrier(1, &b);
                g_pump_list->Close();
                ID3D12CommandList *lists[] = { g_pump_list };
                h.pump_queue->ExecuteCommandLists(1, lists);
                h.pump_queue->Signal(g_pump_fence, ++g_pump_val);
                if (g_pump_fence->GetCompletedValue() < g_pump_val && g_pump_ev != nullptr)
                {
                    g_pump_fence->SetEventOnCompletion(g_pump_val, g_pump_ev);
                    WaitForSingleObject(g_pump_ev, 100);
                }
            }
            bb->Release();
        }
    }
    h.swap->Present(0, 0);
}

static bool InitDisguise()
{
    // ReShade first: the app-directory dxgi.dll IS ReShade x64. Loading it before
    // d3d12.dll means every later D3D12/DXGI entry point goes through its hooks --
    // the same order a real game gets, and what lets the DLSS 5 add-on see us.
    HMODULE dxgi = LoadLibraryW(L"dxgi.dll");
    if (!dxgi || !reshade::register_addon(GetModuleHandleW(nullptr))) {
        Log("[host] ReShade host observer registration failed"); return false;
    }
    reshade::register_event<reshade::addon_event::init_command_list>(ObserveHostList);
    reshade::register_event<reshade::addon_event::destroy_command_list>(ForgetHostList);
    reshade::register_event<reshade::addon_event::init_command_queue>(ObserveHostQueue);
    reshade::register_event<reshade::addon_event::destroy_command_queue>(ForgetHostQueue);
    HMODULE d3d12 = LoadLibraryW(L"d3d12.dll");
    auto create_device  = d3d12 ? reinterpret_cast<PFN_D3D12CreateDevice_>(GetProcAddress(d3d12, "D3D12CreateDevice")) : nullptr;
    auto create_factory = dxgi ? reinterpret_cast<PFN_CreateDXGIFactory1_>(GetProcAddress(dxgi, "CreateDXGIFactory1")) : nullptr;
    if (create_device == nullptr || create_factory == nullptr) { Log("[host] dxgi/d3d12 exports missing"); return false; }

    wchar_t exe[MAX_PATH] = {};
    GetModuleFileNameW(dxgi, exe, MAX_PATH);
    Log("[host] dxgi.dll: %ls", exe);

    WNDCLASSW wc = {};
    wc.lpfnWndProc   = WndProc;
    wc.hInstance     = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"dlss5feedhost";
    RegisterClassW(&wc);
    h.hwnd = CreateWindowExW(0, wc.lpszClassName,
                             L"DLSS 5 Feed host - press Home HERE to tune DLSS 5 neural rendering",
                             WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT, 960, 540,
                             nullptr, nullptr, wc.hInstance, nullptr);
    if (h.hwnd == nullptr) { Log("[host] window creation failed"); return false; }
    if (g_show_window) ShowWindow(h.hwnd, SW_SHOWNOACTIVATE);   // never steal the game's focus

    HRESULT hr = create_device(nullptr, D3D_FEATURE_LEVEL_11_0, __uuidof(ID3D12Device),
                               reinterpret_cast<void **>(&h.dev));
    if (FAILED(hr)) { Log("[host] D3D12CreateDevice failed 0x%08X", hr); return false; }

    D3D12_COMMAND_QUEUE_DESC qd = {};
    h.dev->CreateCommandQueue(&qd, __uuidof(ID3D12CommandQueue), reinterpret_cast<void **>(&h.pump_queue));
    h.dev->CreateCommandQueue(&qd, __uuidof(ID3D12CommandQueue), reinterpret_cast<void **>(&h.queue));
    if (h.pump_queue == nullptr || h.queue == nullptr) { Log("[host] queue creation failed"); return false; }

    IDXGIFactory2 *factory = nullptr;
    hr = create_factory(__uuidof(IDXGIFactory2), reinterpret_cast<void **>(&factory));
    if (FAILED(hr)) { Log("[host] CreateDXGIFactory1 failed 0x%08X", hr); return false; }

    DXGI_SWAP_CHAIN_DESC1 sd = {};
    sd.Width            = 960;
    sd.Height           = 540;
    sd.Format           = DXGI_FORMAT_R8G8B8A8_UNORM;
    sd.SampleDesc.Count = 1;
    sd.BufferUsage      = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    sd.BufferCount      = 2;
    sd.SwapEffect       = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    hr = factory->CreateSwapChainForHwnd(h.pump_queue, h.hwnd, &sd, nullptr, nullptr, &h.swap);
    factory->Release();
    if (FAILED(hr)) { Log("[host] CreateSwapChainForHwnd failed 0x%08X", hr); return false; }
    Log("[host] disguise up: hidden window + D3D12 swapchain (ReShade should be attached now)");

    for (int i = 0; i < 60; ++i) PumpPresent();   // let ReShade + the DLSS 5 add-on settle

    // Ring + internal fence for our own submissions.
    for (int i = 0; i < Host::kFrames; ++i)
        h.dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, __uuidof(ID3D12CommandAllocator),
                                      reinterpret_cast<void **>(&h.alloc[i]));
    if (h.alloc[0] != nullptr)
        h.dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, h.alloc[0], nullptr,
                                 __uuidof(ID3D12GraphicsCommandList), reinterpret_cast<void **>(&h.list));
    if (h.list != nullptr) h.list->Close();
    h.dev->CreateFence(0, D3D12_FENCE_FLAG_NONE, __uuidof(ID3D12Fence), reinterpret_cast<void **>(&h.fence));
    h.fence_event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (h.list == nullptr || h.fence == nullptr) { Log("[host] list/fence creation failed"); return false; }

    if (!NativeHostList(h.list)) { Log("[host] exact native list was not observed by ReShade"); return false; }
    if (!NativeHostQueue(h.queue)) { Log("[host] exact native queue was not observed by ReShade"); return false; }
    // Establish Phoenix's exact-list queue ownership before the first feeder call.
    if (!BeginCommands()) return false;
    const auto seed = EndCommands();
    if (!WaitFenceValue(h.fence, seed, 4000)) return false;
    if (g_show_window) InitBanner();
    return true;
}

static int g_last_nr_enabled[3] = {-1, -1, -1};
static bool g_last_evaluate_bypass = false;
static bool CopyTextureCommon(ID3D12Resource *src, ID3D12Resource *dst);

static bool ReadPhoenixOutputSummary(nlohmann::json& summary)
{
    try {
        using Summary = BOOL (WINAPI *)(const wchar_t*);
        auto module = GetModuleHandleW(L"Phoenix.addon64");
        auto progress = module ? reinterpret_cast<PhoenixFeederProgressJsonProc>(
                                     GetProcAddress(module, "CnrPHOENIXFeederProgressJson")) : nullptr;
        if (progress) {
            for (unsigned retry = 0; retry < 3; ++retry) {
                const auto bytes = progress(nullptr, 0);
                if (!bytes || bytes > 1024u * 1024u) return false;
                std::string text(bytes, '\0');
                const auto written = progress(text.data(), bytes);
                if (!written) return false;
                if (written > bytes) continue; // a background worker advanced
                summary = nlohmann::json::parse(text.c_str());
                return summary.at("schema") == "production-game-phoenix/2";
            }
            return false;
        }
        // Older published cores expose the compact panel snapshot but not the
        // dedicated progress export. Avoid synchronous diagnostic report I/O
        // for every retry/frame on those releases as well.
        using PanelQuery = uint32_t (WINAPI *)(char*, uint32_t);
        auto panel = module ? reinterpret_cast<PanelQuery>(GetProcAddress(module, "CnrPHOENIXFeederUiJson")) : nullptr;
        if (panel) {
            for (unsigned retry=0; retry<3; ++retry) {
                const auto bytes=panel(nullptr,0); if (!bytes || bytes>1024u*1024u) return false;
                std::string text(bytes,'\0'); const auto written=panel(text.data(),bytes);
                if (!written) return false; if (written>bytes) continue;
                summary=PhoenixCompactPanelProgress(nlohmann::json::parse(text.c_str()));
                return true;
            }
            return false;
        }
        auto report = module ? reinterpret_cast<Summary>(GetProcAddress(module, "CnrPHOENIXSummary")) : nullptr;
        wchar_t executable[32768]{};
        if (!report || !GetModuleFileNameW(nullptr, executable, 32768)) return false;
        const auto path = std::filesystem::path(executable).parent_path() / L"phoenix-host-output-summary.json";
        if (!report(path.c_str())) return false;
        std::ifstream file(path); file >> summary;
        return summary.at("schema") == "production-game-phoenix/2";
    } catch (const std::exception&) { return false; }
}

static bool EvaluateFloatForStream(uint32_t stream_id, ID3D12Resource *color, ID3D12Resource *output,
                                ID3D12Resource *depth, ID3D12Resource *mv, ID3D12Resource *mask,
                                UINT input_w, UINT input_h, int reset, int nr_enabled, float mvsx, float mvsy)
{
    (void)mask; // The direct feeder ABI uses color, motion and optional depth.
    if (stream_id > 2 || !color || !output || !mv || !g_feeder_evaluate) return false;
    const auto input = color->GetDesc(), destination = output->GetDesc();
    if (input.Width != input_w || input.Height != input_h ||
        input.Width != destination.Width || input.Height != destination.Height ||
        input.Format != destination.Format) {
        Log("[host] direct Phoenix DLSS5 requires matching float color/output at 1:1");
        return false;
    }
    g_last_evaluate_bypass = !nr_enabled || !g_phoenix_enabled;
    if (g_last_evaluate_bypass) {
        g_last_nr_enabled[stream_id] = 0;
        return CopyTextureCommon(color, output);
    }
    const bool nr_changed = g_last_nr_enabled[stream_id] != 1;
    g_last_nr_enabled[stream_id] = 1;
    nlohmann::json before, after;
    const bool summary_before_available = ReadPhoenixOutputSummary(before);
    std::string last_deferred_status;
    // Retries poll the same captured input. Repeating its history reset would
    // invalidate the portable executor's pending readback on every retry.
    bool reset_pending = reset || nr_changed;
    const auto capture_token = ++g_capture_sequence;
    const auto wait_ms = g_reference_cpu ? 55000u : 15000u;
    const auto deadline = GetTickCount64() + wait_ms;
    do {
        // Reseed every retry from the untouched input, never from partial output.
        if (!CopyTextureCommon(color, output) || !BeginCommands()) return false;
        PhoenixFeederFrameV1 frame;
        frame.command_list = NativeHostList(h.list);
        frame.queue = NativeHostQueue(h.queue);
        frame.output = output;
        frame.motion = mv;
        frame.depth = depth;
        frame.reset = reset_pending ? 1u : 0u;
        frame.stream_id = stream_id;
        frame.motion_scale_x = mvsx;
        frame.motion_scale_y = mvsy;
        frame.depth_inverted = h.depth_inverted ? 1u : 0u;
        PhoenixFeederResultV1 result;
        if (!PhoenixFeederContractV1(frame, result)) {
            AbortCommands();
            HostTerminal("Direct feeder frame contract rejected");
            return false;
        }
        // Fresh states must be observable on the exact recording epoch.
        D3D12_RESOURCE_BARRIER observed[3] = {};
        ID3D12Resource *resources[]{output, mv, depth};
        const UINT count = depth ? 3u : 2u;
        for (UINT i = 0; i < count; ++i) {
            observed[i].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
            observed[i].Transition.pResource = resources[i];
            observed[i].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
            observed[i].Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
            observed[i].Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
        }
        h.list->ResourceBarrier(count, observed);
        for (UINT i = 0; i < count; ++i)
            std::swap(observed[i].Transition.StateBefore, observed[i].Transition.StateAfter);
        h.list->ResourceBarrier(count, observed);
        // Use the ReShade wrapper so this epoch has an observed empty heap state.
        h.list->SetDescriptorHeaps(0, nullptr);
        DWORD ecode = 0;
        const HRESULT hr = SafeEvaluatePhoenix(&frame, &result, &ecode, capture_token);
        if (ecode || !ValidFeederReply(hr, frame, result)) {
            AbortCommands();
            Log("[host] direct feeder failed: HRESULT=0x%08X exception=0x%08X", hr, ecode);
            std::string reason = "Direct Phoenix DLSS5 evaluation failed";
            if (!ecode && FAILED(hr)) {
                nlohmann::json refusal;
                if (ReadPhoenixOutputSummary(refusal)) {
                    const auto status = refusal.value("direct_game", nlohmann::json::object()).value("status", std::string());
                    if (!status.empty()) {
                        Log("[host] direct feeder refusal: %s", status.c_str());
                        reason += ": " + status;
                    }
                }
            }
            LogFeederModules("evaluate failure");
            HostTerminal(reason.c_str());
            return false;
        }
        if (hr == S_FALSE && result.status == PhoenixFeederStatusV1::Deferred) {
            AbortCommands();
            nlohmann::json deferred;
            if (ReadPhoenixOutputSummary(deferred)) {
                const auto status = deferred.value("direct_game", nlohmann::json::object()).value("status", std::string());
                if (status != last_deferred_status) {
                    Log("[host] direct feeder deferred: %s", status.c_str());
                    last_deferred_status = status;
                }
                if (deferred.value("terminal", true)) {
                    HostTerminal("Phoenix DLSS5 became terminal while the feeder was deferred");
                    return false;
                }
            }
            PumpPresent(); Sleep(8);
            continue;
        }
        if (result.status == PhoenixFeederStatusV1::Recorded && !summary_before_available) {
            AbortCommands();
            HostTerminal("Recorded Phoenix DLSS5 output lacks a baseline completion summary");
            return false;
        }
        if (!WaitFenceValue(h.fence, EndCommands(), 15000)) {
            HostTerminal("Direct feeder submission did not retire");
            return false;
        }
        reset_pending = false; // A deferred/unsubmitted attempt does not consume it.
        LogFeederModules("evaluate");
        if (result.status == PhoenixFeederStatusV1::Bypass) {
            g_last_evaluate_bypass = true;
            g_last_nr_enabled[stream_id] = 0;
            return CopyTextureCommon(color, output);
        }
        if (!ReadPhoenixOutputSummary(after) || after.value("terminal", true)) {
            HostTerminal("Phoenix DLSS5 processing stopped before completion proof");
            return false;
        }
        if (PhoenixOutputAdvanced(before, after)) return true;
        PumpPresent(); Sleep(8);
    } while (GetTickCount64() < deadline);
    Log("[host] Last processing progress: %s", after.dump().c_str());
    Log("[host] Phoenix DLSS5 produced no proven processed output within %u ms", wait_ms);
    return false;
}

static bool EvaluateForStream(uint32_t stream_id, ID3D12Resource *color, ID3D12Resource *output,
                                ID3D12Resource *depth, ID3D12Resource *mv, ID3D12Resource *mask,
                                UINT input_w, UINT input_h, int reset, int nr_enabled, float mvsx, float mvsy)
{
    if (stream_id > 2 || !color || !output || !mv) return false;
    const auto input = color->GetDesc(), destination = output->GetDesc();
    const auto route = PhoenixHostColorRoute(input.Format, destination.Format);
    if (route == PhoenixColorRoute::Unsupported) {
        Log("[host] unsupported Phoenix color adapter formats %u -> %u", input.Format, destination.Format);
        return false;
    }
    const bool processing = nr_enabled && g_phoenix_enabled;
    if (input.Format == destination.Format &&
        (!processing || destination.Format == DXGI_FORMAT_R16G16B16A16_FLOAT))
        return EvaluateFloatForStream(stream_id, color, output, depth, mv, mask,
                                        input_w, input_h, reset, nr_enabled, mvsx, mvsy);
    if (input.Width != destination.Width || input.Height != destination.Height ||
        input.SampleDesc.Count != 1 || destination.SampleDesc.Count != 1 ||
        input.Dimension != D3D12_RESOURCE_DIMENSION_TEXTURE2D ||
        destination.Dimension != D3D12_RESOURCE_DIMENSION_TEXTURE2D ||
        (input.Flags & D3D12_RESOURCE_FLAG_DENY_SHADER_RESOURCE)) {
        Log("[host] byte-format adapter requires shader-readable, single-sample 1:1 textures");
        return false;
    }
    if (!WaitFenceValue(h.fence, h.fence_value, 15000)) return false;
    if (route == PhoenixColorRoute::DirectFloat && destination.Format != DXGI_FORMAT_R16G16B16A16_FLOAT) {
        Log("[host] direct feeder output requires FP16 or the RGBA8/BGRA8 adapter");
        return false;
    }
    const auto adapter_format = PhoenixByteFormat(destination.Format) ? destination.Format : DXGI_FORMAT_R8G8B8A8_UNORM;
    const auto hr = g_format_adapter.Ensure(h.dev, static_cast<UINT>(input.Width), input.Height, adapter_format);
    if (FAILED(hr)) { Log("[host] Phoenix format adapter initialization failed 0x%08X", hr); return false; }
    if (!BeginCommands()) return false;
    g_format_adapter.RecordInput(h.dev, h.list, color);
    if (!WaitFenceValue(h.fence, EndCommands(), 15000)) return false;
    if (processing) {
        if (!EvaluateFloatForStream(stream_id, g_format_adapter.Color(), g_format_adapter.Output(), depth, mv, mask,
                                     input_w, input_h, reset, nr_enabled, mvsx, mvsy)) return false;
    } else {
        g_last_nr_enabled[stream_id] = 0;
    }
    if (destination.Format == DXGI_FORMAT_R16G16B16A16_FLOAT)
        return CopyTextureCommon(processing && !g_last_evaluate_bypass ? g_format_adapter.Output() : g_format_adapter.Color(), output);
    // Do not copy anything to the game until the real Phoenix completion gate passes.
    if (!BeginCommands()) return false;
    g_format_adapter.RecordOutput(h.dev, h.list, output, processing && !g_last_evaluate_bypass ? nullptr : g_format_adapter.Color());
    const bool copied = WaitFenceValue(h.fence, EndCommands(), 15000);
    if (copied) Log("[host] byte-format writeback completed: Phoenix=%d format=%u %ux%u",
                    processing && !g_last_evaluate_bypass ? 1 : 0, destination.Format, static_cast<UINT>(destination.Width), destination.Height);
    return copied;
}

static bool Evaluate(ID3D12Resource *color, ID3D12Resource *output, ID3D12Resource *depth, ID3D12Resource *mv,
                     ID3D12Resource *mask,
                     UINT input_w, UINT input_h, int reset, int nr_enabled, float mvsx, float mvsy)
{
    return EvaluateForStream(0, color, output, depth, mv, mask, input_w, input_h, reset, nr_enabled, mvsx, mvsy);
}

static char *TrimAscii(char *s)
{
    while (*s && isspace(static_cast<unsigned char>(*s))) ++s;
    char *e = s + strlen(s);
    while (e > s && isspace(static_cast<unsigned char>(e[-1]))) *--e = '\0';
    return s;
}

static int ReadParentFeedCfgInt(const char *key, int fallback)
{
    char path[MAX_PATH] = {};
    GetModuleFileNameA(nullptr, path, MAX_PATH);
    char *slash = strrchr(path, '\\');
    if (slash == nullptr) return fallback;
    *slash = '\0';                    // host64
    slash = strrchr(path, '\\');
    if (slash == nullptr) return fallback;
    strcpy_s(slash + 1, MAX_PATH - (slash + 1 - path), "dlss5-feed.cfg");

    FILE *f = nullptr;
    if (fopen_s(&f, path, "r") != 0 || f == nullptr)
        return fallback;

    int value = fallback;
    char line[256] = {};
    while (fgets(line, sizeof(line), f))
    {
        char *eq = strchr(line, '=');
        if (eq == nullptr) continue;
        *eq = '\0';
        char *k = TrimAscii(line);
        char *v = TrimAscii(eq + 1);
        if (_stricmp(k, key) == 0)
        {
            value = atoi(v);
            break;
        }
    }
    fclose(f);
    return value;
}

static bool ShouldSplitEyes(const FeedBuild &b)
{
    const int cfg = ReadParentFeedCfgInt("vr_eye_split", -1); // -1 auto, 0 off, 1 force
    if (cfg == 0 || b.output_width < 128u || b.output_height < 64u || b.width < 128u)
        return false;
    if ((b.output_width % 2u) != 0 || (b.width % 2u) != 0)
        return false;

    const double aspect = static_cast<double>(b.output_width) / static_cast<double>(b.output_height);
    if (cfg > 0)
        return aspect >= 1.60 && aspect <= 2.60;

    // Preserve the large side-by-side split threshold. Each eye uses its own
    // feeder stream; ordinary ultrawide targets use stream zero.
    return b.output_width > 8192u && aspect >= 1.60 && aspect <= 2.60;
}

// ---------------------------------------------------------------------------
// --test: prove the whole stack with no game attached
// ---------------------------------------------------------------------------

static ID3D12Resource *MakeTex(UINT w, UINT h_, DXGI_FORMAT fmt, bool uav)
{
    D3D12_HEAP_PROPERTIES hp = {};
    hp.Type = D3D12_HEAP_TYPE_DEFAULT;
    D3D12_RESOURCE_DESC rd = {};
    rd.Dimension        = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    rd.Width            = w;
    rd.Height           = h_;
    rd.DepthOrArraySize = 1;
    rd.MipLevels        = 1;
    rd.Format           = fmt;
    rd.SampleDesc.Count = 1;
    rd.Layout           = D3D12_TEXTURE_LAYOUT_UNKNOWN;
    rd.Flags            = uav && !PhoenixByteFormat(fmt) ? D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS : D3D12_RESOURCE_FLAG_NONE;
    ID3D12Resource *t = nullptr;
    h.dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_COMMON, nullptr,
                                   __uuidof(ID3D12Resource), reinterpret_cast<void **>(&t));
    return t;
}

static bool CopyTextureCommon(ID3D12Resource *src, ID3D12Resource *dst)
{
    if (src == nullptr || dst == nullptr) return false;
    if (src->GetDesc().Format != dst->GetDesc().Format) {
        Log("[host] incompatible copy formats; conversion required");
        return false;
    }
    if (!BeginCommands()) return false;

    D3D12_RESOURCE_BARRIER b[2] = {};
    b[0].Type                   = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    b[0].Transition.pResource   = src;
    b[0].Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
    b[0].Transition.StateAfter  = D3D12_RESOURCE_STATE_COPY_SOURCE;
    b[0].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    b[1].Type                   = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    b[1].Transition.pResource   = dst;
    b[1].Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
    b[1].Transition.StateAfter  = D3D12_RESOURCE_STATE_COPY_DEST;
    b[1].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    h.list->ResourceBarrier(2, b);
    h.list->CopyResource(dst, src);
    b[0].Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_SOURCE;
    b[0].Transition.StateAfter  = D3D12_RESOURCE_STATE_COMMON;
    b[1].Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
    b[1].Transition.StateAfter  = D3D12_RESOURCE_STATE_COMMON;
    h.list->ResourceBarrier(2, b);
    const UINT64 v = EndCommands();
    return WaitFenceValue(h.fence, v, 4000);
}

static bool CopyTextureRegionCommon(ID3D12Resource *src, ID3D12Resource *dst,
                                    UINT src_x, UINT src_y, UINT dst_x, UINT dst_y,
                                    UINT width, UINT height)
{
    if (src == nullptr || dst == nullptr) return false;
    if (!BeginCommands()) return false;

    D3D12_RESOURCE_BARRIER b[2] = {};
    b[0].Type                   = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    b[0].Transition.pResource   = src;
    b[0].Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
    b[0].Transition.StateAfter  = D3D12_RESOURCE_STATE_COPY_SOURCE;
    b[0].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    b[1].Type                   = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    b[1].Transition.pResource   = dst;
    b[1].Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
    b[1].Transition.StateAfter  = D3D12_RESOURCE_STATE_COPY_DEST;
    b[1].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    h.list->ResourceBarrier(2, b);

    D3D12_TEXTURE_COPY_LOCATION src_loc = {}, dst_loc = {};
    src_loc.pResource = src;
    src_loc.Type      = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
    dst_loc.pResource = dst;
    dst_loc.Type      = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
    D3D12_BOX box = { src_x, src_y, 0, src_x + width, src_y + height, 1 };
    h.list->CopyTextureRegion(&dst_loc, dst_x, dst_y, 0, &src_loc, &box);

    b[0].Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_SOURCE;
    b[0].Transition.StateAfter  = D3D12_RESOURCE_STATE_COMMON;
    b[1].Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
    b[1].Transition.StateAfter  = D3D12_RESOURCE_STATE_COMMON;
    h.list->ResourceBarrier(2, b);
    const UINT64 v = EndCommands();
    return WaitFenceValue(h.fence, v, 4000);
}

static bool CopyGameInputsToLocal()
{
    if (!CopyTextureCommon(h.tex[FEED_COLOR], h.local_tex[FEED_COLOR])) return false;
    if (!CopyTextureCommon(h.tex[FEED_DEPTH], h.local_tex[FEED_DEPTH])) return false;
    if (!CopyTextureCommon(h.tex[FEED_MV], h.local_tex[FEED_MV])) return false;
    if (h.has_mask && !CopyTextureCommon(h.tex[FEED_MASK], h.local_tex[FEED_MASK])) return false;
    return true;
}

static bool CopyGameInputsToEyeLocal()
{
    for (UINT eye = 0; eye < 2; ++eye)
    {
        const UINT in_x = eye * h.eye_input_width;
        ID3D12Resource *color_src = h.cpu_bridge ? h.local_tex[FEED_COLOR] : h.tex[FEED_COLOR];
        if (!CopyTextureRegionCommon(color_src, h.eye_tex[eye][FEED_COLOR],
                                     in_x, 0, 0, 0, h.eye_input_width, h.eye_input_height)) return false;
        if (h.cpu_bridge)
            continue;
        if (!CopyTextureRegionCommon(h.tex[FEED_DEPTH], h.eye_tex[eye][FEED_DEPTH],
                                     in_x, 0, 0, 0, h.eye_input_width, h.eye_input_height)) return false;
        if (!CopyTextureRegionCommon(h.tex[FEED_MV], h.eye_tex[eye][FEED_MV],
                                     in_x, 0, 0, 0, h.eye_input_width, h.eye_input_height)) return false;
        if (h.has_mask && !CopyTextureRegionCommon(h.tex[FEED_MASK], h.eye_tex[eye][FEED_MASK],
                                                   in_x, 0, 0, 0, h.eye_input_width, h.eye_input_height)) return false;
    }
    return true;
}

static bool CopyEyeOutputsToGame()
{
    for (UINT eye = 0; eye < 2; ++eye)
    {
        const UINT out_x = eye * h.eye_width;
        ID3D12Resource *output_dst = h.cpu_bridge ? h.local_tex[FEED_OUTPUT] : h.tex[FEED_OUTPUT];
        if (!CopyTextureRegionCommon(h.eye_tex[eye][FEED_OUTPUT], output_dst,
                                     0, 0, out_x, 0, h.eye_width, h.eye_height)) return false;
    }
    return true;
}

static bool EvaluateSplitEyes(uint32_t iterations, int reset, int nr_enabled, float mvsx, float mvsy)
{
    static int diag_left = 4;
    const bool diag = diag_left-- > 0;
    if (diag) Log("[host] VR split frame: copy eye inputs begin");
    if (!CopyGameInputsToEyeLocal()) return false;
    if (diag) Log("[host] VR split frame: copy eye inputs done");

    bool ok = true;
    for (UINT eye = 0; eye < 2 && ok; ++eye)
    {
        if (diag) Log("[host] VR split frame: evaluate eye %u begin", eye);
        for (uint32_t i = 0; i < iterations && ok; ++i) {
            auto *input = i == 0 ? h.eye_tex[eye][FEED_COLOR] : h.eye_scratch[eye][(i - 1) & 1u];
            auto *output = i + 1 == iterations ? h.eye_tex[eye][FEED_OUTPUT] : h.eye_scratch[eye][i & 1u];
            ok = EvaluateForStream(eye + 1, input, output,
                                   h.eye_tex[eye][FEED_DEPTH], h.eye_tex[eye][FEED_MV],
                                   h.has_mask ? h.eye_tex[eye][FEED_MASK] : nullptr,
                                   h.eye_input_width, h.eye_input_height,
                                   i == 0 ? reset : 0, nr_enabled, mvsx, mvsy);
        }
        if (diag) Log("[host] VR split frame: evaluate eye %u %s", eye, ok ? "done" : "failed");
    }

    if (!ok) return false;
    if (diag) Log("[host] VR split frame: stitch outputs begin");
    ok = CopyEyeOutputsToGame();
    if (diag) Log("[host] VR split frame: stitch outputs %s", ok ? "done" : "failed");
    return ok;
}

struct RgbaImage
{
    UINT width = 0, height = 0;
    std::vector<BYTE> pixels;
};

static void ResizeRgbaNearest(const RgbaImage &src, UINT dst_w, UINT dst_h, std::vector<BYTE> &dst)
{
    dst.assign(static_cast<size_t>(dst_w) * dst_h * 4u, 0);
    if (src.width == 0 || src.height == 0 || src.pixels.size() < static_cast<size_t>(src.width) * src.height * 4u ||
        dst_w == 0 || dst_h == 0)
        return;

    for (UINT y = 0; y < dst_h; ++y)
    {
        const UINT sy = static_cast<UINT>((static_cast<uint64_t>(y) * src.height) / dst_h);
        const BYTE *src_row = src.pixels.data() + static_cast<size_t>(sy) * src.width * 4u;
        BYTE *dst_row = dst.data() + static_cast<size_t>(y) * dst_w * 4u;
        for (UINT x = 0; x < dst_w; ++x)
        {
            const UINT sx = static_cast<UINT>((static_cast<uint64_t>(x) * src.width) / dst_w);
            memcpy(dst_row + static_cast<size_t>(x) * 4u, src_row + static_cast<size_t>(sx) * 4u, 4u);
        }
    }
}

static std::wstring WideArg(const char *s)
{
    if (s == nullptr) return L"";
    int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, nullptr, 0);
    if (n <= 1) n = MultiByteToWideChar(CP_ACP, 0, s, -1, nullptr, 0);
    std::wstring out(static_cast<size_t>(n > 0 ? n - 1 : 0), L'\0');
    if (n > 1)
    {
        if (MultiByteToWideChar(CP_UTF8, 0, s, -1, &out[0], n) == 0)
            MultiByteToWideChar(CP_ACP, 0, s, -1, &out[0], n);
    }
    return out;
}

static bool LoadImageRgba8(const wchar_t *path, RgbaImage &img)
{
    IWICImagingFactory *factory = nullptr;
    IWICBitmapDecoder *decoder = nullptr;
    IWICBitmapFrameDecode *frame = nullptr;
    IWICFormatConverter *conv = nullptr;
    HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                                  IID_PPV_ARGS(&factory));
    if (SUCCEEDED(hr)) hr = factory->CreateDecoderFromFilename(path, nullptr, GENERIC_READ,
                                                               WICDecodeMetadataCacheOnLoad, &decoder);
    if (SUCCEEDED(hr)) hr = decoder->GetFrame(0, &frame);
    if (SUCCEEDED(hr)) hr = factory->CreateFormatConverter(&conv);
    if (SUCCEEDED(hr)) hr = conv->Initialize(frame, GUID_WICPixelFormat32bppRGBA,
                                             WICBitmapDitherTypeNone, nullptr, 0.0f,
                                             WICBitmapPaletteTypeCustom);
    if (SUCCEEDED(hr)) hr = conv->GetSize(&img.width, &img.height);
    if (SUCCEEDED(hr) && (img.width == 0 || img.height == 0 || img.width > 8192 || img.height > 8192))
        hr = E_INVALIDARG;
    if (SUCCEEDED(hr))
    {
        img.pixels.resize(static_cast<size_t>(img.width) * img.height * 4);
        hr = conv->CopyPixels(nullptr, img.width * 4, static_cast<UINT>(img.pixels.size()), img.pixels.data());
    }
    ReleasePtr(conv);
    ReleasePtr(frame);
    ReleasePtr(decoder);
    ReleasePtr(factory);
    if (FAILED(hr)) Log("[host] image load failed 0x%08X", hr);
    return SUCCEEDED(hr);
}

static bool SaveImageRgba8(const wchar_t *path, const RgbaImage &img)
{
    std::vector<BYTE> bgra(img.pixels.size());
    for (size_t i = 0; i + 3 < img.pixels.size(); i += 4)
    {
        bgra[i + 0] = img.pixels[i + 2];
        bgra[i + 1] = img.pixels[i + 1];
        bgra[i + 2] = img.pixels[i + 0];
        bgra[i + 3] = img.pixels[i + 3];
    }
    IWICImagingFactory *factory = nullptr;
    IWICStream *stream = nullptr;
    IWICBitmapEncoder *encoder = nullptr;
    IWICBitmapFrameEncode *frame = nullptr;
    IPropertyBag2 *bag = nullptr;
    HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                                  IID_PPV_ARGS(&factory));
    if (SUCCEEDED(hr)) hr = factory->CreateStream(&stream);
    if (SUCCEEDED(hr)) hr = stream->InitializeFromFilename(path, GENERIC_WRITE);
    if (SUCCEEDED(hr)) hr = factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder);
    if (SUCCEEDED(hr)) hr = encoder->Initialize(stream, WICBitmapEncoderNoCache);
    if (SUCCEEDED(hr)) hr = encoder->CreateNewFrame(&frame, &bag);
    if (SUCCEEDED(hr)) hr = frame->Initialize(bag);
    if (SUCCEEDED(hr)) hr = frame->SetSize(img.width, img.height);
    WICPixelFormatGUID fmt = GUID_WICPixelFormat32bppBGRA;
    if (SUCCEEDED(hr)) hr = frame->SetPixelFormat(&fmt);
    if (SUCCEEDED(hr) && memcmp(&fmt, &GUID_WICPixelFormat32bppBGRA, sizeof(fmt)) != 0) hr = E_FAIL;
    if (SUCCEEDED(hr)) hr = frame->WritePixels(img.height, img.width * 4,
                                               static_cast<UINT>(bgra.size()),
                                               bgra.data());
    if (SUCCEEDED(hr)) hr = frame->Commit();
    if (SUCCEEDED(hr)) hr = encoder->Commit();
    ReleasePtr(bag);
    ReleasePtr(frame);
    ReleasePtr(encoder);
    ReleasePtr(stream);
    ReleasePtr(factory);
    if (FAILED(hr)) Log("[host] image save failed 0x%08X", hr);
    return SUCCEEDED(hr);
}

static bool UploadRows(ID3D12Resource *tex, DXGI_FORMAT fmt, UINT w, UINT hgt, const void *data, UINT row_bytes)
{
    const UINT pitch = (row_bytes + 255) & ~255u;
    D3D12_HEAP_PROPERTIES hp = {};
    hp.Type = D3D12_HEAP_TYPE_UPLOAD;
    D3D12_RESOURCE_DESC bd = {};
    bd.Dimension        = D3D12_RESOURCE_DIMENSION_BUFFER;
    bd.Width            = static_cast<UINT64>(pitch) * hgt;
    bd.Height           = 1;
    bd.DepthOrArraySize = 1;
    bd.MipLevels        = 1;
    bd.SampleDesc.Count = 1;
    bd.Layout           = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    ID3D12Resource *upload = nullptr;
    if (FAILED(h.dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &bd,
                                              D3D12_RESOURCE_STATE_GENERIC_READ, nullptr,
                                              __uuidof(ID3D12Resource),
                                              reinterpret_cast<void **>(&upload))))
        return false;

    BYTE *dst = nullptr;
    if (FAILED(upload->Map(0, nullptr, reinterpret_cast<void **>(&dst)))) { upload->Release(); return false; }
    const BYTE *src = static_cast<const BYTE *>(data);
    for (UINT y = 0; y < hgt; ++y)
        memcpy(dst + static_cast<size_t>(y) * pitch, src + static_cast<size_t>(y) * row_bytes, row_bytes);
    upload->Unmap(0, nullptr);

    bool ok = false;
    if (BeginCommands())
    {
        D3D12_RESOURCE_BARRIER b = {};
        b.Type                   = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Transition.pResource   = tex;
        b.Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
        b.Transition.StateAfter  = D3D12_RESOURCE_STATE_COPY_DEST;
        b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        h.list->ResourceBarrier(1, &b);

        D3D12_TEXTURE_COPY_LOCATION src_loc = {}, dst_loc = {};
        src_loc.pResource = upload;
        src_loc.Type      = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
        src_loc.PlacedFootprint.Footprint.Format   = fmt;
        src_loc.PlacedFootprint.Footprint.Width    = w;
        src_loc.PlacedFootprint.Footprint.Height   = hgt;
        src_loc.PlacedFootprint.Footprint.Depth    = 1;
        src_loc.PlacedFootprint.Footprint.RowPitch = pitch;
        dst_loc.pResource = tex;
        dst_loc.Type      = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        h.list->CopyTextureRegion(&dst_loc, 0, 0, 0, &src_loc, nullptr);

        b.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
        b.Transition.StateAfter  = D3D12_RESOURCE_STATE_COMMON;
        h.list->ResourceBarrier(1, &b);
        const UINT64 v = EndCommands();
        ok = WaitFenceValue(h.fence, v, 4000);
    }
    upload->Release();
    return ok;
}

static bool DownloadRgba8(ID3D12Resource *tex, UINT w, UINT hgt, RgbaImage &img)
{
    const auto format = tex->GetDesc().Format;
    const bool half = format == DXGI_FORMAT_R16G16B16A16_FLOAT;
    if (!half && !PhoenixByteFormat(format)) return false;
    img.width = w;
    img.height = hgt;
    img.pixels.assign(static_cast<size_t>(w) * hgt * 4, 0);
    const UINT row_bytes = w * (half ? 8 : 4);
    const UINT pitch = (row_bytes + 255) & ~255u;
    D3D12_HEAP_PROPERTIES hp = {};
    hp.Type = D3D12_HEAP_TYPE_READBACK;
    D3D12_RESOURCE_DESC bd = {};
    bd.Dimension        = D3D12_RESOURCE_DIMENSION_BUFFER;
    bd.Width            = static_cast<UINT64>(pitch) * hgt;
    bd.Height           = 1;
    bd.DepthOrArraySize = 1;
    bd.MipLevels        = 1;
    bd.SampleDesc.Count = 1;
    bd.Layout           = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    ID3D12Resource *readback = nullptr;
    if (FAILED(h.dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &bd,
                                              D3D12_RESOURCE_STATE_COPY_DEST, nullptr,
                                              __uuidof(ID3D12Resource),
                                              reinterpret_cast<void **>(&readback))))
        return false;

    bool ok = false;
    if (BeginCommands())
    {
        D3D12_RESOURCE_BARRIER b = {};
        b.Type                   = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Transition.pResource   = tex;
        b.Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
        b.Transition.StateAfter  = D3D12_RESOURCE_STATE_COPY_SOURCE;
        b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        h.list->ResourceBarrier(1, &b);

        D3D12_TEXTURE_COPY_LOCATION src_loc = {}, dst_loc = {};
        src_loc.pResource = tex;
        src_loc.Type      = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        dst_loc.pResource = readback;
        dst_loc.Type      = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
        dst_loc.PlacedFootprint.Footprint.Format   = format;
        dst_loc.PlacedFootprint.Footprint.Width    = w;
        dst_loc.PlacedFootprint.Footprint.Height   = hgt;
        dst_loc.PlacedFootprint.Footprint.Depth    = 1;
        dst_loc.PlacedFootprint.Footprint.RowPitch = pitch;
        h.list->CopyTextureRegion(&dst_loc, 0, 0, 0, &src_loc, nullptr);

        b.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_SOURCE;
        b.Transition.StateAfter  = D3D12_RESOURCE_STATE_COMMON;
        h.list->ResourceBarrier(1, &b);
        const UINT64 v = EndCommands();
        ok = WaitFenceValue(h.fence, v, 4000);
    }

    if (ok)
    {
        BYTE *src = nullptr;
        if (SUCCEEDED(readback->Map(0, nullptr, reinterpret_cast<void **>(&src))))
        {
            for (UINT y = 0; y < hgt; ++y) {
                auto* destination = img.pixels.data() + static_cast<size_t>(y) * w * 4;
                const auto* row = src + static_cast<size_t>(y) * pitch;
                if (half) {
                    const auto* values = reinterpret_cast<const uint16_t*>(row);
                    for (UINT x = 0; x < w * 4; ++x) destination[x] = PhoenixProbeByte(values[x]);
                } else {
                    memcpy(destination, row, row_bytes);
                    if (format == DXGI_FORMAT_B8G8R8A8_UNORM)
                        for (UINT x = 0; x < w; ++x) std::swap(destination[x * 4], destination[x * 4 + 2]);
                }
            }
            readback->Unmap(0, nullptr);
        }
        else ok = false;
    }
    readback->Release();
    return ok;
}

static int RunImage(const wchar_t *input_path, const wchar_t *output_path, int frames, DXGI_FORMAT format)
{
    RgbaImage input = {};
    if (!LoadImageRgba8(input_path, input)) return 1;
    const UINT W = input.width, H = input.height;
    Log("[host] --image: %ux%u static direct Phoenix probe, frames=%d format=%u", W, H, frames, format);

    ID3D12Resource *color  = MakeTex(W, H, format, false);
    ID3D12Resource *output = MakeTex(W, H, format, true);
    ID3D12Resource *depth  = MakeTex(W, H, DXGI_FORMAT_R32_FLOAT, false);
    ID3D12Resource *mv     = MakeTex(W, H, DXGI_FORMAT_R16G16_FLOAT, false);
    if (!color || !output || !depth || !mv) { Log("[host] image texture creation failed"); return 1; }

    std::vector<float> depth_px(static_cast<size_t>(W) * H, 1.0f);
    std::vector<uint16_t> mv_px(static_cast<size_t>(W) * H * 2, 0);
    const auto color_px = PhoenixProbeHalfPixels(input.pixels);
    auto byte_px = input.pixels;
    if (format == DXGI_FORMAT_B8G8R8A8_UNORM)
        for (size_t i = 0; i < byte_px.size(); i += 4) std::swap(byte_px[i], byte_px[i + 2]);
    const bool half = format == DXGI_FORMAT_R16G16B16A16_FLOAT;
    bool ok = UploadRows(color, format, W, H, half ? static_cast<const void*>(color_px.data()) : byte_px.data(), W * (half ? 8 : 4)) &&
              UploadRows(depth, DXGI_FORMAT_R32_FLOAT, W, H, depth_px.data(), W * sizeof(float)) &&
              UploadRows(mv, DXGI_FORMAT_R16G16_FLOAT, W, H, mv_px.data(), W * 2 * sizeof(uint16_t));
    if (!ok) { Log("[host] image upload failed"); return 1; }

    for (int i = 0; i < 120; ++i) { PumpPresent(); Sleep(8); }
    int good = 0;
    const int total = frames > 0 ? frames : 12;
    for (int i = 0; i < total; ++i)
    {
        PumpPresent();
        if (!Evaluate(color, output, depth, mv, nullptr, W, H, i == 0 ? 1 : 0, 1, 1.0f, 1.0f)) break;
        ++good;
    }
    WaitFenceValue(h.fence, h.fence_value, 4000);
    Log("[host] --image evaluates: %d/%d", good, total);
    if (good != total) return 1;

    RgbaImage out = {};
    if (!DownloadRgba8(output, W, H, out)) { Log("[host] image readback failed"); return 1; }
    size_t changed = 0;
    uint64_t abs_sum = 0;
    for (size_t i = 0; i < out.pixels.size() && i < input.pixels.size(); ++i)
    {
        int d = static_cast<int>(out.pixels[i]) - static_cast<int>(input.pixels[i]);
        if (d != 0) ++changed;
        abs_sum += static_cast<unsigned>(d < 0 ? -d : d);
    }
    Log("[host] image diff: changed_bytes=%zu abs_sum=%llu", changed, static_cast<unsigned long long>(abs_sum));
    return SaveImageRgba8(output_path, out) ? 0 : 1;
}

static int RunTest()
{
    const UINT W = 640, H = 360;
    Log("[host] --test: %ux%u synthetic direct Phoenix", W, H);

    ID3D12Resource *color  = MakeTex(W, H, DXGI_FORMAT_R8G8B8A8_UNORM, false);
    ID3D12Resource *output = MakeTex(W, H, DXGI_FORMAT_R8G8B8A8_UNORM, true);
    ID3D12Resource *depth  = MakeTex(W, H, DXGI_FORMAT_R32_FLOAT, false);
    ID3D12Resource *mv     = MakeTex(W, H, DXGI_FORMAT_R16G16_FLOAT, false);
    if (!color || !output || !depth || !mv) { Log("[host] test texture creation failed"); return 1; }

    // Give the DLSS 5 add-on its hook-arming time, with the swapchain pumping.
    for (int i = 0; i < 120; ++i) { PumpPresent(); Sleep(8); }

    int good = 0;
    for (int i = 0; i < 300; ++i)
    {
        PumpPresent();
        if (Evaluate(color, output, depth, mv, nullptr, W, H, i == 0 ? 1 : 0, 1, 1.0f, 1.0f)) ++good;
        else break;
    }
    Log("[host] --test finished: %d/300 evaluates succeeded", good);
    Log("[host] inspect Phoenix summary for completed direct feeder output");
    return good >= 250 ? 0 : 1;
}

static int RunVrProbe(UINT, UINT, float)
{
    Log("[host] --probe-vr SR feature sizing is unavailable: direct Phoenix accepts SDR 1:1 frames only");
    return 1;
}

// ---------------------------------------------------------------------------
// Serve mode: the real pipe server for a 32-bit game
// ---------------------------------------------------------------------------

static bool ReadFull(HANDLE pipe, void *buf, DWORD len)
{
    BYTE *dst = static_cast<BYTE *>(buf);
    DWORD total = 0;
    while (total < len)
    {
        DWORD got = 0;
        if (!ReadFile(pipe, dst + total, len - total, &got, nullptr) || got == 0)
            return false;
        total += got;
    }
    return true;
}

static bool WriteFull(HANDLE pipe, const void *buf, DWORD len)
{
    const BYTE *src = static_cast<const BYTE *>(buf);
    DWORD total = 0;
    while (total < len)
    {
        DWORD put = 0;
        if (!WriteFile(pipe, src + total, len - total, &put, nullptr) || put == 0)
            return false;
        total += put;
    }
    return true;
}

static int Serve(DWORD game_pid)
{
    char name[128];
    sprintf_s(name, FEED_PIPE_FMT, static_cast<unsigned long>(game_pid));
    HANDLE pipe = CreateNamedPipeA(name, PIPE_ACCESS_DUPLEX, PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                                   1, 1024, 1024, 0, nullptr);
    if (pipe == INVALID_HANDLE_VALUE) { Log("[host] CreateNamedPipe failed %lu", GetLastError()); return 1; }
    Log("[host] serving on %s", name);
    if (!ConnectNamedPipe(pipe, nullptr) && GetLastError() != ERROR_PIPE_CONNECTED)
    { Log("[host] ConnectNamedPipe failed %lu", GetLastError()); return 1; }

    FeedHello hello = {};
    if (!ReadFull(pipe, &hello, sizeof(hello)) || hello.magic != FEED_IPC_MAGIC)
    { Log("[host] bad hello"); return 1; }
    if (hello.version != FEED_IPC_VERSION)
    {
        Log("[host] protocol mismatch: game v%u, host v%u", hello.version, FEED_IPC_VERSION);
        return 1;
    }
    FeedHelloAck ack = { FEED_IPC_MAGIC, FEED_IPC_VERSION };
    WriteFull(pipe, &ack, sizeof(ack));
    Log("[host] game pid %u connected (protocol v%u)", hello.pid, hello.version);

    HANDLE hgame = OpenProcess(PROCESS_DUP_HANDLE, FALSE, hello.pid);
    if (hgame == nullptr)
        Log("[host] OpenProcess failed %lu; shared-handle routes disabled, CPU bridge routes may still work", GetLastError());

    // Shared fences live for the whole session when the game can exchange GPU handles.
    HANDLE hin = nullptr, hout = nullptr;
    HANDLE game_in = nullptr, game_out = nullptr;
    bool shared_fences_ready = false;
    if (hgame != nullptr)
    {
        h.dev->CreateFence(0, D3D12_FENCE_FLAG_SHARED, __uuidof(ID3D12Fence), reinterpret_cast<void **>(&h.fence_in));
        h.dev->CreateFence(0, D3D12_FENCE_FLAG_SHARED, __uuidof(ID3D12Fence), reinterpret_cast<void **>(&h.fence_out));
        if (h.fence_in == nullptr || h.fence_out == nullptr ||
            FAILED(h.dev->CreateSharedHandle(h.fence_in, nullptr, GENERIC_ALL, nullptr, &hin)) ||
            FAILED(h.dev->CreateSharedHandle(h.fence_out, nullptr, GENERIC_ALL, nullptr, &hout)))
        {
            Log("[host] shared fence creation failed; shared-handle routes disabled");
        }
        else if (!DuplicateHandle(GetCurrentProcess(), hin, hgame, &game_in, 0, FALSE, DUPLICATE_SAME_ACCESS) ||
                 !DuplicateHandle(GetCurrentProcess(), hout, hgame, &game_out, 0, FALSE, DUPLICATE_SAME_ACCESS))
        {
            Log("[host] shared fence duplication failed %lu; shared-handle routes disabled", GetLastError());
        }
        else
        {
            shared_fences_ready = true;
        }
    }

    bool transport_only = false;
    float mvsx = 1.0f, mvsy = 1.0f;


    for (;;)
    {
        // Peek the next message type by size: Build (big) vs FrameMsg (small).
        // The pipe is byte-mode from a single writer, so read the smaller header
        // first and decide -- FeedFrameMsg and FeedBuild share no prefix, so the
        // client precedes every message with a 1-byte tag instead.
        //
        // A plain blocking ReadFile here starves the message pump (and Present)
        // whenever the game stops feeding frames -- paused, loading, a menu -- and
        // Windows shows the host window as "Not Responding". Poll instead, so the
        // window (and its ReShade overlay) stays alive and clickable at all times.
        BYTE tag = 0;
        bool tag_read = false;
        for (;;)
        {
            DWORD avail = 0;
            if (!PeekNamedPipe(pipe, nullptr, 0, nullptr, &avail, nullptr)) break;   // pipe broken
            if (avail > 0) { tag_read = ReadFull(pipe, &tag, 1); break; }
            PumpPresent();
            Sleep(8);
        }
        if (!tag_read) { Log("[host] pipe closed by the game"); break; }

        if (tag == 'B')
        {
            FeedBuild b = {};
            if (!ReadFull(pipe, &b, sizeof(b))) break;
            Log("[host] build: input %ux%u -> output %ux%u color=%u output=%u hdr=%d inverted=%d mask=%d cpu=%d game_vr_split=%d",
                b.width, b.height, b.output_width, b.output_height,
                b.color_fmt, b.output_fmt, b.hdr, b.depth_inverted, b.has_mask ? 1 : 0, b.cpu_bridge ? 1 : 0,
                b.game_side_vr_split ? 1 : 0);

            // Tear down the old set.
            h.feeder_ready = false;
            for (int i = 0; i < FEED_SLOTS; ++i)
                if (h.tex[i] != nullptr) { h.tex[i]->Release(); h.tex[i] = nullptr; }
            for (int i = 0; i < FEED_SLOTS; ++i)
                if (h.local_tex[i] != nullptr) { h.local_tex[i]->Release(); h.local_tex[i] = nullptr; }
            for (int eye = 0; eye < 2; ++eye)
                for (int i = 0; i < FEED_SLOTS; ++i)
                    ReleasePtr(h.eye_tex[eye][i]);
            ReleasePtr(h.iter_scratch[0]);
            ReleasePtr(h.iter_scratch[1]);
            for (int eye = 0; eye < 2; ++eye)
                for (int i = 0; i < 2; ++i)
                    ReleasePtr(h.eye_scratch[eye][i]);

            // Open the game's textures (duplicate the handles out of the game) unless this
            // is a CPU bridge, where frames arrive as pipe payloads.
            bool ok = !b.hdr && b.width == b.output_width && b.height == b.output_height;
            if (!ok) Log("[host] Phoenix feeder currently requires SDR and 1:1 input/output; build rejected");
            if (!b.cpu_bridge && (hgame == nullptr || !shared_fences_ready))
            {
                Log("[host] shared-handle build requested, but game process handles/fences are unavailable");
                ok = false;
            }
            for (int i = 0; i < FEED_SLOTS && ok && !b.cpu_bridge; ++i)
            {
                HANDLE local = nullptr;
                if (!DuplicateHandle(hgame, reinterpret_cast<HANDLE>(static_cast<uintptr_t>(b.tex[i])),
                                     GetCurrentProcess(), &local, 0, FALSE, DUPLICATE_SAME_ACCESS))
                { Log("[host] DuplicateHandle(tex %d) failed %lu", i, GetLastError()); ok = false; break; }
                HRESULT hr = h.dev->OpenSharedHandle(local, __uuidof(ID3D12Resource),
                                                     reinterpret_cast<void **>(&h.tex[i]));
                CloseHandle(local);
                if (FAILED(hr)) { Log("[host] OpenSharedHandle(tex %d) failed 0x%08X", i, hr); ok = false; }
            }


            if (ok)
            {
                h.input_width = b.width; h.input_height = b.height;
                h.width = b.output_width; h.height = b.output_height;
                h.split_eyes = false;
                h.game_side_vr_split = b.game_side_vr_split != 0;
                h.eye_width = h.eye_height = h.eye_input_width = h.eye_input_height = 0;
                h.color_fmt  = static_cast<DXGI_FORMAT>(b.color_fmt);
                h.output_fmt = static_cast<DXGI_FORMAT>(b.output_fmt);
                h.has_mask = b.has_mask != 0;
                h.cpu_bridge = b.cpu_bridge != 0;
                mvsx = b.mv_scale_x; mvsy = b.mv_scale_y;
                transport_only = b.transport != 0;
                int depth_inverted = b.depth_inverted;
                h.depth_inverted = depth_inverted != 0;
                if (transport_only)
                {
                    Log("[host] transport-only mode: Color will be copied to Output, no evaluate");
                }
                else
                {
                    h.split_eyes = !h.game_side_vr_split && ShouldSplitEyes(b);
                    if (h.game_side_vr_split)
                    {
                        Log("[host] game-side VR split enabled: per-eye input %ux%u -> output %ux%u, present %ux%u",
                            b.width, b.height, b.output_width, b.output_height, b.present_width, b.present_height);
                        h.local_tex[FEED_COLOR]  = MakeTex(b.width, b.height, static_cast<DXGI_FORMAT>(b.color_fmt), false);
                        h.local_tex[FEED_OUTPUT] = MakeTex(b.output_width, b.output_height, static_cast<DXGI_FORMAT>(b.output_fmt), true);
                        h.local_tex[FEED_DEPTH]  = MakeTex(b.width, b.height, DXGI_FORMAT_R32_FLOAT, false);
                        h.local_tex[FEED_MV]     = MakeTex(b.width, b.height, DXGI_FORMAT_R16G16_FLOAT, false);
                        if (h.has_mask)
                            h.local_tex[FEED_MASK] = MakeTex(b.width, b.height, DXGI_FORMAT_R8_UNORM, false);
                        h.iter_scratch[0] = MakeTex(b.output_width, b.output_height, static_cast<DXGI_FORMAT>(b.output_fmt), true);
                        h.iter_scratch[1] = MakeTex(b.output_width, b.output_height, static_cast<DXGI_FORMAT>(b.output_fmt), true);
                        if (h.local_tex[FEED_COLOR] == nullptr || h.local_tex[FEED_OUTPUT] == nullptr ||
                            h.local_tex[FEED_DEPTH] == nullptr || h.local_tex[FEED_MV] == nullptr ||
                            (h.has_mask && h.local_tex[FEED_MASK] == nullptr) ||
                            h.iter_scratch[0] == nullptr || h.iter_scratch[1] == nullptr)
                        {
                            Log("[host] game-side VR split local texture creation failed");
                            ok = false;
                        }
                    }
                    else if (h.split_eyes)
                    {
                        h.eye_width = b.output_width / 2u;
                        h.eye_height = b.output_height;
                        h.eye_input_width = b.width / 2u;
                        h.eye_input_height = b.height;
                        Log("[host] VR split enabled: combined input %ux%u -> output %ux%u, per-eye input %ux%u -> output %ux%u",
                            b.width, b.height, b.output_width, b.output_height,
                            h.eye_input_width, h.eye_input_height, h.eye_width, h.eye_height);
                        for (int eye = 0; eye < 2; ++eye)
                        {
                            h.eye_tex[eye][FEED_COLOR]  = MakeTex(h.eye_input_width, h.eye_input_height, static_cast<DXGI_FORMAT>(b.color_fmt), false);
                            h.eye_tex[eye][FEED_OUTPUT] = MakeTex(h.eye_width, h.eye_height, static_cast<DXGI_FORMAT>(b.output_fmt), true);
                            h.eye_tex[eye][FEED_DEPTH]  = MakeTex(h.eye_input_width, h.eye_input_height, DXGI_FORMAT_R32_FLOAT, false);
                            h.eye_tex[eye][FEED_MV]     = MakeTex(h.eye_input_width, h.eye_input_height, DXGI_FORMAT_R16G16_FLOAT, false);
                            if (h.has_mask)
                                h.eye_tex[eye][FEED_MASK] = MakeTex(h.eye_input_width, h.eye_input_height, DXGI_FORMAT_R8_UNORM, false);
                            h.eye_scratch[eye][0] = MakeTex(h.eye_width, h.eye_height, static_cast<DXGI_FORMAT>(b.output_fmt), true);
                            h.eye_scratch[eye][1] = MakeTex(h.eye_width, h.eye_height, static_cast<DXGI_FORMAT>(b.output_fmt), true);
                            if (h.eye_tex[eye][FEED_COLOR] == nullptr || h.eye_tex[eye][FEED_OUTPUT] == nullptr ||
                                h.eye_tex[eye][FEED_DEPTH] == nullptr || h.eye_tex[eye][FEED_MV] == nullptr ||
                                (h.has_mask && h.eye_tex[eye][FEED_MASK] == nullptr) ||
                                h.eye_scratch[eye][0] == nullptr || h.eye_scratch[eye][1] == nullptr)
                            {
                                Log("[host] VR split texture creation failed for eye %d", eye);
                                ok = false;
                                break;
                            }
                        }
                        if (ok && h.cpu_bridge)
                        {
                            h.local_tex[FEED_COLOR] = MakeTex(b.width, b.height, static_cast<DXGI_FORMAT>(b.color_fmt), false);
                            h.local_tex[FEED_OUTPUT] = MakeTex(b.output_width, b.output_height, static_cast<DXGI_FORMAT>(b.output_fmt), true);
                            if (h.local_tex[FEED_COLOR] == nullptr || h.local_tex[FEED_OUTPUT] == nullptr)
                            {
                                Log("[host] CPU VR split full-frame texture creation failed");
                                ok = false;
                            }
                        }
                        if (ok && h.cpu_bridge)
                        {
                            std::vector<float> depth_px(static_cast<size_t>(h.eye_input_width) * h.eye_input_height, 1.0f);
                            std::vector<uint16_t> mv_px(static_cast<size_t>(h.eye_input_width) * h.eye_input_height * 2, 0);
                            for (int eye = 0; eye < 2 && ok; ++eye)
                            {
                                ok = UploadRows(h.eye_tex[eye][FEED_DEPTH], DXGI_FORMAT_R32_FLOAT,
                                                h.eye_input_width, h.eye_input_height, depth_px.data(),
                                                h.eye_input_width * sizeof(float)) &&
                                     UploadRows(h.eye_tex[eye][FEED_MV], DXGI_FORMAT_R16G16_FLOAT,
                                                h.eye_input_width, h.eye_input_height, mv_px.data(),
                                                h.eye_input_width * 2 * sizeof(uint16_t));
                            }
                            if (!ok)
                                Log("[host] CPU VR split guide upload failed");
                        }
                    }
                    else
                    {
                        h.local_tex[FEED_COLOR]  = MakeTex(b.width, b.height, static_cast<DXGI_FORMAT>(b.color_fmt), false);
                        h.local_tex[FEED_OUTPUT] = MakeTex(b.output_width, b.output_height, static_cast<DXGI_FORMAT>(b.output_fmt), true);
                        h.local_tex[FEED_DEPTH]  = MakeTex(b.width, b.height, DXGI_FORMAT_R32_FLOAT, false);
                        h.local_tex[FEED_MV]     = MakeTex(b.width, b.height, DXGI_FORMAT_R16G16_FLOAT, false);
                        if (h.has_mask)
                            h.local_tex[FEED_MASK] = MakeTex(b.width, b.height, DXGI_FORMAT_R8_UNORM, false);
                        h.iter_scratch[0] = MakeTex(b.output_width, b.output_height, static_cast<DXGI_FORMAT>(b.output_fmt), true);
                        h.iter_scratch[1] = MakeTex(b.output_width, b.output_height, static_cast<DXGI_FORMAT>(b.output_fmt), true);
                        if (h.local_tex[FEED_COLOR] == nullptr || h.local_tex[FEED_OUTPUT] == nullptr ||
                            h.local_tex[FEED_DEPTH] == nullptr || h.local_tex[FEED_MV] == nullptr ||
                            (h.has_mask && h.local_tex[FEED_MASK] == nullptr) ||
                            h.iter_scratch[0] == nullptr || h.iter_scratch[1] == nullptr)
                        {
                            Log("[host] local bridge texture creation failed");
                            ok = false;
                        }
                        if (ok && h.cpu_bridge)
                        {
                            std::vector<float> depth_px(static_cast<size_t>(b.width) * b.height, 1.0f);
                            std::vector<uint16_t> mv_px(static_cast<size_t>(b.width) * b.height * 2, 0);
                            ok = UploadRows(h.local_tex[FEED_DEPTH], DXGI_FORMAT_R32_FLOAT,
                                            b.width, b.height, depth_px.data(), b.width * sizeof(float)) &&
                                 UploadRows(h.local_tex[FEED_MV], DXGI_FORMAT_R16G16_FLOAT,
                                            b.width, b.height, mv_px.data(), b.width * 2 * sizeof(uint16_t));
                            if (!ok)
                                Log("[host] CPU bridge guide upload failed");
                        }
                    }
                }

                if (ok && !transport_only)
                    ok = WaitForPhoenixFeeder(3000);
            }
            h.feeder_ready = ok;
            for (auto& state : g_last_nr_enabled) state = -1;

            FeedBuildAck back = {};
            back.ok         = ok ? 1 : 0;
            back.ngx_result = ok ? 1u : 0xBAD00005u; // Legacy IPC success/failure field; no NGX call.
            back.fence_in   = (!b.cpu_bridge && shared_fences_ready) ? reinterpret_cast<uint64_t>(game_in) : 0;
            back.fence_out  = (!b.cpu_bridge && shared_fences_ready) ? reinterpret_cast<uint64_t>(game_out) : 0;
            WriteFull(pipe, &back, sizeof(back));
            if (g_host_terminal) break;
        }
        else if (tag == 'F')
        {
            FeedFrameMsg fm = {};
            if (!ReadFull(pipe, &fm, sizeof(fm))) break;
            if (!h.feeder_ready) { HostTerminal("Frame arrived without an accepted build"); break; }
            if (h.game_side_vr_split && fm.eye_index > 1u) { HostTerminal("Invalid feeder eye index"); break; }
            if (h.cpu_bridge)
            {
                const DWORD in_bytes = h.input_width * h.input_height * 4u;
                std::vector<BYTE> cpu_in(in_bytes);
                if (!ReadFull(pipe, cpu_in.data(), in_bytes)) break;

                bool done = false;
                RgbaImage cpu_out = {};
                if (transport_only)
                {
                    cpu_out.width = h.input_width;
                    cpu_out.height = h.input_height;
                    cpu_out.pixels = cpu_in;
                    done = true;
                }
                else
                {
                    uint32_t iterations = PhoenixHostIterations(fm.iterations, g_frame_iterations);
                    if (iterations < 1) iterations = 1;
                    if (iterations > 10) iterations = 10;
                    if (h.split_eyes)
                    {
                        done = UploadRows(h.local_tex[FEED_COLOR], DXGI_FORMAT_R8G8B8A8_UNORM,
                                          h.input_width, h.input_height, cpu_in.data(), h.input_width * 4u);
                        if (done)
                            done = EvaluateSplitEyes(iterations, fm.reset ? 1 : 0, fm.nr_enabled ? 1 : 0, mvsx, mvsy);
                    }
                    else
                    {
                        std::vector<BYTE> next_input;
                        const BYTE *input_bytes = cpu_in.data();
                        for (uint32_t i = 0; i < iterations; ++i)
                        {
                            done = UploadRows(h.local_tex[FEED_COLOR], DXGI_FORMAT_R8G8B8A8_UNORM,
                                              h.input_width, h.input_height, input_bytes, h.input_width * 4u);
                            if (!done) break;

                            done = EvaluateForStream(h.game_side_vr_split ? 1u + (fm.eye_index & 1u) : 0u, h.local_tex[FEED_COLOR], h.local_tex[FEED_OUTPUT], h.local_tex[FEED_DEPTH], h.local_tex[FEED_MV],
                                            nullptr, h.input_width, h.input_height,
                                            (i == 0 && fm.reset) ? 1 : 0, fm.nr_enabled ? 1 : 0, mvsx, mvsy);
                            if (!done) break;

                            done = DownloadRgba8(h.local_tex[FEED_OUTPUT], h.width, h.height, cpu_out);
                            if (!done) break;

                            if (i + 1 < iterations)
                            {
                                ResizeRgbaNearest(cpu_out, h.input_width, h.input_height, next_input);
                                input_bytes = next_input.data();
                            }
                        }
                    }
                    if (done && cpu_out.pixels.empty())
                        done = DownloadRgba8(h.local_tex[FEED_OUTPUT], h.width, h.height, cpu_out);
                }

                FeedCpuFrameAck ca = {};
                ca.ok = done ? 1 : 0;
                ca.row_bytes = h.width * 4u;
                WriteFull(pipe, &ca, sizeof(ca));
                if (done)
                    WriteFull(pipe, cpu_out.pixels.data(), static_cast<DWORD>(cpu_out.pixels.size()));
                if (!done) { HostTerminal("CPU frame processing or retirement failed"); break; }

                if (fm.n <= 3 || (fm.n % 1800) == 0)
                    Log("[host] CPU frame %llu evaluated (ok=%d nr=%s, iterations=%u)",
                        (unsigned long long)fm.n, done ? 1 : 0, fm.nr_enabled ? "on" : "off", fm.iterations);
                PumpPresent();
                continue;
            }
            if (!WaitFenceValue(h.fence_in, fm.n, 2000))
            { HostTerminal("Shared input fence did not retire"); break; }
            if (FAILED(h.queue->Wait(h.fence_in, fm.n)))
            { HostTerminal("Shared input queue wait failed"); break; }

            bool done = false;
            if (transport_only)
            {
                if (BeginCommands())
                {
                    // Deliberately copy only the LEFT half: a split screen in the game is
                    // unambiguous visual proof that the host's output reaches the screen.
                    D3D12_TEXTURE_COPY_LOCATION src = {}, dst = {};
                    src.pResource = h.tex[FEED_COLOR];
                    src.Type      = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
                    dst.pResource = h.tex[FEED_OUTPUT];
                    dst.Type      = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
                    D3D12_BOX box = { 0, 0, 0, h.width / 2, h.height, 1 };
                    h.list->CopyTextureRegion(&dst, 0, 0, 0, &src, &box);
                    EndCommands();
                    done = true;
                }
            }
            else
            {
                uint32_t iterations = PhoenixHostIterations(fm.iterations, g_frame_iterations);
                if (h.split_eyes)
                {
                    if (iterations < 1) iterations = 1;
                    if (iterations > 10) iterations = 10;
                    done = EvaluateSplitEyes(iterations, fm.reset ? 1 : 0, fm.nr_enabled ? 1 : 0, mvsx, mvsy);
                }
                else
                {
                    done = CopyGameInputsToLocal();
                    if (done)
                    {
                        if (iterations < 1) iterations = 1;
                        if (iterations > 10) iterations = 10;
                        for (uint32_t i = 0; i < iterations; ++i)
                        {
                            ID3D12Resource *in = (i == 0) ? h.local_tex[FEED_COLOR] : h.iter_scratch[(i - 1) & 1u];
                            ID3D12Resource *out = (i == iterations - 1) ? h.local_tex[FEED_OUTPUT] : h.iter_scratch[i & 1u];
                            const uint32_t stream_id = h.game_side_vr_split ? 1u + (fm.eye_index & 1u) : 0u;
                            done = EvaluateForStream(stream_id, in, out, h.local_tex[FEED_DEPTH], h.local_tex[FEED_MV],
                                                       h.has_mask ? h.local_tex[FEED_MASK] : nullptr,
                                                       h.input_width, h.input_height,
                                                       (i == 0 && fm.reset) ? 1 : 0, fm.nr_enabled ? 1 : 0, mvsx, mvsy);
                            if (!done) break;
                            // Evaluate already waited for completion and restored COMMON.
                        }
                    }
                    if (done)
                        done = CopyTextureCommon(h.local_tex[FEED_OUTPUT], h.tex[FEED_OUTPUT]);
                }
            }

            const bool retired = done && WaitFenceValue(h.fence, h.fence_value, 15000);
            if (PhoenixMayPublishShared(done, retired, g_host_terminal))
            {
                if (FAILED(h.queue->Signal(h.fence_out, fm.n)))
                { HostTerminal("Shared output queue signal failed"); break; }
            }
            else {
                HostTerminal("Shared frame processing or retirement failed; output not published");
                break;
            }

            if (fm.n <= 3 || (fm.n % 1800) == 0)
                Log("[host] frame %llu evaluated (nr=%s, iterations=%u)", (unsigned long long)fm.n,
                    fm.nr_enabled ? "on" : "off", fm.iterations);
            PumpPresent();
        }
        else
        {
            Log("[host] unknown tag 0x%02X", tag);
            break;
        }
    }
    const bool retired = !g_queue_retirement_unproven && WaitFenceValue(h.fence, h.fence_value, 15000);
    if (!retired) {
        HostTerminal("Host queue retirement unproven; terminating without publishing output or releasing in-flight resources");
        DisconnectNamedPipe(pipe);
        ExitProcess(1);
    }
    DisconnectNamedPipe(pipe);
    CloseHandle(pipe);
    if (game_out != nullptr) CloseHandle(game_out);
    if (game_in != nullptr) CloseHandle(game_in);
    if (hout != nullptr) CloseHandle(hout);
    if (hin != nullptr) CloseHandle(hin);
    if (hgame != nullptr) CloseHandle(hgame);
    ReleasePtr(h.iter_scratch[0]);
    ReleasePtr(h.iter_scratch[1]);
    for (int eye = 0; eye < 2; ++eye)
        for (int i = 0; i < 2; ++i)
            ReleasePtr(h.eye_scratch[eye][i]);
    for (int i = 0; i < FEED_SLOTS; ++i)
    {
        ReleasePtr(h.local_tex[i]);
        ReleasePtr(h.tex[i]);
    }
    for (int eye = 0; eye < 2; ++eye)
        for (int i = 0; i < FEED_SLOTS; ++i)
            ReleasePtr(h.eye_tex[eye][i]);
    return g_host_terminal ? 1 : 0;
}

// ---------------------------------------------------------------------------

int main(int argc, char **argv)
{
    GetModuleFileNameA(nullptr, g_log_path, MAX_PATH);
    if (char *s = strrchr(g_log_path, '\\'))
        strcpy_s(s + 1, MAX_PATH - (s + 1 - g_log_path), "dlss5-feed-host.log");
    { FILE *f = nullptr; if (fopen_s(&f, g_log_path, "w") == 0 && f) fclose(f); }

    Log("dlss5-feed-host64 (built %s %s)", __DATE__, __TIME__);

    HRESULT co_hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(co_hr) && co_hr != RPC_E_CHANGED_MODE) { Log("[host] COM init failed 0x%08X", co_hr); return 1; }

    bool  test = false, hide = false, image = false, probe_vr = false;
    DWORD pid = 0;
    std::wstring image_in, image_out;
    int image_frames = 12;
    DXGI_FORMAT image_format = DXGI_FORMAT_R16G16B16A16_FLOAT;
    UINT probe_w = 0, probe_h = 0;
    float probe_scale = 0.333f;
    for (int i = 1; i < argc; ++i)
    {
        if      (strcmp(argv[i], "--test") == 0) test = true;
        else if ((strcmp(argv[i], "--image") == 0 || strcmp(argv[i], "--image-rgba8") == 0 ||
                  strcmp(argv[i], "--image-bgra8") == 0) && i + 2 < argc)
        {
            if (strcmp(argv[i], "--image-rgba8") == 0) image_format = DXGI_FORMAT_R8G8B8A8_UNORM;
            else if (strcmp(argv[i], "--image-bgra8") == 0) image_format = DXGI_FORMAT_B8G8R8A8_UNORM;
            image = true;
            image_in = WideArg(argv[++i]);
            image_out = WideArg(argv[++i]);
            if (i + 1 < argc && argv[i + 1][0] != '-') image_frames = atoi(argv[++i]);
        }
        else if (strcmp(argv[i], "--probe-vr") == 0 && i + 2 < argc)
        {
            probe_vr = true;
            probe_w = static_cast<UINT>(strtoul(argv[++i], nullptr, 10));
            probe_h = static_cast<UINT>(strtoul(argv[++i], nullptr, 10));
            if (i + 1 < argc && argv[i + 1][0] != '-') probe_scale = static_cast<float>(atof(argv[++i]));
        }
        else if (strcmp(argv[i], "--hide") == 0) hide = true;
        else pid = static_cast<DWORD>(strtoul(argv[i], nullptr, 10));
    }
    if (!test && !image && !probe_vr && pid == 0)
    {
        Log("usage: dlss5-feed-host64 --test | dlss5-feed-host64 --image input.png output.png [frames] | dlss5-feed-host64 --probe-vr output_w output_h [render_scale] | dlss5-feed-host64 <game pid> [--hide]");
        return 1;
    }
    g_show_window = !test && !image && !probe_vr && !hide;   // the visible window carries the DLSS 5 add-on's tuning panel

    if (!ConfigurePhoenixHost()) return 1;

    if (!InitDisguise()) return 1;
    if (!WaitForPhoenixFeeder(5000)) return 1;
    LogFeederModules("startup");

    int rc = test ? RunTest() : (image ? RunImage(image_in.c_str(), image_out.c_str(), image_frames, image_format) :
             (probe_vr ? RunVrProbe(probe_w, probe_h, probe_scale) : Serve(pid)));
    if (g_queue_retirement_unproven || !WaitFenceValue(h.fence, h.fence_value, 15000)) {
        HostTerminal("Host shutdown could not prove queue retirement");
        ExitProcess(1);
    }
    if (SUCCEEDED(co_hr)) CoUninitialize();
    return rc;
}
