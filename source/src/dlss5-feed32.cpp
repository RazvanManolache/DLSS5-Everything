// dlss5-feed - the in-game half of DLSS5-Feeder for D3D9/D3D11/D3D12 games.
//
// The game process does not host NGX or the DLSS 5 add-on directly. This add-on
// creates shared resources, copies the frame plus guide textures into them, and
// lets dlss5-feed-host64.exe -- spawned from the host64\ subfolder, where
// ReShade x64 + renodx-dlss5.addon64 live -- run the DLSS/DLSSNR evaluate. The
// result comes back through the shared Output resource and is presented over the
// game backbuffer.
//
// If the host dies, the pipe breaks and the game just renders normally.

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d9.h>
#include <d3d11_4.h>
#include <d3d12.h>
#include <dxgi1_2.h>
#include <d3dcompiler.h>
#include <cstdio>
#include <cstdarg>
#include <cstdint>
#include <cstring>
#include <cstdlib>
#include <cwchar>
#include <string>
#include <vector>
#include <algorithm>

#define ImTextureID ImU64   // required by reshade_overlay.hpp before including imgui.h
#include <imgui.h>
#include <reshade.hpp>

#include "feed_ipc.h"

#define FEED_VERSION "0.6.0-beta.31"
#ifdef _WIN64
#define FEED_ARCH_LABEL "64-bit"
#else
#define FEED_ARCH_LABEL "32-bit"
#endif

extern "C" __declspec(dllexport) const char *NAME = "DLSS 5 Feed (" FEED_ARCH_LABEL ") " FEED_VERSION;
extern "C" __declspec(dllexport) const char *DESCRIPTION =
    "Feeds DLSS 5 neural rendering in games without DLSS: ships the frame, depth and "
    "motion vectors to a 64-bit helper process (host64\\dlss5-feed-host64.exe) over cross-process "
    "shared GPU textures or a CPU fallback bridge, and blits the neural result back. Needs DLSS5_Feed.fx and a motion-vector "
    "provider (DRME, qUINT, Launchpad, VORT or LumeniteFX; pick it with the DLSS5_MV_PROVIDER definition). "
    "Settings in dlss5-feed.cfg.";

// ---------------------------------------------------------------------------
// Logging (same shape as the 64-bit add-on)
// ---------------------------------------------------------------------------

static HMODULE          g_self;
static char             g_log_path[MAX_PATH];
static CRITICAL_SECTION g_log_cs;
static HANDLE           g_key_hook_thread;
static HANDLE           g_key_hook_stop;
static DWORD            g_key_hook_thread_id;
static HHOOK            g_key_hook;
static volatile LONG    g_key_hook_shot_request;

static void Log(const char *fmt, ...)
{
    char line[2048];
    va_list ap;
    va_start(ap, fmt);
    _vsnprintf_s(line, sizeof(line), _TRUNCATE, fmt, ap);
    va_end(ap);
    SYSTEMTIME st;
    GetLocalTime(&st);
    EnterCriticalSection(&g_log_cs);
    FILE *f = nullptr;
    if (fopen_s(&f, g_log_path, "a") == 0 && f != nullptr)
    {
        fprintf(f, "%02u:%02u:%02u.%03u  %s\n", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, line);
        fclose(f);
    }
    LeaveCriticalSection(&g_log_cs);
}

static void Warn(const char *fmt, ...)
{
    char line[1024];
    va_list ap;
    va_start(ap, fmt);
    _vsnprintf_s(line, sizeof(line), _TRUNCATE, fmt, ap);
    va_end(ap);
    Log("%s", line);
    char tagged[1100];
    _snprintf_s(tagged, sizeof(tagged), _TRUNCATE, "[DLSS 5 Feed %s] %s", FEED_ARCH_LABEL, line);
    reshade::log::message(reshade::log::level::warning, tagged);
}

static const char *volatile g_where = "starting up";
static void Breadcrumb(const char *what) { g_where = what; }

// ---------------------------------------------------------------------------
// Configuration: same dlss5-feed.cfg as the 64-bit add-on (extra keys ignored)
// ---------------------------------------------------------------------------

struct Cfg
{
    int   enabled;
    int   mode;            // 0 inert, 1 transport test THROUGH the host (no NGX), 2 full DLSS path
    int   hdr;             // -1 auto, 0/1 force
    int   depth_inverted;  // -1 auto (RESHADE_DEPTH_INPUT_IS_REVERSED), 0/1 force
    int   flags;           // -1 auto, else raw DLSS.Feature.Create.Flags (host applies)
    int   reset_every;
    int   log_frames;
    int   host_window;     // 1 = show the host's window (it carries the DLSS 5 tuning panel: press Home there)
    int   hotkey_toggle;   // virtual-key code, 0 disables; default off
    int   hotkey_compare;  // virtual-key code, 0 disables; default VK_F9
    int   hotkey_screenshot; // virtual-key code, 0 disables; default VK_SNAPSHOT
    int   compare_mode;    // 0 original, 1 DLSS output, 2 original|DLSS, 3 original|difference, 4 difference|DLSS
    int   iterations;      // 1..10 evaluates of the same frame before presenting the result
    float render_scale;    // mode 2 only: input size / output size, 1.0 = DLAA/native
    float mv_scale_x, mv_scale_y;
    int   native_probe_seconds; // non-zero: wait for root RenoDX native NGX signal before fallback feeding
};

static Cfg g_cfg = { 1, 2, -1, -1, -1, 0, 3, 1, 0, VK_F9, VK_SNAPSHOT, 1, 1, 1.0f, 1.0f, 1.0f, 0 };
static bool g_nr_enabled = true;
static ULONGLONG g_native_probe_start = 0;
static bool g_native_probe_decided = false;
static bool g_native_probe_use_fallback = true;
static bool g_native_probe_logged = false;

static void FeedDisable(const char *why);

static void CfgPath(char *out)
{
    GetModuleFileNameA(g_self, out, MAX_PATH);
    if (char *s = strrchr(out, '\\'))
        strcpy_s(s + 1, MAX_PATH - (s + 1 - out), "dlss5-feed.cfg");
}

static void CfgWriteDefault()
{
    char path[MAX_PATH];
    CfgPath(path);
    if (GetFileAttributesA(path) != INVALID_FILE_ATTRIBUTES) return;
    FILE *f = nullptr;
    if (fopen_s(&f, path, "w") != 0 || f == nullptr) return;
    fprintf(f, "enabled=%d\nmode=%d\nhdr=%d\ndepth_inverted=%d\nflags=%d\nreset_every=%d\nlog_frames=%d\n"
               "host_window=%d\nhotkey_toggle=%d\nhotkey_compare=%d\nhotkey_screenshot=%d\ncompare_mode=%d\niterations=%d\nrender_scale=%.3f\nmv_scale_x=%.3f\nmv_scale_y=%.3f\nnative_probe_seconds=%d\n",
            g_cfg.enabled, g_cfg.mode, g_cfg.hdr, g_cfg.depth_inverted, g_cfg.flags, g_cfg.reset_every,
            g_cfg.log_frames, g_cfg.host_window, g_cfg.hotkey_toggle, g_cfg.hotkey_compare, g_cfg.hotkey_screenshot,
            g_cfg.compare_mode, g_cfg.iterations, g_cfg.render_scale, g_cfg.mv_scale_x, g_cfg.mv_scale_y,
            g_cfg.native_probe_seconds);
    fclose(f);
}

// Writes every current value, overwriting the file -- used by the overlay page so an
// edit made there survives the next CfgReload() instead of being read back off the
// stale on-disk copy 60 frames later.
static void CfgSave()
{
    char path[MAX_PATH];
    CfgPath(path);
    FILE *f = nullptr;
    if (fopen_s(&f, path, "w") != 0 || f == nullptr) return;
    fprintf(f, "enabled=%d\nmode=%d\nhdr=%d\ndepth_inverted=%d\nflags=%d\nreset_every=%d\nlog_frames=%d\n"
               "host_window=%d\nhotkey_toggle=%d\nhotkey_compare=%d\nhotkey_screenshot=%d\ncompare_mode=%d\niterations=%d\nrender_scale=%.3f\nmv_scale_x=%.3f\nmv_scale_y=%.3f\nnative_probe_seconds=%d\n",
            g_cfg.enabled, g_cfg.mode, g_cfg.hdr, g_cfg.depth_inverted, g_cfg.flags, g_cfg.reset_every,
            g_cfg.log_frames, g_cfg.host_window, g_cfg.hotkey_toggle, g_cfg.hotkey_compare, g_cfg.hotkey_screenshot,
            g_cfg.compare_mode, g_cfg.iterations, g_cfg.render_scale, g_cfg.mv_scale_x, g_cfg.mv_scale_y,
            g_cfg.native_probe_seconds);
    fclose(f);
}

static bool CfgReload()   // true when a build-affecting value changed
{
    char path[MAX_PATH];
    CfgPath(path);
    FILE *f = nullptr;
    if (fopen_s(&f, path, "r") != 0 || f == nullptr) return false;
    Cfg next = g_cfg;
    char line[160];
    while (fgets(line, sizeof(line), f) != nullptr)
    {
        char  key[64];
        float val = 0.0f;
        if (sscanf_s(line, "%63[^=]=%f", key, static_cast<unsigned>(sizeof(key)), &val) != 2) continue;
        const int iv = static_cast<int>(val);
        if      (_stricmp(key, "enabled")        == 0) next.enabled        = iv;
        else if (_stricmp(key, "mode")           == 0) next.mode           = iv;
        else if (_stricmp(key, "hdr")            == 0) next.hdr            = iv;
        else if (_stricmp(key, "depth_inverted") == 0) next.depth_inverted = iv;
        else if (_stricmp(key, "flags")          == 0) next.flags          = iv;
        else if (_stricmp(key, "reset_every")    == 0) next.reset_every    = iv;
        else if (_stricmp(key, "log_frames")     == 0) next.log_frames     = iv;
        else if (_stricmp(key, "host_window")    == 0) next.host_window    = iv;
        else if (_stricmp(key, "hotkey_toggle")  == 0) next.hotkey_toggle  = iv;
        else if (_stricmp(key, "hotkey_compare") == 0) next.hotkey_compare = iv;
        else if (_stricmp(key, "hotkey_screenshot") == 0) next.hotkey_screenshot = iv;
        else if (_stricmp(key, "compare_mode")   == 0) next.compare_mode   = iv;
        else if (_stricmp(key, "iterations")     == 0) next.iterations     = iv;
        else if (_stricmp(key, "render_scale")   == 0) next.render_scale   = val;
        else if (_stricmp(key, "mv_scale_x")     == 0) next.mv_scale_x     = val;
        else if (_stricmp(key, "mv_scale_y")     == 0) next.mv_scale_y     = val;
        else if (_stricmp(key, "native_probe_seconds") == 0) next.native_probe_seconds = iv;
    }
    fclose(f);
    if (next.mode < 0 || next.mode > 2) next.mode = g_cfg.mode;
    if (next.hotkey_toggle < 0 || next.hotkey_toggle > 255) next.hotkey_toggle = g_cfg.hotkey_toggle;
    if (next.hotkey_compare < 0 || next.hotkey_compare > 255) next.hotkey_compare = g_cfg.hotkey_compare;
    if (next.hotkey_screenshot < 0 || next.hotkey_screenshot > 255) next.hotkey_screenshot = g_cfg.hotkey_screenshot;
    if (next.compare_mode < 0) next.compare_mode = 0;
    if (next.compare_mode > 4) next.compare_mode = 4;
    if (next.iterations < 1) next.iterations = 1;
    if (next.iterations > 10) next.iterations = 10;
    if (next.render_scale < 0.33f) next.render_scale = 0.33f;
    if (next.render_scale > 1.0f) next.render_scale = 1.0f;
    if (next.native_probe_seconds < 0) next.native_probe_seconds = 0;
    if (next.native_probe_seconds > 60) next.native_probe_seconds = 60;
    const bool rebuild = next.mode != g_cfg.mode || next.hdr != g_cfg.hdr ||
                         next.depth_inverted != g_cfg.depth_inverted || next.flags != g_cfg.flags ||
                         next.render_scale != g_cfg.render_scale ||
                         next.mv_scale_x != g_cfg.mv_scale_x || next.mv_scale_y != g_cfg.mv_scale_y;
    const bool changed = memcmp(&next, &g_cfg, sizeof(Cfg)) != 0;
    if (changed)
    {
        if (next.native_probe_seconds != g_cfg.native_probe_seconds)
        {
            g_native_probe_start = 0;
            g_native_probe_decided = false;
            g_native_probe_use_fallback = true;
            g_native_probe_logged = false;
        }
        g_cfg = next;
        Log("[feed32] config: enabled=%d mode=%d hdr=%d depth_inverted=%d flags=%d reset_every=%d compare_mode=%d iterations=%d render_scale=%.3f native_probe_seconds=%d",
            g_cfg.enabled, g_cfg.mode, g_cfg.hdr, g_cfg.depth_inverted, g_cfg.flags, g_cfg.reset_every,
            g_cfg.compare_mode, g_cfg.iterations, g_cfg.render_scale, g_cfg.native_probe_seconds);
    }
    return rebuild;
}

static void RootPathA(const char *name, char *out)
{
    GetModuleFileNameA(g_self, out, MAX_PATH);
    if (char *s = strrchr(out, '\\'))
        strcpy_s(s + 1, MAX_PATH - (s + 1 - out), name);
}

static bool RootRenoDxAddonPresent()
{
    char path[MAX_PATH];
    RootPathA("renodx-dlss5.addon64", path);
    return GetFileAttributesA(path) != INVALID_FILE_ATTRIBUTES;
}

static bool FileTailContainsAny(const char *path, const char *const *needles, int needle_count)
{
    HANDLE f = CreateFileA(path, GENERIC_READ,
                           FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) return false;

    LARGE_INTEGER size = {};
    if (!GetFileSizeEx(f, &size))
    {
        CloseHandle(f);
        return false;
    }

    const DWORD max_read = 256u * 1024u;
    const DWORD to_read = static_cast<DWORD>(std::min<LONGLONG>(size.QuadPart, max_read));
    LARGE_INTEGER pos = {};
    pos.QuadPart = size.QuadPart > to_read ? size.QuadPart - to_read : 0;
    SetFilePointerEx(f, pos, nullptr, FILE_BEGIN);

    std::string text;
    text.resize(to_read);
    DWORD got = 0;
    const BOOL ok = ReadFile(f, text.data(), to_read, &got, nullptr);
    CloseHandle(f);
    if (!ok) return false;
    text.resize(got);

    for (int i = 0; i < needle_count; ++i)
        if (text.find(needles[i]) != std::string::npos)
            return true;
    return false;
}

static bool NativeRenoDxHasNgxSignal()
{
    char path[MAX_PATH];
    RootPathA("ReShade.log", path);
    static const char *const needles[] = {
        "NGX feature create intercepted",
        "NGX EvaluateFeature intercepted",
        "ACTIVE - NR",
        "NR INJECTED",
        "Successful NR"
    };
    return FileTailContainsAny(path, needles, static_cast<int>(sizeof(needles) / sizeof(needles[0])));
}

static bool NativeProbeAllowsFallback()
{
    if (g_cfg.native_probe_seconds <= 0 || !RootRenoDxAddonPresent())
        return true;

    if (g_native_probe_decided)
        return g_native_probe_use_fallback;

    const ULONGLONG now = GetTickCount64();
    if (g_native_probe_start == 0)
    {
        g_native_probe_start = now;
        Log("[feed32] native probe armed for %d second(s); feeder fallback waits for root RenoDX NGX activity",
            g_cfg.native_probe_seconds);
    }

    if (NativeRenoDxHasNgxSignal())
    {
        g_native_probe_decided = true;
        g_native_probe_use_fallback = false;
        FeedDisable("native RenoDX NGX signal detected; feeder fallback disabled");
        return false;
    }

    const ULONGLONG elapsed_ms = now - g_native_probe_start;
    if (elapsed_ms < static_cast<ULONGLONG>(g_cfg.native_probe_seconds) * 1000ull)
    {
        if (!g_native_probe_logged && elapsed_ms > 2000)
        {
            g_native_probe_logged = true;
            Log("[feed32] waiting for native RenoDX signal before starting fallback");
        }
        return false;
    }

    g_native_probe_decided = true;
    g_native_probe_use_fallback = true;
    Log("[feed32] no native RenoDX NGX signal detected after %d second(s); starting feeder fallback",
        g_cfg.native_probe_seconds);
    return true;
}

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------

static const char *kEffectFile    = "DLSS5_Feed.fx";
static const char *kTechnique     = "DLSS5_Feed";
// Known motion-vector providers, keyed by the DLSS5_MV_PROVIDER value DLSS5_Feed.fx
// was compiled with (0 texMotionVectors, 1 Launchpad, 2 VORT, 3 LumeniteFX Kernel,
// 4 LumeniteFX QuantMotion). Name checks only, for the status line and a mismatch warning.
static const struct { int mode; const char *file, *tech; } kMvProviders[] = {
    { 0, "MotionEstimation.fx",     "DRME" },
    { 0, "qUINT_motionvectors.fx",  "MotionVectors" },
    { 0, "dh_uber_motion.fx",       "DH_UBER_MOTION_020" },
    { 1, "MartysMods_LAUNCHPAD.fx", "MartysMods_Launchpad" },
    { 2, "vort_Motion.fx",          "vort_MotionEffects" },
    { 3, "lumenite_Kernel.fx",      "Lumenite_Kernel" },
    { 4, "lumenite_QuantMotion.fx", "Lumenite_QuantMotion" },
};
static const char *kMvModeName[] = { "texMotionVectors", "Launchpad", "VORT", "LumeniteFX Kernel", "LumeniteFX QuantMotion" };
static const int   kMvModeCount  = static_cast<int>(sizeof(kMvModeName) / sizeof(kMvModeName[0]));

static char g_mv_status[192]  = "not checked yet";
static char g_mv_problem[640] = "";

// A provider whose effect failed to compile is still listed (and can be "enabled") but writes
// nothing. ReShade logs the compiler error next to the game; the last line about that file
// -- an error or a "Successfully compiled" -- is the current state. Same as the 64-bit add-on.
static bool ProviderCompileError(const char *file, char *out, size_t out_size)
{
    out[0] = '\0';
    char path[MAX_PATH];
    GetModuleFileNameA(g_self, path, MAX_PATH);
    if (char *s = strrchr(path, '\\')) strcpy_s(s + 1, MAX_PATH - (s + 1 - path), "ReShade.log");
    FILE *f = nullptr;
    if (fopen_s(&f, path, "rb") != 0 || f == nullptr) return false;
    fseek(f, 0, SEEK_END);
    const long size = ftell(f);
    const long take = size < 512 * 1024 ? size : 512 * 1024;
    fseek(f, size - take, SEEK_SET);
    std::string buf(static_cast<size_t>(take), '\0');
    const size_t got = fread(buf.data(), 1, buf.size(), f);
    fclose(f);
    buf.resize(got);

    char needle_err[MAX_PATH], needle_ok[MAX_PATH];
    _snprintf_s(needle_err, sizeof(needle_err), _TRUNCATE, "\\%s(", file);
    _snprintf_s(needle_ok,  sizeof(needle_ok),  _TRUNCATE, "\\%s'",  file);
    bool failed = false;
    size_t pos = 0;
    while (pos < buf.size())
    {
        size_t eol = buf.find('\n', pos);
        if (eol == std::string::npos) eol = buf.size();
        const std::string line = buf.substr(pos, eol - pos);
        pos = eol + 1;
        if (line.find(needle_ok) != std::string::npos && line.find("Successfully compiled") != std::string::npos)
            failed = false;
        else if (const size_t at = line.find(needle_err); at != std::string::npos && line.find("error") != std::string::npos)
        {
            failed = true;
            std::string msg = line.substr(at + 1);
            while (!msg.empty() && (msg.back() == '\r' || msg.back() == ' ')) msg.pop_back();
            strncpy_s(out, out_size, msg.c_str(), _TRUNCATE);
        }
    }
    return failed;
}

static int ReadMvProviderMode(reshade::api::effect_runtime *rt)
{
    char v[16] = {};
    int mode = 0;
    if (rt->get_preprocessor_definition_for_effect(kEffectFile, "DLSS5_MV_PROVIDER", v) ||
        rt->get_preprocessor_definition("DLSS5_MV_PROVIDER", v))
        mode = atoi(v);
    return (mode < 0 || mode >= kMvModeCount) ? 0 : mode;
}

struct Feed32
{
    reshade::api::effect_runtime          *runtime;
    reshade::api::effect_technique         technique;
    reshade::api::effect_technique         launchpad;
    reshade::api::effect_texture_variable  mv_var;
    reshade::api::effect_texture_variable  depth_var;
    reshade::api::effect_texture_variable  mask_var;

    bool depth_reversed;
    bool handles_ok;
    bool mask_available;
    bool bound_mask_available;
    bool missing_reported;

    bool disabled;
    int  consecutive_fails;

    // host + pipe
    HANDLE hproc;
    HANDLE pipe;

    // shared textures (created HERE, opened by the host)
    ID3D11Texture2D *tex[FEED_SLOTS];
    HANDLE           tex_handle[FEED_SLOTS];
    ID3D11RenderTargetView *tex_rtv[FEED_SLOTS];
    ID3D11ShaderResourceView *output_srv;
    ID3D11Fence     *fence_in;    // we signal
    ID3D11Fence     *fence_out;   // host signals
    ID3D11DeviceContext4 *ctx4;
    ID3D11Device    *dev;         // not owned

    bool        built;
    UINT        width, height;
    UINT        input_width, input_height;
    DXGI_FORMAT bb_fmt, color_fmt, output_fmt;
    UINT64      frame_n;
    bool        need_reset;
    bool        dual_shot_pending;
    int         dual_shot_delay;

    // blit
    ID3D11VertexShader *blit_vs;
    ID3D11PixelShader  *blit_ps;
    ID3D11PixelShader  *compare_ps;
    ID3D11PixelShader  *diff_ps;
    ID3D11PixelShader  *diff_enhanced_ps;
    ID3D11SamplerState *blit_sampler;
    ID3D11Texture2D    *color_stage;
    ID3D11ShaderResourceView *color_stage_srv;

    // Native D3D9 CPU bridge. This avoids dgVoodoo by reading the D3D9
    // backbuffer into CPU memory, letting the x64 host evaluate, then writing
    // CPU pixels back to the D3D9 backbuffer.
    IDirect3DDevice9  *dev9;        // owned COM reference while the native D3D9 bridge is built
    IDirect3DSurface9 *stage9;
    IDirect3DSurface9 *resolve9;
    IDirect3DSurface9 *upload9;
    D3DFORMAT          d3d9_fmt;
    std::vector<BYTE>  cpu_color_rgba;
    std::vector<BYTE>  cpu_output_rgba;
    std::vector<BYTE>  cpu_present_rgba;

    // Native D3D12 CPU bridge. Used when a DX12 game has RenoDX loaded in the
    // root folder but never makes native NGX/DLSS calls for RenoDX to intercept.
    ID3D12Device      *dev12;       // owned COM reference while the native D3D12 bridge is built
    ID3D12Resource    *readback12;
    ID3D12Resource    *upload12;
    DXGI_FORMAT        d3d12_fmt;
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT d3d12_footprint;
    UINT               d3d12_rows;
    UINT64             d3d12_row_bytes;
    UINT64             d3d12_total_bytes;
    bool               d3d12_capture_valid;

    UINT64   frames_done;
    UINT64   finish_events;
    UINT64   technique_events;
    UINT64   last_technique_frame;
    bool     using_zero_guides;
    LONGLONG qpf, cpu_ticks, span_start;
    UINT64   timed_frames;
};

static Feed32 g;

template <typename T> static void SafeRelease(T *&p) { if (p) { p->Release(); p = nullptr; } }

static bool ForegroundIsThisProcess()
{
    HWND hwnd = GetForegroundWindow();
    if (hwnd == nullptr) return false;
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    return pid == GetCurrentProcessId();
}

static DXGI_FORMAT TypedColorFormat(DXGI_FORMAT f)
{
    switch (f)
    {
    case DXGI_FORMAT_R8G8B8A8_TYPELESS: case DXGI_FORMAT_R8G8B8A8_UNORM: case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
        return DXGI_FORMAT_R8G8B8A8_UNORM;
    case DXGI_FORMAT_B8G8R8A8_TYPELESS: case DXGI_FORMAT_B8G8R8A8_UNORM: case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
        return DXGI_FORMAT_B8G8R8A8_UNORM;
    case DXGI_FORMAT_R10G10B10A2_TYPELESS: case DXGI_FORMAT_R10G10B10A2_UNORM:
        return DXGI_FORMAT_R10G10B10A2_UNORM;
    case DXGI_FORMAT_R16G16B16A16_TYPELESS: case DXGI_FORMAT_R16G16B16A16_FLOAT:
        return DXGI_FORMAT_R16G16B16A16_FLOAT;
    case DXGI_FORMAT_R11G11B10_FLOAT:
        return DXGI_FORMAT_R11G11B10_FLOAT;
    default:
        return DXGI_FORMAT_UNKNOWN;
    }
}

static DXGI_FORMAT OutputFormatFor(DXGI_FORMAT color_typed)
{
    switch (color_typed)
    {
    case DXGI_FORMAT_R16G16B16A16_FLOAT: return DXGI_FORMAT_R16G16B16A16_FLOAT;
    case DXGI_FORMAT_R11G11B10_FLOAT:    return DXGI_FORMAT_R16G16B16A16_FLOAT;
    case DXGI_FORMAT_R10G10B10A2_UNORM:  return DXGI_FORMAT_R10G10B10A2_UNORM;
    default:                             return DXGI_FORMAT_R8G8B8A8_UNORM;
    }
}

static bool IsHdrFormat(DXGI_FORMAT typed)
{
    return typed == DXGI_FORMAT_R16G16B16A16_FLOAT || typed == DXGI_FORMAT_R11G11B10_FLOAT;
}

static void FeedDisable(const char *why)
{
    if (g.disabled) return;
    g.disabled = true;
    Warn("stopped: %s. The game renders normally. See dlss5-feed.log for the detail.", why);
}

// A build can fail transiently -- the game is mid-resolution-change, or the host's NGX
// needs a reinit first. Retrying every frame just hammers a broken NGX (and spams the
// log), so back off exponentially instead of disabling the feed for the whole session:
// the user should not have to restart the game because one mode switch went wrong.
static UINT64 g_retry_at;   // GetTickCount64 deadline

static void FeedFail(const char *what)
{
    const int n = ++g.consecutive_fails;
    const DWORD wait_ms = n <= 3 ? 1000u : (n <= 6 ? 5000u : 30000u);
    g_retry_at = GetTickCount64() + wait_ms;
    Log("[feed32] failure: %s (attempt %d; retrying in %lu ms)", what, n, wait_ms);
}

// ---------------------------------------------------------------------------
// Host process + pipe
// ---------------------------------------------------------------------------

static void HostClose()
{
    if (g.pipe != nullptr)  { CloseHandle(g.pipe); g.pipe = nullptr; }
    if (g.hproc != nullptr)
    {
        if (WaitForSingleObject(g.hproc, 2000) != WAIT_OBJECT_0)
            TerminateProcess(g.hproc, 0);      // it did not exit on the pipe break
        CloseHandle(g.hproc);
        g.hproc = nullptr;
    }
}

static void ResetBridgeForRebuild(const char *why);

static void HostLost(const char *why)
{
    Log("[feed32] host lost: %s", why);
    ResetBridgeForRebuild("host lost; restarting the shared bridge");
    FeedFail("host lost");
}

static bool HostAlive()
{
    return g.hproc != nullptr && WaitForSingleObject(g.hproc, 0) == WAIT_TIMEOUT;
}

static void ReleaseShared();
static void ApplyNrToggleToHost();

static const char *HotkeyName(int vk)
{
    if (vk >= VK_F1 && vk <= VK_F24)
    {
        static char name[8];
        sprintf_s(name, "F%d", vk - VK_F1 + 1);
        return name;
    }
    return vk == 0 ? "disabled" : "custom";
}

static const char *CompareModeName(int mode)
{
    switch (mode)
    {
    case 0: return "original";
    case 1: return "DLSS output";
    case 2: return "original | DLSS";
    case 3: return "original | difference";
    case 4: return "difference | DLSS";
    default: return "unknown";
    }
}

static void CycleCompareMode(const char *source)
{
    static volatile LONG last_tick = 0;
    const LONG now = static_cast<LONG>(GetTickCount());
    const LONG old_tick = InterlockedCompareExchange(&last_tick, now, last_tick);
    if (now - old_tick < 450)
        return;

    const int old_mode = g_cfg.compare_mode;
    g_cfg.compare_mode = (g_cfg.compare_mode + 1) % 5;
    CfgSave();
    Warn("Display view: %s (%s cycles)", CompareModeName(g_cfg.compare_mode),
         HotkeyName(g_cfg.hotkey_compare));
    Log("[feed32] display hotkey changed compare_mode %d -> %d via %s", old_mode, g_cfg.compare_mode, source);
}

static void ToggleFeedHotkey()
{
    g_nr_enabled = !g_nr_enabled;
    g.need_reset = true;
    ApplyNrToggleToHost();
}

static void PollToggleHotkey()
{
    static bool was_down = false;
    static UINT64 last_toggle = 0;
    const int vk = g_cfg.hotkey_toggle;
    if (vk <= 0 || vk > 255)
    {
        was_down = false;
        return;
    }

    const bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
    const UINT64 now = GetTickCount64();
    if (down && !was_down && now - last_toggle >= 1000)
    {
        ToggleFeedHotkey();
        last_toggle = now;
    }
    was_down = down;
}

static void PollCompareHotkey()
{
    static bool was_down = false;
    static UINT64 last_toggle = 0;
    const int vk = g_cfg.hotkey_compare;
    if (vk <= 0 || vk > 255)
    {
        was_down = false;
        return;
    }

    const bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
    const UINT64 now = GetTickCount64();
    if (down && !was_down && now - last_toggle >= 500)
    {
        CycleCompareMode("poll");
        last_toggle = now;
    }
    was_down = down;
}

static void PollDualScreenshotHotkey()
{
    static UINT64 last_shot = 0;
    static bool was_down = false;
    const int vk = g_cfg.hotkey_screenshot;
    if (vk <= 0 || vk > 255)
    {
        was_down = false;
        return;
    }

    if (InterlockedExchange(&g_key_hook_shot_request, 0) != 0)
    {
        g.dual_shot_pending = true;
        g.dual_shot_delay = 2;
        last_shot = GetTickCount64();
        Log("[feed32] PrintScreen captured by feeder hook; queued normal+DLSS capture");
        was_down = true;
        return;
    }

    if (vk == VK_SNAPSHOT)
        return; // handled by the feeder hook so Windows/ReShade do not also take focus

    const bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
    const UINT64 now = GetTickCount64();
    if (down && !was_down && now - last_shot >= 1000)
    {
        g.dual_shot_pending = true;
        g.dual_shot_delay = 10;
        last_shot = now;
        Log("[feed32] screenshot hotkey queued delayed normal+DLSS capture");
    }
    was_down = down;
}

static LRESULT CALLBACK FeedKeyboardHook(int code, WPARAM wparam, LPARAM lparam)
{
    if (code == HC_ACTION && ForegroundIsThisProcess())
    {
        auto *kb = reinterpret_cast<KBDLLHOOKSTRUCT *>(lparam);
        if (kb != nullptr)
        {
            const bool down = wparam == WM_KEYDOWN || wparam == WM_SYSKEYDOWN;
            if (down && g_cfg.hotkey_compare > 0 && g_cfg.hotkey_compare <= 255 &&
                kb->vkCode == static_cast<DWORD>(g_cfg.hotkey_compare))
            {
                static UINT64 last_hook_compare = 0;
                const UINT64 now = GetTickCount64();
                if (now - last_hook_compare >= 500)
                {
                    CycleCompareMode("keyboard hook");
                    last_hook_compare = now;
                }
                return 1; // stop the game/ReShade from consuming the display-cycle key
            }

            if (down && g_cfg.hotkey_toggle > 0 && g_cfg.hotkey_toggle <= 255 &&
                kb->vkCode == static_cast<DWORD>(g_cfg.hotkey_toggle))
            {
                static UINT64 last_hook_toggle = 0;
                const UINT64 now = GetTickCount64();
                if (now - last_hook_toggle >= 1000)
                {
                    ToggleFeedHotkey();
                    last_hook_toggle = now;
                }
                return 1;
            }

            if (g_cfg.hotkey_screenshot == VK_SNAPSHOT && kb->vkCode == VK_SNAPSHOT)
            {
                static UINT64 last_hook_shot = 0;
                if (down)
                {
                    const UINT64 now = GetTickCount64();
                    if (now - last_hook_shot >= 1000)
                    {
                        InterlockedExchange(&g_key_hook_shot_request, 1);
                        last_hook_shot = now;
                    }
                }
                return 1; // stop Windows snipping/desktop focus and ReShade's own screenshot key
            }
        }
    }
    return CallNextHookEx(g_key_hook, code, wparam, lparam);
}

static DWORD WINAPI KeyboardHookThread(void *)
{
    g_key_hook_thread_id = GetCurrentThreadId();
    MSG msg = {};
    PeekMessageW(&msg, nullptr, WM_USER, WM_USER, PM_NOREMOVE);
    g_key_hook = SetWindowsHookExW(WH_KEYBOARD_LL, FeedKeyboardHook, g_self, 0);
    if (g_key_hook == nullptr)
    {
        Log("[feed32] keyboard hook failed: %lu", GetLastError());
        return 0;
    }
    Log("[feed32] keyboard hook installed");

    HANDLE wait_handles[1] = { g_key_hook_stop };
    for (;;)
    {
        const DWORD wait = MsgWaitForMultipleObjects(1, wait_handles, FALSE, INFINITE, QS_ALLINPUT);
        if (wait == WAIT_OBJECT_0)
            break;
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    UnhookWindowsHookEx(g_key_hook);
    g_key_hook = nullptr;
    Log("[feed32] keyboard hook removed");
    return 0;
}

static void StartKeyboardHook()
{
    if (g_key_hook_thread != nullptr)
        return;
    g_key_hook_stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (g_key_hook_stop == nullptr)
    {
        Log("[feed32] keyboard hook event failed: %lu", GetLastError());
        return;
    }
    g_key_hook_thread = CreateThread(nullptr, 0, KeyboardHookThread, nullptr, 0, nullptr);
    if (g_key_hook_thread == nullptr)
    {
        Log("[feed32] keyboard hook thread failed: %lu", GetLastError());
        CloseHandle(g_key_hook_stop);
        g_key_hook_stop = nullptr;
    }
}

static void StopKeyboardHook()
{
    if (g_key_hook_thread == nullptr)
        return;
    SetEvent(g_key_hook_stop);
    if (g_key_hook_thread_id != 0)
        PostThreadMessageW(g_key_hook_thread_id, WM_NULL, 0, 0);
    WaitForSingleObject(g_key_hook_thread, 1000);
    CloseHandle(g_key_hook_thread);
    g_key_hook_thread = nullptr;
    if (g_key_hook_stop != nullptr)
    {
        CloseHandle(g_key_hook_stop);
        g_key_hook_stop = nullptr;
    }
    g_key_hook_thread_id = 0;
}

static bool PipeWrite(const void *buf, DWORD len)
{
    DWORD put = 0;
    return g.pipe != nullptr && WriteFile(g.pipe, buf, len, &put, nullptr) && put == len;
}

static bool PipeRead(void *buf, DWORD len)
{
    DWORD got = 0;
    return g.pipe != nullptr && ReadFile(g.pipe, buf, len, &got, nullptr) && got == len;
}

static bool EnsureHost()
{
    if (g.pipe != nullptr && HostAlive()) return true;
    HostClose();

    char dir[MAX_PATH];
    GetModuleFileNameA(g_self, dir, MAX_PATH);
    if (char *s = strrchr(dir, '\\')) *(s + 1) = '\0';

    char exe[MAX_PATH], cmd[MAX_PATH + 32], wd[MAX_PATH];
    sprintf_s(exe, "%shost64\\dlss5-feed-host64.exe", dir);
    sprintf_s(wd, "%shost64", dir);
    if (GetFileAttributesA(exe) == INVALID_FILE_ATTRIBUTES)
    {
        Warn("host64\\dlss5-feed-host64.exe not found next to the add-on");
        FeedDisable("the 64-bit host is not installed");
        return false;
    }
    sprintf_s(cmd, "\"%s\" %lu%s", exe, GetCurrentProcessId(), g_cfg.host_window ? "" : " --hide");

    STARTUPINFOA si = { sizeof(si) };
    PROCESS_INFORMATION pi = {};
    Breadcrumb("spawning the 64-bit host");
    if (!CreateProcessA(nullptr, cmd, nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, wd, &si, &pi))
    {
        Log("[feed32] CreateProcess failed %lu", GetLastError());
        FeedDisable("could not start the 64-bit host");
        return false;
    }
    CloseHandle(pi.hThread);
    g.hproc = pi.hProcess;
    Log("[feed32] host spawned (pid %lu)", pi.dwProcessId);

    char name[128];
    sprintf_s(name, FEED_PIPE_FMT, static_cast<unsigned long>(GetCurrentProcessId()));
    for (int i = 0; i < 150 && g.pipe == nullptr; ++i)   // up to 15 s (host loads ReShade + NGX)
    {
        HANDLE p = CreateFileA(name, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        if (p != INVALID_HANDLE_VALUE) { g.pipe = p; break; }
        if (!HostAlive()) { HostLost("exited during startup"); return false; }
        Sleep(100);
    }
    if (g.pipe == nullptr) { HostLost("pipe never appeared"); return false; }

    FeedHello hello = { FEED_IPC_MAGIC, FEED_IPC_VERSION, GetCurrentProcessId() };
    FeedHelloAck ack = {};
    if (!PipeWrite(&hello, sizeof(hello)) || !PipeRead(&ack, sizeof(ack)) ||
        ack.magic != FEED_IPC_MAGIC || ack.version != FEED_IPC_VERSION)
    { HostLost("handshake failed"); return false; }
    Log("[feed32] host connected (protocol v%u)", ack.version);
    return true;
}

// ---------------------------------------------------------------------------
// The host's DLSS 5 settings, controlled from the game's own ReShade panel.
// The renodx add-on reads [RenoDX.DLSS5] from the HOST's ReShade.ini at startup
// (only its own panel can change them live), so applying = write that ini and
// cycle the host. The game renders normally during the ~2 s gap.
// ---------------------------------------------------------------------------

struct HostNR
{
    int   uplift, upscaling, preset, style, automask, uicorr, depth_mode;
    float intensity, structure_, tone, skin, paper_white, transfer_strength, color_strength;
    float mvec_scale_x, mvec_scale_y;
};

static void HostIniPath(char *out)
{
    GetModuleFileNameA(g_self, out, MAX_PATH);
    if (char *s = strrchr(out, '\\'))
        strcpy_s(s + 1, MAX_PATH - (s + 1 - out), "host64\\ReShade.ini");
}

static void ReadHostNR(HostNR *v)
{
    char p[MAX_PATH], buf[64];
    HostIniPath(p);
    v->uplift    = GetPrivateProfileIntA("RenoDX.DLSS5", "NeuralUplift", 1, p);
    v->upscaling = GetPrivateProfileIntA("RenoDX.DLSS5", "NREnableUpscaling", 1, p);
    v->preset    = GetPrivateProfileIntA("RenoDX.DLSS5", "NRPreset", 0, p);
    v->style     = GetPrivateProfileIntA("RenoDX.DLSS5", "NRStyle", 0, p);
    v->automask  = GetPrivateProfileIntA("RenoDX.DLSS5", "NRAutoMask", 1, p);
    v->uicorr    = GetPrivateProfileIntA("RenoDX.DLSS5", "NRUICorrection", 1, p);
    v->depth_mode = GetPrivateProfileIntA("RenoDX.DLSS5", "NRDepthMode", 0, p);
    GetPrivateProfileStringA("RenoDX.DLSS5", "NRIntensity", "0.85", buf, sizeof(buf), p);
    v->intensity = static_cast<float>(atof(buf));
    GetPrivateProfileStringA("RenoDX.DLSS5", "NRLocalStructure", "0.99", buf, sizeof(buf), p);
    v->structure_ = static_cast<float>(atof(buf));
    GetPrivateProfileStringA("RenoDX.DLSS5", "NRLocalTone", "0.45", buf, sizeof(buf), p);
    v->tone = static_cast<float>(atof(buf));
    GetPrivateProfileStringA("RenoDX.DLSS5", "NRSkinStructure", "0.99", buf, sizeof(buf), p);
    v->skin = static_cast<float>(atof(buf));
    GetPrivateProfileStringA("RenoDX.DLSS5", "NRPaperWhiteScale", "1.0", buf, sizeof(buf), p);
    v->paper_white = static_cast<float>(atof(buf));
    GetPrivateProfileStringA("RenoDX.DLSS5", "NRTransferStrength", "1.0", buf, sizeof(buf), p);
    v->transfer_strength = static_cast<float>(atof(buf));
    GetPrivateProfileStringA("RenoDX.DLSS5", "NRColorStrength", "1.0", buf, sizeof(buf), p);
    v->color_strength = static_cast<float>(atof(buf));
    GetPrivateProfileStringA("RenoDX.DLSS5", "NRMVecScaleX", "1.0", buf, sizeof(buf), p);
    v->mvec_scale_x = static_cast<float>(atof(buf));
    GetPrivateProfileStringA("RenoDX.DLSS5", "NRMVecScaleY", "1.0", buf, sizeof(buf), p);
    v->mvec_scale_y = static_cast<float>(atof(buf));
}

static void WriteHostNR(const HostNR &v)
{
    char p[MAX_PATH], buf[64];
    HostIniPath(p);
    sprintf_s(buf, "%d", v.uplift);        WritePrivateProfileStringA("RenoDX.DLSS5", "NeuralUplift", buf, p);
    sprintf_s(buf, "%d", v.upscaling);     WritePrivateProfileStringA("RenoDX.DLSS5", "NREnableUpscaling", buf, p);
    sprintf_s(buf, "%d", v.preset);        WritePrivateProfileStringA("RenoDX.DLSS5", "NRPreset", buf, p);
    sprintf_s(buf, "%d", v.style);         WritePrivateProfileStringA("RenoDX.DLSS5", "NRStyle", buf, p);
    sprintf_s(buf, "%d", v.automask);      WritePrivateProfileStringA("RenoDX.DLSS5", "NRAutoMask", buf, p);
    sprintf_s(buf, "%d", v.uicorr);        WritePrivateProfileStringA("RenoDX.DLSS5", "NRUICorrection", buf, p);
    sprintf_s(buf, "%d", v.depth_mode);    WritePrivateProfileStringA("RenoDX.DLSS5", "NRDepthMode", buf, p);
    sprintf_s(buf, "%.6f", v.intensity);   WritePrivateProfileStringA("RenoDX.DLSS5", "NRIntensity", buf, p);
    sprintf_s(buf, "%.6f", v.structure_);  WritePrivateProfileStringA("RenoDX.DLSS5", "NRLocalStructure", buf, p);
    sprintf_s(buf, "%.6f", v.tone);        WritePrivateProfileStringA("RenoDX.DLSS5", "NRLocalTone", buf, p);
    sprintf_s(buf, "%.6f", v.skin);        WritePrivateProfileStringA("RenoDX.DLSS5", "NRSkinStructure", buf, p);
    sprintf_s(buf, "%.6f", v.paper_white); WritePrivateProfileStringA("RenoDX.DLSS5", "NRPaperWhiteScale", buf, p);
    sprintf_s(buf, "%.6f", v.transfer_strength); WritePrivateProfileStringA("RenoDX.DLSS5", "NRTransferStrength", buf, p);
    sprintf_s(buf, "%.6f", v.color_strength); WritePrivateProfileStringA("RenoDX.DLSS5", "NRColorStrength", buf, p);
    sprintf_s(buf, "%.6f", v.mvec_scale_x); WritePrivateProfileStringA("RenoDX.DLSS5", "NRMVecScaleX", buf, p);
    sprintf_s(buf, "%.6f", v.mvec_scale_y); WritePrivateProfileStringA("RenoDX.DLSS5", "NRMVecScaleY", buf, p);
}

// Cache of the host's settings, shown and edited on the ReShade overlay page (Add-ons
// tab -> DLSS 5 Feed). Loaded from the host's ini on first resolve so the panel always
// starts from what is actually active, never a stale default.
static HostNR g_host_nr;
static bool   g_host_nr_loaded;
static bool   g_host_nr_dirty;

static void HostClose();   // below

static void HostApplySettings()
{
    Log("[feed32] applying DLSS 5 host settings: uplift=%d upscaling=%d preset=%d style=%d intensity=%.2f structure=%.2f tone=%.2f skin=%.2f automask=%d uicorr=%d paper=%.2f transfer_strength=%.2f color=%.2f depth_mode=%d mvec=%.2f/%.2f",
        g_host_nr.uplift, g_host_nr.upscaling, g_host_nr.preset, g_host_nr.style, g_host_nr.intensity,
        g_host_nr.structure_, g_host_nr.tone, g_host_nr.skin, g_host_nr.automask, g_host_nr.uicorr,
        g_host_nr.paper_white, g_host_nr.transfer_strength, g_host_nr.color_strength,
        g_host_nr.depth_mode, g_host_nr.mvec_scale_x, g_host_nr.mvec_scale_y);

    // Order matters: the host's ReShade saves its ini ON EXIT and would clobber our
    // values -- close the host first, write after, respawn on the next frame.
    HostClose();
    WriteHostNR(g_host_nr);

    SafeRelease(g.fence_in);    // the new host creates new fences; reopen from its BuildAck
    SafeRelease(g.fence_out);
    g.built = false;
    g.disabled = false;
    g.consecutive_fails = 0;
    g_retry_at = 0;
    Warn("DLSS 5 settings applied -- restarting the host (~2 s)");
}

static void ApplyNrToggleToHost()
{
    if (!g_host_nr_loaded)
    {
        ReadHostNR(&g_host_nr);
        g_host_nr_loaded = true;
    }

    g_host_nr.uplift = g_nr_enabled ? 1 : 0;
    Warn("DLSS5 Neural Rendering %s (%s toggles; host restarts)",
         g_nr_enabled ? "ON" : "OFF", HotkeyName(g_cfg.hotkey_toggle));
    Log("[feed32] hotkey toggled NeuralUplift %s; applying to the host", g_nr_enabled ? "ON" : "OFF");
    HostApplySettings();
}

// ---------------------------------------------------------------------------
// Shared resources (created on the game's D3D11 device)
// ---------------------------------------------------------------------------

static void ReleaseShared()
{
    SafeRelease(g.output_srv);
    SafeRelease(g.color_stage_srv);
    SafeRelease(g.color_stage);
    for (int i = 0; i < FEED_SLOTS; ++i)
    {
        SafeRelease(g.tex_rtv[i]);
        SafeRelease(g.tex[i]);
        if (g.tex_handle[i] != nullptr) { CloseHandle(g.tex_handle[i]); g.tex_handle[i] = nullptr; }
    }
    g.built = false;
}

static void ReleaseD3D9Bridge()
{
    SafeRelease(g.stage9);
    SafeRelease(g.resolve9);
    SafeRelease(g.upload9);
    g.cpu_color_rgba.clear();
    g.cpu_output_rgba.clear();
    g.cpu_present_rgba.clear();
    SafeRelease(g.dev9);
    g.built = false;
}

static void ReleaseD3D12Bridge()
{
    SafeRelease(g.readback12);
    SafeRelease(g.upload12);
    SafeRelease(g.dev12);
    g.d3d12_fmt = DXGI_FORMAT_UNKNOWN;
    g.d3d12_footprint = {};
    g.d3d12_rows = 0;
    g.d3d12_row_bytes = 0;
    g.d3d12_total_bytes = 0;
    g.d3d12_capture_valid = false;
    g.cpu_color_rgba.clear();
    g.cpu_output_rgba.clear();
    g.cpu_present_rgba.clear();
    g.built = false;
}

static void ReleaseD3D9Surfaces()
{
    SafeRelease(g.stage9);
    SafeRelease(g.resolve9);
    SafeRelease(g.upload9);
    g.cpu_color_rgba.clear();
    g.cpu_output_rgba.clear();
    g.cpu_present_rgba.clear();
    g.built = false;
}

static void ResetBridgeForRebuild(const char *why)
{
    Log("[feed32] %s", why);
    HostClose();
    ReleaseShared();
    ReleaseD3D9Bridge();
    ReleaseD3D12Bridge();
    SafeRelease(g.fence_in);
    SafeRelease(g.fence_out);
    g.need_reset = true;
    g.disabled = false;
    g.consecutive_fails = 0;
    g_retry_at = GetTickCount64() + 1000;
}

static bool MakeShared(int slot, UINT w, UINT h, DXGI_FORMAT fmt, bool uav, bool rtv)
{
    D3D11_TEXTURE2D_DESC td = {};
    td.Width            = w;
    td.Height           = h;
    td.MipLevels        = 1;
    td.ArraySize        = 1;
    td.Format           = fmt;
    td.SampleDesc.Count = 1;
    td.Usage            = D3D11_USAGE_DEFAULT;
    td.BindFlags        = D3D11_BIND_SHADER_RESOURCE | (uav ? D3D11_BIND_UNORDERED_ACCESS : 0) |
                          (rtv ? D3D11_BIND_RENDER_TARGET : 0);
    td.MiscFlags        = D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_SHARED;
    HRESULT hr = g.dev->CreateTexture2D(&td, nullptr, &g.tex[slot]);
    if (FAILED(hr)) { Log("[feed32] tex %d CreateTexture2D failed 0x%08X", slot, hr); return false; }

    IDXGIResource1 *r = nullptr;
    hr = g.tex[slot]->QueryInterface(__uuidof(IDXGIResource1), reinterpret_cast<void **>(&r));
    if (SUCCEEDED(hr))
    {
        hr = r->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr,
                                   &g.tex_handle[slot]);
        r->Release();
    }
    if (FAILED(hr)) { Log("[feed32] tex %d CreateSharedHandle failed 0x%08X", slot, hr); return false; }
    if (rtv)
    {
        hr = g.dev->CreateRenderTargetView(g.tex[slot], nullptr, &g.tex_rtv[slot]);
        if (FAILED(hr)) { Log("[feed32] tex %d RTV failed 0x%08X", slot, hr); return false; }
    }
    return true;
}

static bool MakeColorStage(UINT w, UINT h, DXGI_FORMAT fmt)
{
    D3D11_TEXTURE2D_DESC td = {};
    td.Width            = w;
    td.Height           = h;
    td.MipLevels        = 1;
    td.ArraySize        = 1;
    td.Format           = fmt;
    td.SampleDesc.Count = 1;
    td.Usage            = D3D11_USAGE_DEFAULT;
    td.BindFlags        = D3D11_BIND_SHADER_RESOURCE;
    HRESULT hr = g.dev->CreateTexture2D(&td, nullptr, &g.color_stage);
    if (FAILED(hr)) { Log("[feed32] color stage CreateTexture2D failed 0x%08X", hr); return false; }
    hr = g.dev->CreateShaderResourceView(g.color_stage, nullptr, &g.color_stage_srv);
    if (FAILED(hr)) { Log("[feed32] color stage SRV failed 0x%08X", hr); return false; }
    return true;
}

static bool MakeBlitShaders()
{
    if (g.blit_vs != nullptr && g.blit_ps != nullptr && g.compare_ps != nullptr &&
        g.diff_ps != nullptr && g.diff_enhanced_ps != nullptr)
        return true;
    static const char kSrc[] =
        "Texture2D<float4> src : register(t0);\n"
        "Texture2D<float4> original_src : register(t1);\n"
        "SamplerState smp : register(s0);\n"
        "struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };\n"
        "VSOut vs(uint id : SV_VertexID) { VSOut o; float2 uv = float2((id << 1) & 2, id & 2);\n"
        "  o.uv = uv; o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1); return o; }\n"
        "float4 ps(VSOut i) : SV_Target { return float4(src.Sample(smp, i.uv).rgb, 1.0); }\n"
        "float4 ps_compare(VSOut i) : SV_Target {\n"
        "  float3 o = original_src.Sample(smp, i.uv).rgb;\n"
        "  float3 p = src.Sample(smp, i.uv).rgb;\n"
        "  float3 c = i.uv.x < 0.5 ? o : p;\n"
        "  if (abs(i.uv.x - 0.5) < 0.0018) c = float3(1.0, 1.0, 0.0);\n"
        "  return float4(c, 1.0);\n"
        "}\n"
        "float4 ps_diff(VSOut i) : SV_Target {\n"
        "  float3 o = original_src.Sample(smp, i.uv).rgb;\n"
        "  float3 p = src.Sample(smp, i.uv).rgb;\n"
        "  float3 d = saturate(abs(p - o) * 8.0);\n"
        "  float3 c = i.uv.x < 0.5 ? o : d;\n"
        "  if (abs(i.uv.x - 0.5) < 0.0018) c = float3(0.0, 1.0, 1.0);\n"
        "  return float4(c, 1.0);\n"
        "}\n"
        "float4 ps_diff_enhanced(VSOut i) : SV_Target {\n"
        "  float3 o = original_src.Sample(smp, i.uv).rgb;\n"
        "  float3 p = src.Sample(smp, i.uv).rgb;\n"
        "  float3 d = saturate(abs(p - o) * 8.0);\n"
        "  float3 c = i.uv.x < 0.5 ? d : p;\n"
        "  if (abs(i.uv.x - 0.5) < 0.0018) c = float3(1.0, 0.0, 1.0);\n"
        "  return float4(c, 1.0);\n"
        "}\n";
    HMODULE m = LoadLibraryW(L"d3dcompiler_47.dll");
    auto compile = m != nullptr ? reinterpret_cast<pD3DCompile>(GetProcAddress(m, "D3DCompile")) : nullptr;
    if (compile == nullptr) { Log("[feed32] d3dcompiler_47.dll unavailable"); return false; }
    ID3DBlob *vs = nullptr, *ps = nullptr, *cmp = nullptr, *diff = nullptr, *diff_enhanced = nullptr, *err = nullptr;
    HRESULT hr = compile(kSrc, sizeof(kSrc) - 1, "feedblit", nullptr, nullptr, "vs", "vs_4_0", 0, 0, &vs, &err);
    if (FAILED(hr)) { Log("[feed32] blit VS compile failed 0x%08X", hr); SafeRelease(err); return false; }
    SafeRelease(err);
    hr = compile(kSrc, sizeof(kSrc) - 1, "feedblit", nullptr, nullptr, "ps", "ps_4_0", 0, 0, &ps, &err);
    if (FAILED(hr)) { Log("[feed32] blit PS compile failed 0x%08X", hr); SafeRelease(err); SafeRelease(vs); return false; }
    SafeRelease(err);
    hr = compile(kSrc, sizeof(kSrc) - 1, "feedblit", nullptr, nullptr, "ps_compare", "ps_4_0", 0, 0, &cmp, &err);
    if (FAILED(hr)) { Log("[feed32] compare PS compile failed 0x%08X", hr); SafeRelease(err); SafeRelease(vs); SafeRelease(ps); return false; }
    SafeRelease(err);
    hr = compile(kSrc, sizeof(kSrc) - 1, "feedblit", nullptr, nullptr, "ps_diff", "ps_4_0", 0, 0, &diff, &err);
    if (FAILED(hr)) { Log("[feed32] diff PS compile failed 0x%08X", hr); SafeRelease(err); SafeRelease(vs); SafeRelease(ps); SafeRelease(cmp); return false; }
    SafeRelease(err);
    hr = compile(kSrc, sizeof(kSrc) - 1, "feedblit", nullptr, nullptr, "ps_diff_enhanced", "ps_4_0", 0, 0, &diff_enhanced, &err);
    if (FAILED(hr)) { Log("[feed32] diff/enhanced PS compile failed 0x%08X", hr); SafeRelease(err); SafeRelease(vs); SafeRelease(ps); SafeRelease(cmp); SafeRelease(diff); return false; }
    SafeRelease(err);
    hr = g.dev->CreateVertexShader(vs->GetBufferPointer(), vs->GetBufferSize(), nullptr, &g.blit_vs);
    if (SUCCEEDED(hr)) hr = g.dev->CreatePixelShader(ps->GetBufferPointer(), ps->GetBufferSize(), nullptr, &g.blit_ps);
    if (SUCCEEDED(hr)) hr = g.dev->CreatePixelShader(cmp->GetBufferPointer(), cmp->GetBufferSize(), nullptr, &g.compare_ps);
    if (SUCCEEDED(hr)) hr = g.dev->CreatePixelShader(diff->GetBufferPointer(), diff->GetBufferSize(), nullptr, &g.diff_ps);
    if (SUCCEEDED(hr)) hr = g.dev->CreatePixelShader(diff_enhanced->GetBufferPointer(), diff_enhanced->GetBufferSize(), nullptr, &g.diff_enhanced_ps);
    vs->Release();
    ps->Release();
    cmp->Release();
    diff->Release();
    diff_enhanced->Release();
    if (FAILED(hr)) { Log("[feed32] blit shader creation failed 0x%08X", hr); return false; }
    D3D11_SAMPLER_DESC sd = {};
    sd.Filter   = D3D11_FILTER_MIN_MAG_MIP_POINT;
    sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sd.MaxLOD   = D3D11_FLOAT32_MAX;
    return SUCCEEDED(g.dev->CreateSamplerState(&sd, &g.blit_sampler));
}

static bool BuildShared(UINT w, UINT h, DXGI_FORMAT bb_fmt, bool has_bound_mask)
{
    Breadcrumb("building the shared textures");
    ReleaseShared();

    float scale = (g_cfg.mode == 2) ? g_cfg.render_scale : 1.0f;
    if (scale < 0.33f) scale = 0.33f;
    if (scale > 1.0f) scale = 1.0f;
    UINT iw = static_cast<UINT>(w * scale + 0.5f);
    UINT ih = static_cast<UINT>(h * scale + 0.5f);
    if (iw < 64u) iw = 64u;
    if (ih < 64u) ih = 64u;
    iw &= ~1u;
    ih &= ~1u;

    g.width      = w;
    g.height     = h;
    g.input_width  = iw;
    g.input_height = ih;
    g.bb_fmt     = bb_fmt;
    g.color_fmt  = TypedColorFormat(bb_fmt);
    // Transport test copies Color->Output host-side with CopyResource: same format then.
    g.output_fmt = g_cfg.mode == 1 ? g.color_fmt : OutputFormatFor(g.color_fmt);
    if (g.color_fmt == DXGI_FORMAT_UNKNOWN)
    { FeedDisable("unsupported backbuffer format"); return false; }
    const bool hdr      = g_cfg.hdr >= 0 ? g_cfg.hdr != 0 : IsHdrFormat(g.color_fmt);
    const bool inverted = g_cfg.depth_inverted >= 0 ? g_cfg.depth_inverted != 0 : g.depth_reversed;

    const bool color_needs_rtv = iw != w || ih != h;
    if (!MakeShared(FEED_COLOR, iw, ih, g.color_fmt, false, color_needs_rtv) ||
        !MakeShared(FEED_OUTPUT, w, h, g.output_fmt, true, false) ||
        !MakeShared(FEED_DEPTH, iw, ih, DXGI_FORMAT_R32_FLOAT, false, true) ||
        !MakeShared(FEED_MV, iw, ih, DXGI_FORMAT_R16G16_FLOAT, false, true) ||
        !MakeShared(FEED_MASK, iw, ih, DXGI_FORMAT_R8_UNORM, false, true))
    { ReleaseShared(); return false; }

    D3D11_SHADER_RESOURCE_VIEW_DESC sv = {};
    sv.Format              = g.output_fmt;
    sv.ViewDimension       = D3D11_SRV_DIMENSION_TEXTURE2D;
    sv.Texture2D.MipLevels = 1;
    if (FAILED(g.dev->CreateShaderResourceView(g.tex[FEED_OUTPUT], &sv, &g.output_srv)))
    { Log("[feed32] output SRV failed"); ReleaseShared(); return false; }
    if (!MakeBlitShaders()) { ReleaseShared(); return false; }
    if (!MakeColorStage(w, h, g.color_fmt))
    { ReleaseShared(); return false; }

    if (!EnsureHost()) return false;

    FeedBuild b = {};
    b.output_width   = w;
    b.output_height  = h;
    b.width          = iw;
    b.height         = ih;
    b.color_fmt      = g.color_fmt;
    b.output_fmt     = g.output_fmt;
    b.hdr            = hdr ? 1 : 0;
    b.depth_inverted = inverted ? 1 : 0;
    b.flags_override = g_cfg.flags;
    b.transport      = g_cfg.mode == 1 ? 1 : 0;
    b.has_mask       = has_bound_mask ? 1 : 0;
    b.mv_scale_x     = g_cfg.mv_scale_x;
    b.mv_scale_y     = g_cfg.mv_scale_y;
    for (int i = 0; i < FEED_SLOTS; ++i)
        b.tex[i] = reinterpret_cast<uintptr_t>(g.tex_handle[i]);

    Breadcrumb("asking the host to build");
    BYTE tag = 'B';
    FeedBuildAck ack = {};
    if (!PipeWrite(&tag, 1) || !PipeWrite(&b, sizeof(b)) || !PipeRead(&ack, sizeof(ack)))
    { HostLost("build exchange failed"); return false; }
    if (!ack.ok)
    {
        Log("[feed32] host build failed (ngx 0x%08X)", ack.ngx_result);
        return false;
    }

    if (g.fence_in == nullptr || g.fence_out == nullptr)
    {
        ID3D11Device5 *dev5 = nullptr;
        if (FAILED(g.dev->QueryInterface(__uuidof(ID3D11Device5), reinterpret_cast<void **>(&dev5))) || dev5 == nullptr)
        { FeedDisable("ID3D11Device5 unavailable (Windows 10 1703+ required)"); return false; }
        HRESULT h1 = dev5->OpenSharedFence(reinterpret_cast<HANDLE>(static_cast<uintptr_t>(ack.fence_in)),
                                           __uuidof(ID3D11Fence), reinterpret_cast<void **>(&g.fence_in));
        HRESULT h2 = dev5->OpenSharedFence(reinterpret_cast<HANDLE>(static_cast<uintptr_t>(ack.fence_out)),
                                           __uuidof(ID3D11Fence), reinterpret_cast<void **>(&g.fence_out));
        dev5->Release();
        if (FAILED(h1) || FAILED(h2)) { Log("[feed32] OpenSharedFence failed 0x%08X/0x%08X", h1, h2); return false; }
    }

    Log("[feed32] shared set ready: input %ux%u -> output %ux%u color fmt=%u output fmt=%u (host ngx 0x%08X, %s)",
        iw, ih, w, h, g.color_fmt, g.output_fmt, ack.ngx_result, g_cfg.mode == 1 ? "transport" : "DLSS");
    g.built      = true;
    g.bound_mask_available = has_bound_mask;
    g.need_reset = true;
    g.consecutive_fails = 0;
    return true;
}

// ---------------------------------------------------------------------------
// Copy-back blit (verbatim from the 64-bit add-on)
// ---------------------------------------------------------------------------

static void DrawSrvToRtv(ID3D11DeviceContext *ctx, ID3D11ShaderResourceView *srv,
                         ID3D11RenderTargetView *rtv, UINT w, UINT h,
                         ID3D11ShaderResourceView *compare_srv = nullptr, int compare_mode = 0)
{
    ID3D11RenderTargetView   *old_rtv = nullptr;
    ID3D11DepthStencilView   *old_dsv = nullptr;
    ID3D11VertexShader       *old_vs  = nullptr;
    ID3D11PixelShader        *old_ps  = nullptr;
    ID3D11ShaderResourceView *old_srvs[2] = {};
    ID3D11SamplerState       *old_smp = nullptr;
    ID3D11InputLayout        *old_il  = nullptr;
    ID3D11BlendState         *old_bs  = nullptr; FLOAT old_bf[4]; UINT old_mask = 0;
    ID3D11DepthStencilState  *old_ds  = nullptr; UINT old_sref = 0;
    ID3D11RasterizerState    *old_rs  = nullptr;
    D3D11_PRIMITIVE_TOPOLOGY  old_topo = D3D11_PRIMITIVE_TOPOLOGY_UNDEFINED;
    UINT nvp = 1; D3D11_VIEWPORT old_vp = {};
    ctx->OMGetRenderTargets(1, &old_rtv, &old_dsv);
    ctx->VSGetShader(&old_vs, nullptr, nullptr);
    ctx->PSGetShader(&old_ps, nullptr, nullptr);
    ctx->PSGetShaderResources(0, 2, old_srvs);
    ctx->PSGetSamplers(0, 1, &old_smp);
    ctx->IAGetInputLayout(&old_il);
    ctx->IAGetPrimitiveTopology(&old_topo);
    ctx->OMGetBlendState(&old_bs, old_bf, &old_mask);
    ctx->OMGetDepthStencilState(&old_ds, &old_sref);
    ctx->RSGetState(&old_rs);
    ctx->RSGetViewports(&nvp, &old_vp);

    D3D11_VIEWPORT vp = {};
    vp.Width    = static_cast<float>(w);
    vp.Height   = static_cast<float>(h);
    vp.MaxDepth = 1.0f;
    const bool compare = compare_mode >= 2 && compare_srv != nullptr;
    ID3D11RenderTargetView *rtvs[] = { rtv };
    ID3D11ShaderResourceView *srvs[] = { srv, compare ? compare_srv : nullptr };
    ID3D11SamplerState *smps[] = { g.blit_sampler };
    ctx->OMSetRenderTargets(1, rtvs, nullptr);
    ctx->OMSetBlendState(nullptr, nullptr, 0xFFFFFFFF);
    ctx->OMSetDepthStencilState(nullptr, 0);
    ctx->RSSetState(nullptr);
    ctx->RSSetViewports(1, &vp);
    ctx->IASetInputLayout(nullptr);
    ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    ctx->VSSetShader(g.blit_vs, nullptr, 0);
    ID3D11PixelShader *ps = g.blit_ps;
    if (compare)
        ps = compare_mode == 3 ? g.diff_ps : (compare_mode == 4 ? g.diff_enhanced_ps : g.compare_ps);
    ctx->PSSetShader(ps, nullptr, 0);
    ctx->PSSetShaderResources(0, 2, srvs);
    ctx->PSSetSamplers(0, 1, smps);
    ctx->Draw(3, 0);

    ID3D11ShaderResourceView *no_srvs[2] = {};
    ctx->PSSetShaderResources(0, 2, no_srvs);
    ctx->OMSetRenderTargets(1, &old_rtv, old_dsv);
    ctx->VSSetShader(old_vs, nullptr, 0);
    ctx->PSSetShader(old_ps, nullptr, 0);
    ctx->PSSetShaderResources(0, 2, old_srvs);
    ctx->PSSetSamplers(0, 1, &old_smp);
    ctx->IASetInputLayout(old_il);
    ctx->IASetPrimitiveTopology(old_topo);
    ctx->OMSetBlendState(old_bs, old_bf, old_mask);
    ctx->OMSetDepthStencilState(old_ds, old_sref);
    ctx->RSSetState(old_rs);
    if (nvp) ctx->RSSetViewports(1, &old_vp);
    SafeRelease(old_rtv); SafeRelease(old_dsv); SafeRelease(old_vs); SafeRelease(old_ps);
    SafeRelease(old_srvs[0]); SafeRelease(old_srvs[1]);
    SafeRelease(old_smp); SafeRelease(old_il); SafeRelease(old_bs); SafeRelease(old_ds); SafeRelease(old_rs);
}

static void BlitOutputToBackbuffer(ID3D11DeviceContext *ctx, ID3D11RenderTargetView *rtv)
{
    if (g_cfg.compare_mode == 0 && g.color_stage_srv != nullptr)
        DrawSrvToRtv(ctx, g.color_stage_srv, rtv, g.width, g.height);
    else if (g_cfg.compare_mode >= 2 && g.color_stage_srv != nullptr)
        DrawSrvToRtv(ctx, g.output_srv, rtv, g.width, g.height, g.color_stage_srv, g_cfg.compare_mode);
    else
        DrawSrvToRtv(ctx, g.output_srv, rtv, g.width, g.height);
}

static bool ScreenshotPaths(wchar_t *normal, size_t normal_count, wchar_t *dlss, size_t dlss_count)
{
    wchar_t dir[MAX_PATH];
    GetModuleFileNameW(g_self, dir, MAX_PATH);
    if (wchar_t *s = wcsrchr(dir, L'\\'))
        wcscpy_s(s + 1, MAX_PATH - (s + 1 - dir), L"screenshots");
    CreateDirectoryW(dir, nullptr);

    SYSTEMTIME st;
    GetLocalTime(&st);
    swprintf_s(normal, normal_count, L"%s\\dlss5-feed-%04u%02u%02u-%02u%02u%02u-%03u-normal.bmp",
               dir, st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
    swprintf_s(dlss, dlss_count, L"%s\\dlss5-feed-%04u%02u%02u-%02u%02u%02u-%03u-dlss.bmp",
               dir, st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
    return true;
}

static bool SaveTextureBmp(ID3D11Device *dev, ID3D11DeviceContext *ctx, ID3D11Texture2D *src, const wchar_t *path)
{
    if (dev == nullptr || ctx == nullptr || src == nullptr || path == nullptr) return false;

    D3D11_TEXTURE2D_DESC desc = {};
    src->GetDesc(&desc);

    const bool rgba = desc.Format == DXGI_FORMAT_R8G8B8A8_UNORM || desc.Format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
    const bool bgra = desc.Format == DXGI_FORMAT_B8G8R8A8_UNORM || desc.Format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
    if (!rgba && !bgra)
    {
        Log("[feed32] screenshot skipped: unsupported texture format %u", desc.Format);
        return false;
    }

    D3D11_TEXTURE2D_DESC sd = desc;
    sd.MipLevels = 1;
    sd.ArraySize = 1;
    sd.SampleDesc.Count = 1;
    sd.SampleDesc.Quality = 0;
    sd.Usage = D3D11_USAGE_STAGING;
    sd.BindFlags = 0;
    sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    sd.MiscFlags = 0;

    ID3D11Texture2D *stage = nullptr;
    HRESULT hr = dev->CreateTexture2D(&sd, nullptr, &stage);
    if (FAILED(hr) || stage == nullptr)
    {
        Log("[feed32] screenshot staging texture failed 0x%08X", hr);
        return false;
    }

    ctx->CopyResource(stage, src);
    D3D11_MAPPED_SUBRESOURCE map = {};
    hr = ctx->Map(stage, 0, D3D11_MAP_READ, 0, &map);
    if (FAILED(hr))
    {
        Log("[feed32] screenshot map failed 0x%08X", hr);
        stage->Release();
        return false;
    }

    const uint32_t row_bytes = desc.Width * 4u;
    std::vector<BYTE> pixels(static_cast<size_t>(row_bytes) * desc.Height);
    for (UINT y = 0; y < desc.Height; ++y)
    {
        const BYTE *src_row = static_cast<const BYTE *>(map.pData) + static_cast<size_t>(map.RowPitch) * y;
        BYTE *dst_row = pixels.data() + static_cast<size_t>(row_bytes) * y;
        for (UINT x = 0; x < desc.Width; ++x)
        {
            const BYTE *s = src_row + x * 4u;
            BYTE *d = dst_row + x * 4u;
            if (rgba)
            {
                d[0] = s[2];
                d[1] = s[1];
                d[2] = s[0];
                d[3] = s[3];
            }
            else
            {
                d[0] = s[0];
                d[1] = s[1];
                d[2] = s[2];
                d[3] = s[3];
            }
        }
    }

    ctx->Unmap(stage, 0);
    stage->Release();

    FILE *f = nullptr;
    if (_wfopen_s(&f, path, L"wb") != 0 || f == nullptr)
    {
        Log("[feed32] screenshot open failed");
        return false;
    }

    BITMAPFILEHEADER bfh = {};
    BITMAPINFOHEADER bih = {};
    bfh.bfType = 0x4D42;
    bfh.bfOffBits = sizeof(BITMAPFILEHEADER) + sizeof(BITMAPINFOHEADER);
    bfh.bfSize = bfh.bfOffBits + static_cast<DWORD>(pixels.size());
    bih.biSize = sizeof(BITMAPINFOHEADER);
    bih.biWidth = static_cast<LONG>(desc.Width);
    bih.biHeight = -static_cast<LONG>(desc.Height);
    bih.biPlanes = 1;
    bih.biBitCount = 32;
    bih.biCompression = BI_RGB;
    bih.biSizeImage = static_cast<DWORD>(pixels.size());

    const bool ok = fwrite(&bfh, 1, sizeof(bfh), f) == sizeof(bfh) &&
                    fwrite(&bih, 1, sizeof(bih), f) == sizeof(bih) &&
                    fwrite(pixels.data(), 1, pixels.size(), f) == pixels.size();
    fclose(f);
    if (!ok)
    {
        Log("[feed32] screenshot write failed");
        return false;
    }
    return true;
}

static void SaveDualScreenshotUnsafe(ID3D11DeviceContext *ctx)
{
    if (!g.dual_shot_pending) return;
    if (g.dual_shot_delay > 0)
    {
        --g.dual_shot_delay;
        return;
    }
    g.dual_shot_pending = false;

    if (g.dev == nullptr || ctx == nullptr || g.color_stage == nullptr || g.tex[FEED_OUTPUT] == nullptr)
    {
        Log("[feed32] PrintScreen skipped: normal/DLSS textures are not ready");
        return;
    }

    wchar_t normal[MAX_PATH], dlss[MAX_PATH];
    ScreenshotPaths(normal, sizeof(normal) / sizeof(normal[0]), dlss, sizeof(dlss) / sizeof(dlss[0]));
    Log("[feed32] PrintScreen capture begin");
    Log("[feed32] PrintScreen saving normal texture");
    const bool normal_ok = SaveTextureBmp(g.dev, ctx, g.color_stage, normal);
    Log("[feed32] PrintScreen saving DLSS texture");
    const bool dlss_ok = SaveTextureBmp(g.dev, ctx, g.tex[FEED_OUTPUT], dlss);

    char n8[MAX_PATH], d8[MAX_PATH];
    WideCharToMultiByte(CP_UTF8, 0, normal, -1, n8, sizeof(n8), nullptr, nullptr);
    WideCharToMultiByte(CP_UTF8, 0, dlss, -1, d8, sizeof(d8), nullptr, nullptr);
    if (normal_ok && dlss_ok)
        Warn("saved normal+DLSS screenshots");
    Log("[feed32] PrintScreen saved normal=%d '%s' dlss=%d '%s'", normal_ok ? 1 : 0, n8, dlss_ok ? 1 : 0, d8);
}

static int ScreenshotExceptionFilter(unsigned int code)
{
    Log("[feed32] PrintScreen capture faulted with exception 0x%08X; disabling this capture", code);
    g.dual_shot_pending = false;
    g.dual_shot_delay = 0;
    return EXCEPTION_EXECUTE_HANDLER;
}

static void SaveDualScreenshot(ID3D11DeviceContext *ctx)
{
    __try
    {
        SaveDualScreenshotUnsafe(ctx);
    }
    __except (ScreenshotExceptionFilter(GetExceptionCode()))
    {
    }
}

static bool PrepareDlssInputs(ID3D11DeviceContext *ctx, ID3D11Resource *color,
                              ID3D11ShaderResourceView *mv_srv, ID3D11ShaderResourceView *depth_srv,
                              ID3D11ShaderResourceView *mask_srv)
{
    if (color == nullptr) return false;

    const bool zero_guides = mv_srv == nullptr || depth_srv == nullptr;
    if (zero_guides && !g.using_zero_guides)
    {
        g.using_zero_guides = true;
        Log("[feed32] using zero guide fallback: color is fed to DLSS, motion vectors are zero, depth is flat");
    }
    else if (!zero_guides && g.using_zero_guides)
    {
        g.using_zero_guides = false;
        Log("[feed32] ReShade guide texture bindings recovered");
    }

    if (g.input_width == g.width && g.input_height == g.height)
    {
        ID3D11Resource *mv = nullptr, *depth = nullptr, *mask = nullptr;
        if (mv_srv != nullptr) mv_srv->GetResource(&mv);
        if (depth_srv != nullptr) depth_srv->GetResource(&depth);
        if (mask_srv != nullptr) mask_srv->GetResource(&mask);
        if (!zero_guides && (mv == nullptr || depth == nullptr)) { SafeRelease(mv); SafeRelease(depth); SafeRelease(mask); return false; }
        if (g.color_stage != nullptr)
            ctx->CopyResource(g.color_stage, color);
        ctx->CopyResource(g.tex[FEED_COLOR], color);
        if (depth != nullptr)
            ctx->CopyResource(g.tex[FEED_DEPTH], depth);
        else if (g.tex_rtv[FEED_DEPTH] != nullptr)
        {
            const FLOAT clear_depth[4] = { 1.0f, 0.0f, 0.0f, 0.0f };
            ctx->ClearRenderTargetView(g.tex_rtv[FEED_DEPTH], clear_depth);
        }
        if (mv != nullptr)
            ctx->CopyResource(g.tex[FEED_MV], mv);
        else if (g.tex_rtv[FEED_MV] != nullptr)
        {
            const FLOAT clear_mv[4] = {};
            ctx->ClearRenderTargetView(g.tex_rtv[FEED_MV], clear_mv);
        }
        if (mask != nullptr)
            ctx->CopyResource(g.tex[FEED_MASK], mask);
        else if (g.tex_rtv[FEED_MASK] != nullptr)
        {
            const FLOAT clear[4] = {};
            ctx->ClearRenderTargetView(g.tex_rtv[FEED_MASK], clear);
        }
        SafeRelease(mv);
        SafeRelease(depth);
        SafeRelease(mask);
        return true;
    }

    if (g.color_stage == nullptr || g.color_stage_srv == nullptr ||
        g.tex_rtv[FEED_COLOR] == nullptr || g.tex_rtv[FEED_DEPTH] == nullptr ||
        g.tex_rtv[FEED_MV] == nullptr || g.tex_rtv[FEED_MASK] == nullptr)
        return false;

    ctx->CopyResource(g.color_stage, color);
    DrawSrvToRtv(ctx, g.color_stage_srv, g.tex_rtv[FEED_COLOR], g.input_width, g.input_height);
    if (depth_srv != nullptr)
        DrawSrvToRtv(ctx, depth_srv, g.tex_rtv[FEED_DEPTH], g.input_width, g.input_height);
    else
    {
        const FLOAT clear_depth[4] = { 1.0f, 0.0f, 0.0f, 0.0f };
        ctx->ClearRenderTargetView(g.tex_rtv[FEED_DEPTH], clear_depth);
    }
    if (mv_srv != nullptr)
        DrawSrvToRtv(ctx, mv_srv, g.tex_rtv[FEED_MV], g.input_width, g.input_height);
    else
    {
        const FLOAT clear_mv[4] = {};
        ctx->ClearRenderTargetView(g.tex_rtv[FEED_MV], clear_mv);
    }
    if (mask_srv != nullptr)
        DrawSrvToRtv(ctx, mask_srv, g.tex_rtv[FEED_MASK], g.input_width, g.input_height);
    else
    {
        const FLOAT clear[4] = {};
        ctx->ClearRenderTargetView(g.tex_rtv[FEED_MASK], clear);
    }
    return true;
}

// ---------------------------------------------------------------------------
// Native D3D9 CPU bridge
// ---------------------------------------------------------------------------

static bool IsD3D9Argb8(D3DFORMAT fmt)
{
    return fmt == D3DFMT_A8R8G8B8 || fmt == D3DFMT_X8R8G8B8;
}

static IDirect3DSurface9 *TryD3D9Surface(void *p)
{
    if (p == nullptr) return nullptr;
    IDirect3DSurface9 *surface = nullptr;
    __try
    {
        reinterpret_cast<IUnknown *>(p)->QueryInterface(__uuidof(IDirect3DSurface9),
                                                        reinterpret_cast<void **>(&surface));
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        surface = nullptr;
    }
    return surface;
}

static IDirect3DSurface9 *GetD3D9BackbufferSurface(reshade::api::device *dev_api,
                                                   reshade::api::resource_view rtv)
{
    Breadcrumb("native D3D9 surface lookup");
    IDirect3DSurface9 *surface = TryD3D9Surface(reinterpret_cast<void *>(rtv.handle));
    if (surface != nullptr)
        return surface;

    reshade::api::resource res = dev_api->get_resource_from_view(rtv);
    return TryD3D9Surface(reinterpret_cast<void *>(res.handle));
}

static bool BuildCpuBridge(UINT w, UINT h, D3DFORMAT d3d_fmt)
{
    Breadcrumb("building native D3D9 CPU bridge");
    ReleaseShared();
    ReleaseD3D9Surfaces();

    if (!IsD3D9Argb8(d3d_fmt))
    {
        Log("[feed32] native D3D9 unsupported backbuffer format %u", static_cast<unsigned>(d3d_fmt));
        FeedDisable("native D3D9 backbuffer format is not supported yet");
        return false;
    }
    if (!EnsureHost()) return false;

    g.width = w;
    g.height = h;
    g.input_width = w;
    g.input_height = h;
    g.bb_fmt = DXGI_FORMAT_R8G8B8A8_UNORM;
    g.color_fmt = DXGI_FORMAT_R8G8B8A8_UNORM;
    g.output_fmt = DXGI_FORMAT_R8G8B8A8_UNORM;
    g.d3d9_fmt = d3d_fmt;
    g.cpu_color_rgba.assign(static_cast<size_t>(w) * h * 4u, 0);
    g.cpu_output_rgba.assign(static_cast<size_t>(w) * h * 4u, 0);
    g.cpu_present_rgba.assign(static_cast<size_t>(w) * h * 4u, 0);

    const bool hdr = false;
    const bool inverted = g_cfg.depth_inverted >= 0 ? g_cfg.depth_inverted != 0 : true;

    FeedBuild b = {};
    b.output_width = w;
    b.output_height = h;
    b.width = w;
    b.height = h;
    b.color_fmt = DXGI_FORMAT_R8G8B8A8_UNORM;
    b.output_fmt = DXGI_FORMAT_R8G8B8A8_UNORM;
    b.hdr = hdr ? 1 : 0;
    b.depth_inverted = inverted ? 1 : 0;
    b.flags_override = g_cfg.flags;
    b.transport = g_cfg.mode == 1 ? 1 : 0;
    b.has_mask = 0;
    b.cpu_bridge = 1;
    b.mv_scale_x = g_cfg.mv_scale_x;
    b.mv_scale_y = g_cfg.mv_scale_y;

    BYTE tag = 'B';
    FeedBuildAck ack = {};
    if (!PipeWrite(&tag, 1) || !PipeWrite(&b, sizeof(b)) || !PipeRead(&ack, sizeof(ack)))
    { HostLost("native D3D9 build exchange failed"); return false; }
    if (!ack.ok)
    {
        Log("[feed32] native D3D9 host build failed (ngx 0x%08X)", ack.ngx_result);
        return false;
    }

    Log("[feed32] native D3D9 CPU bridge ready: %ux%u fmt=%u (host ngx 0x%08X, %s)",
        w, h, static_cast<unsigned>(d3d_fmt), ack.ngx_result, g_cfg.mode == 1 ? "transport" : "DLSS");
    g.built = true;
    g.need_reset = true;
    g.consecutive_fails = 0;
    return true;
}

static bool EnsureD3D9Surfaces(IDirect3DDevice9 *dev, const D3DSURFACE_DESC &desc)
{
    if (g.stage9 != nullptr && g.upload9 != nullptr && g.width == desc.Width &&
        g.height == desc.Height && g.d3d9_fmt == desc.Format)
        return true;

    SafeRelease(g.stage9);
    SafeRelease(g.resolve9);
    SafeRelease(g.upload9);

    HRESULT hr = dev->CreateOffscreenPlainSurface(desc.Width, desc.Height, desc.Format,
                                                  D3DPOOL_SYSTEMMEM, &g.stage9, nullptr);
    if (FAILED(hr) || g.stage9 == nullptr)
    {
        Log("[feed32] native D3D9 staging surface failed 0x%08X", hr);
        return false;
    }
    hr = dev->CreateOffscreenPlainSurface(desc.Width, desc.Height, desc.Format,
                                          D3DPOOL_SYSTEMMEM, &g.upload9, nullptr);
    if (FAILED(hr) || g.upload9 == nullptr)
    {
        Log("[feed32] native D3D9 upload surface failed 0x%08X", hr);
        return false;
    }
    if (desc.MultiSampleType != D3DMULTISAMPLE_NONE)
    {
        hr = dev->CreateRenderTarget(desc.Width, desc.Height, desc.Format, D3DMULTISAMPLE_NONE,
                                     0, FALSE, &g.resolve9, nullptr);
        if (FAILED(hr) || g.resolve9 == nullptr)
        {
            Log("[feed32] native D3D9 resolve surface failed 0x%08X", hr);
            return false;
        }
    }
    return true;
}

static bool CaptureD3D9Rgba(IDirect3DDevice9 *dev, IDirect3DSurface9 *back,
                            const D3DSURFACE_DESC &desc)
{
    Breadcrumb("native D3D9 capture setup");
    if (!EnsureD3D9Surfaces(dev, desc)) return false;

    IDirect3DSurface9 *read_src = back;
    if (g.resolve9 != nullptr)
    {
        Breadcrumb("native D3D9 MSAA resolve");
        if (g.frames_done < 3) Log("[feed32] native D3D9 capture: resolving MSAA backbuffer");
        HRESULT hr = dev->StretchRect(back, nullptr, g.resolve9, nullptr, D3DTEXF_NONE);
        if (FAILED(hr))
        {
            Log("[feed32] native D3D9 MSAA resolve failed 0x%08X", hr);
            return false;
        }
        read_src = g.resolve9;
    }

    Breadcrumb("native D3D9 GetRenderTargetData");
    if (g.frames_done < 3) Log("[feed32] native D3D9 capture: GetRenderTargetData begin");
    HRESULT hr = dev->GetRenderTargetData(read_src, g.stage9);
    if (FAILED(hr))
    {
        Log("[feed32] native D3D9 GetRenderTargetData failed 0x%08X", hr);
        return false;
    }

    Breadcrumb("native D3D9 staging lock");
    if (g.frames_done < 3) Log("[feed32] native D3D9 capture: lock/convert begin");
    D3DLOCKED_RECT lr = {};
    hr = g.stage9->LockRect(&lr, nullptr, D3DLOCK_READONLY);
    if (FAILED(hr))
    {
        Log("[feed32] native D3D9 staging lock failed 0x%08X", hr);
        return false;
    }

    g.cpu_color_rgba.resize(static_cast<size_t>(desc.Width) * desc.Height * 4u);
    for (UINT y = 0; y < desc.Height; ++y)
    {
        const BYTE *src = static_cast<const BYTE *>(lr.pBits) + static_cast<size_t>(lr.Pitch) * y;
        BYTE *dst = g.cpu_color_rgba.data() + static_cast<size_t>(desc.Width) * y * 4u;
        for (UINT x = 0; x < desc.Width; ++x)
        {
            const BYTE *p = src + x * 4u; // D3D9 A8R8G8B8/X8R8G8B8 memory is BGRA/BGRX.
            BYTE *q = dst + x * 4u;
            q[0] = p[2];
            q[1] = p[1];
            q[2] = p[0];
            q[3] = 0xFF;
        }
    }
    Breadcrumb("native D3D9 staging unlock");
    g.stage9->UnlockRect();
    if (g.frames_done < 3) Log("[feed32] native D3D9 capture: complete");
    return true;
}

static void ComposeCpuPresent()
{
    const size_t count = g.cpu_color_rgba.size();
    if (g.cpu_output_rgba.size() != count)
        g.cpu_present_rgba = g.cpu_color_rgba;
    else if (g_cfg.compare_mode == 0)
        g.cpu_present_rgba = g.cpu_color_rgba;
    else if (g_cfg.compare_mode == 1)
        g.cpu_present_rgba = g.cpu_output_rgba;
    else
    {
        g.cpu_present_rgba.resize(count);
        const UINT split = g.width / 2;
        for (UINT y = 0; y < g.height; ++y)
        {
            for (UINT x = 0; x < g.width; ++x)
            {
                const size_t i = (static_cast<size_t>(y) * g.width + x) * 4u;
                const BYTE *o = g.cpu_color_rgba.data() + i;
                const BYTE *p = g.cpu_output_rgba.data() + i;
                BYTE *d = g.cpu_present_rgba.data() + i;
                if (g_cfg.compare_mode == 2)
                {
                    const BYTE *s = x < split ? o : p;
                    d[0] = s[0]; d[1] = s[1]; d[2] = s[2]; d[3] = 0xFF;
                }
                else
                {
                    BYTE diff[4] = {
                        static_cast<BYTE>(std::min(255, abs(int(p[0]) - int(o[0])) * 8)),
                        static_cast<BYTE>(std::min(255, abs(int(p[1]) - int(o[1])) * 8)),
                        static_cast<BYTE>(std::min(255, abs(int(p[2]) - int(o[2])) * 8)),
                        0xFF
                    };
                    const BYTE *s = nullptr;
                    if (g_cfg.compare_mode == 3)
                        s = x < split ? o : diff;
                    else
                        s = x < split ? diff : p;
                    d[0] = s[0]; d[1] = s[1]; d[2] = s[2]; d[3] = 0xFF;
                }
                if (x == split)
                {
                    d[0] = 255;
                    d[1] = g_cfg.compare_mode == 3 ? 255 : 0;
                    d[2] = g_cfg.compare_mode == 4 ? 255 : 0;
                }
            }
        }
    }
}

static void LogCurrentCpuDiff(UINT64 frame)
{
    const size_t count = std::min(g.cpu_color_rgba.size(), g.cpu_output_rgba.size());
    if (count == 0) return;
    UINT64 changed = 0;
    UINT64 abs_sum = 0;
    BYTE max_diff = 0;
    for (size_t i = 0; i < count; ++i)
    {
        const int d = abs(int(g.cpu_output_rgba[i]) - int(g.cpu_color_rgba[i]));
        if (d != 0) ++changed;
        abs_sum += static_cast<UINT64>(d);
        if (d > max_diff) max_diff = static_cast<BYTE>(d);
    }
    Log("[feed32] native CPU current output diff frame %llu: changed_bytes=%llu abs_sum=%llu max=%u mean=%.4f",
        static_cast<unsigned long long>(frame),
        static_cast<unsigned long long>(changed),
        static_cast<unsigned long long>(abs_sum),
        static_cast<unsigned>(max_diff),
        count ? double(abs_sum) / double(count) : 0.0);
}

static bool PresentD3D9Rgba(IDirect3DDevice9 *dev, IDirect3DSurface9 *back,
                            const D3DSURFACE_DESC &desc)
{
    Breadcrumb("native D3D9 present setup");
    if (g.upload9 == nullptr || g.cpu_present_rgba.size() < static_cast<size_t>(desc.Width) * desc.Height * 4u)
        return false;

    D3DLOCKED_RECT lr = {};
    Breadcrumb("native D3D9 upload lock");
    HRESULT hr = g.upload9->LockRect(&lr, nullptr, 0);
    if (FAILED(hr))
    {
        Log("[feed32] native D3D9 upload lock failed 0x%08X", hr);
        return false;
    }
    for (UINT y = 0; y < desc.Height; ++y)
    {
        BYTE *dst = static_cast<BYTE *>(lr.pBits) + static_cast<size_t>(lr.Pitch) * y;
        const BYTE *src = g.cpu_present_rgba.data() + static_cast<size_t>(desc.Width) * y * 4u;
        for (UINT x = 0; x < desc.Width; ++x)
        {
            BYTE *p = dst + x * 4u;
            const BYTE *q = src + x * 4u;
            p[0] = q[2];
            p[1] = q[1];
            p[2] = q[0];
            p[3] = 0xFF;
        }
    }
    Breadcrumb("native D3D9 upload unlock");
    g.upload9->UnlockRect();

    Breadcrumb("native D3D9 UpdateSurface backbuffer");
    if (g.frames_done < 3) Log("[feed32] native D3D9 present: UpdateSurface to backbuffer begin");
    hr = dev->UpdateSurface(g.upload9, nullptr, back, nullptr);
    if (SUCCEEDED(hr))
        return true;

    if (g.resolve9 != nullptr)
    {
        Breadcrumb("native D3D9 UpdateSurface resolve");
        if (g.frames_done < 3) Log("[feed32] native D3D9 present: UpdateSurface to resolve/StretchRect begin");
        hr = dev->UpdateSurface(g.upload9, nullptr, g.resolve9, nullptr);
        if (SUCCEEDED(hr))
            hr = dev->StretchRect(g.resolve9, nullptr, back, nullptr, D3DTEXF_NONE);
    }
    if (FAILED(hr))
    {
        Log("[feed32] native D3D9 present upload failed 0x%08X", hr);
        return false;
    }
    return true;
}

static bool SaveCpuBmp(const std::vector<BYTE> &rgba, UINT w, UINT h, const wchar_t *path)
{
    if (rgba.size() < static_cast<size_t>(w) * h * 4u) return false;
    FILE *f = nullptr;
    if (_wfopen_s(&f, path, L"wb") != 0 || f == nullptr)
        return false;

    std::vector<BYTE> bgra(rgba.size());
    for (size_t i = 0; i + 3 < rgba.size(); i += 4)
    {
        bgra[i + 0] = rgba[i + 2];
        bgra[i + 1] = rgba[i + 1];
        bgra[i + 2] = rgba[i + 0];
        bgra[i + 3] = rgba[i + 3];
    }

    BITMAPFILEHEADER bfh = {};
    BITMAPINFOHEADER bih = {};
    bfh.bfType = 0x4D42;
    bfh.bfOffBits = sizeof(BITMAPFILEHEADER) + sizeof(BITMAPINFOHEADER);
    bfh.bfSize = bfh.bfOffBits + static_cast<DWORD>(bgra.size());
    bih.biSize = sizeof(BITMAPINFOHEADER);
    bih.biWidth = static_cast<LONG>(w);
    bih.biHeight = -static_cast<LONG>(h);
    bih.biPlanes = 1;
    bih.biBitCount = 32;
    bih.biCompression = BI_RGB;
    bih.biSizeImage = static_cast<DWORD>(bgra.size());

    const bool ok = fwrite(&bfh, 1, sizeof(bfh), f) == sizeof(bfh) &&
                    fwrite(&bih, 1, sizeof(bih), f) == sizeof(bih) &&
                    fwrite(bgra.data(), 1, bgra.size(), f) == bgra.size();
    fclose(f);
    return ok;
}

static void SaveDualScreenshotD3D9()
{
    if (!g.dual_shot_pending) return;
    if (g.dual_shot_delay > 0)
    {
        --g.dual_shot_delay;
        return;
    }
    g.dual_shot_pending = false;
    wchar_t normal[MAX_PATH], dlss[MAX_PATH];
    ScreenshotPaths(normal, sizeof(normal) / sizeof(normal[0]), dlss, sizeof(dlss) / sizeof(dlss[0]));
    const bool normal_ok = SaveCpuBmp(g.cpu_color_rgba, g.width, g.height, normal);
    const bool dlss_ok = SaveCpuBmp(g.cpu_output_rgba, g.width, g.height, dlss);
    if (normal_ok && dlss_ok)
        Warn("saved native D3D9 normal+DLSS screenshots");
    Log("[feed32] native D3D9 PrintScreen saved normal=%d dlss=%d", normal_ok ? 1 : 0, dlss_ok ? 1 : 0);
}

static void FeedFrameD3D9(reshade::api::effect_runtime *rt, reshade::api::resource_view rtv)
{
    reshade::api::device *dev_api = rt->get_device();
    IDirect3DSurface9 *back = GetD3D9BackbufferSurface(dev_api, rtv);
    if (back == nullptr)
        return;

    D3DSURFACE_DESC desc = {};
    HRESULT hr = back->GetDesc(&desc);
    if (FAILED(hr))
    {
        back->Release();
        return;
    }

    if (g.dev9 == nullptr)
    {
        Breadcrumb("native D3D9 GetDevice");
        if (FAILED(back->GetDevice(&g.dev9)))
            g.dev9 = nullptr;
    }
    if (g.dev9 == nullptr)
    {
        back->Release();
        return;
    }

    if ((g.frames_done % 60) == 0 && CfgReload())
        g.built = false;
    if (!g_cfg.enabled || g_cfg.mode == 0)
    {
        back->Release();
        return;
    }

    if (!g.built || desc.Width != g.width || desc.Height != g.height || desc.Format != g.d3d9_fmt)
    {
        if (GetTickCount64() < g_retry_at)
        {
            back->Release();
            return;
        }
        Log("[feed32] native D3D9 building: %ux%u fmt=%u msaa=%u",
            desc.Width, desc.Height, static_cast<unsigned>(desc.Format), static_cast<unsigned>(desc.MultiSampleType));
        if (!BuildCpuBridge(desc.Width, desc.Height, desc.Format))
        {
            if (!g.disabled) FeedFail("native D3D9 CPU bridge build");
            back->Release();
            return;
        }
    }

    bool ok = true;
    if (!HostAlive()) { HostLost("process died"); ok = false; }
    if (ok && !CaptureD3D9Rgba(g.dev9, back, desc))
        ok = false;

    if (ok)
    {
        const UINT64 n = ++g.frame_n;
        const int reset = (g.need_reset || g_cfg.reset_every) ? 1 : 0;
        g.need_reset = false;

        BYTE tag = 'F';
        FeedFrameMsg fm = { n, static_cast<uint32_t>(reset), g_nr_enabled ? 1u : 0u,
                            static_cast<uint32_t>(g_cfg.iterations) };
        const DWORD bytes = desc.Width * desc.Height * 4u;
        FeedCpuFrameAck ca = {};
        Breadcrumb("native D3D9 pipe write frame");
        if (g.frames_done < 3) Log("[feed32] native D3D9 frame %llu: sending %lu bytes", n, bytes);
        if (!PipeWrite(&tag, 1) || !PipeWrite(&fm, sizeof(fm)) ||
            !PipeWrite(g.cpu_color_rgba.data(), bytes) ||
            !PipeRead(&ca, sizeof(ca)))
        {
            HostLost("native D3D9 frame exchange failed");
            ok = false;
        }
        else if (!ca.ok || ca.row_bytes != desc.Width * 4u)
        {
            Log("[feed32] native D3D9 host returned no output (ok=%d row=%u)", ca.ok, ca.row_bytes);
            ok = false;
        }
        else
        {
            g.cpu_output_rgba.resize(bytes);
            Breadcrumb("native D3D9 pipe read output");
            if (g.frames_done < 3) Log("[feed32] native D3D9 frame %llu: reading %lu output bytes", n, bytes);
            if (!PipeRead(g.cpu_output_rgba.data(), bytes))
            {
                HostLost("native D3D9 output read failed");
                ok = false;
            }
        }

        if (ok)
        {
            Breadcrumb("native D3D9 compose present");
            if (g.frames_done < 3) Log("[feed32] native D3D9 frame %llu: composing output for present", n);
            ComposeCpuPresent();
            if (!PresentD3D9Rgba(g.dev9, back, desc))
            {
                ok = false;
            }
        }

        if (ok)
        {
            const UINT64 done = ++g.frames_done;
            g.consecutive_fails = 0;
            if (done <= static_cast<UINT64>(g_cfg.log_frames) || (done % 1800) == 0)
            {
                LogCurrentCpuDiff(done);
                Log("[feed32] native D3D9 frame %llu delivered (%ux%u, reset=%d, nr=%s, iterations=%d, compare=%d)",
                    done, desc.Width, desc.Height, reset, g_nr_enabled ? "on" : "off",
                    g_cfg.iterations, g_cfg.compare_mode);
            }
            SaveDualScreenshotD3D9();
        }
    }

    back->Release();
}

static int D3D9FrameExceptionFilter(unsigned int code)
{
    Log("[feed32] native D3D9 frame faulted with exception 0x%08X at %s; disabling native D3D9 bridge",
        code, g_where);
    g.disabled = true;
    ReleaseD3D9Bridge();
    HostClose();
    return EXCEPTION_EXECUTE_HANDLER;
}

static void FeedFrameD3D9Guarded(reshade::api::effect_runtime *rt, reshade::api::resource_view rtv)
{
    __try
    {
        FeedFrameD3D9(rt, rtv);
    }
    __except (D3D9FrameExceptionFilter(GetExceptionCode()))
    {
    }
}

// ---------------------------------------------------------------------------
// Native D3D12 CPU bridge
// ---------------------------------------------------------------------------

static bool IsD3D12CpuFormat(DXGI_FORMAT fmt)
{
    const DXGI_FORMAT typed = TypedColorFormat(fmt);
    return typed == DXGI_FORMAT_R8G8B8A8_UNORM ||
           typed == DXGI_FORMAT_B8G8R8A8_UNORM ||
           typed == DXGI_FORMAT_R10G10B10A2_UNORM;
}

static ID3D12Resource *TryD3D12Resource(void *p)
{
    if (p == nullptr) return nullptr;
    ID3D12Resource *res = nullptr;
    __try
    {
        reinterpret_cast<IUnknown *>(p)->QueryInterface(__uuidof(ID3D12Resource),
                                                        reinterpret_cast<void **>(&res));
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        res = nullptr;
    }
    return res;
}

static ID3D12Resource *GetD3D12BackbufferResource(reshade::api::device *dev_api,
                                                  reshade::api::resource_view rtv)
{
    Breadcrumb("native D3D12 resource lookup");
    reshade::api::resource res = dev_api->get_resource_from_view(rtv);
    ID3D12Resource *back = TryD3D12Resource(reinterpret_cast<void *>(res.handle));
    if (back != nullptr)
        return back;
    return TryD3D12Resource(reinterpret_cast<void *>(rtv.handle));
}

static D3D12_RESOURCE_DESC D3D12BufferDesc(UINT64 bytes)
{
    D3D12_RESOURCE_DESC d = {};
    d.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    d.Width = bytes;
    d.Height = 1;
    d.DepthOrArraySize = 1;
    d.MipLevels = 1;
    d.SampleDesc.Count = 1;
    d.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    return d;
}

static bool EnsureD3D12Buffers(ID3D12Device *dev, const D3D12_RESOURCE_DESC &back_desc)
{
    if (g.readback12 != nullptr && g.upload12 != nullptr &&
        g.width == back_desc.Width && g.height == back_desc.Height && g.d3d12_fmt == back_desc.Format)
        return true;

    SafeRelease(g.readback12);
    SafeRelease(g.upload12);
    g.d3d12_capture_valid = false;

    D3D12_RESOURCE_DESC tex_desc = back_desc;
    tex_desc.DepthOrArraySize = 1;
    tex_desc.MipLevels = 1;
    tex_desc.SampleDesc.Count = 1;
    tex_desc.SampleDesc.Quality = 0;

    dev->GetCopyableFootprints(&tex_desc, 0, 1, 0, &g.d3d12_footprint,
                               &g.d3d12_rows, &g.d3d12_row_bytes, &g.d3d12_total_bytes);
    if (g.d3d12_total_bytes == 0 || g.d3d12_rows == 0)
    {
        Log("[feed32] native D3D12 footprint failed for %llux%u fmt=%u",
            static_cast<unsigned long long>(back_desc.Width), back_desc.Height,
            static_cast<unsigned>(back_desc.Format));
        return false;
    }

    D3D12_HEAP_PROPERTIES read_heap = {};
    read_heap.Type = D3D12_HEAP_TYPE_READBACK;
    D3D12_HEAP_PROPERTIES upload_heap = {};
    upload_heap.Type = D3D12_HEAP_TYPE_UPLOAD;
    const D3D12_RESOURCE_DESC buffer_desc = D3D12BufferDesc(g.d3d12_total_bytes);

    HRESULT hr = dev->CreateCommittedResource(&read_heap, D3D12_HEAP_FLAG_NONE, &buffer_desc,
                                              D3D12_RESOURCE_STATE_COPY_DEST, nullptr,
                                              __uuidof(ID3D12Resource), reinterpret_cast<void **>(&g.readback12));
    if (FAILED(hr) || g.readback12 == nullptr)
    {
        Log("[feed32] native D3D12 readback buffer failed 0x%08X", hr);
        return false;
    }

    hr = dev->CreateCommittedResource(&upload_heap, D3D12_HEAP_FLAG_NONE, &buffer_desc,
                                      D3D12_RESOURCE_STATE_GENERIC_READ, nullptr,
                                      __uuidof(ID3D12Resource), reinterpret_cast<void **>(&g.upload12));
    if (FAILED(hr) || g.upload12 == nullptr)
    {
        Log("[feed32] native D3D12 upload buffer failed 0x%08X", hr);
        SafeRelease(g.readback12);
        return false;
    }

    g.width = static_cast<UINT>(back_desc.Width);
    g.height = back_desc.Height;
    g.d3d12_fmt = back_desc.Format;
    Log("[feed32] native D3D12 CPU buffers ready: %ux%u fmt=%u row_pitch=%u total=%llu",
        g.width, g.height, static_cast<unsigned>(g.d3d12_fmt),
        static_cast<unsigned>(g.d3d12_footprint.Footprint.RowPitch),
        static_cast<unsigned long long>(g.d3d12_total_bytes));
    return true;
}

static bool BuildD3D12CpuBridge(UINT w, UINT h, DXGI_FORMAT fmt)
{
    Breadcrumb("building native D3D12 CPU bridge");
    ReleaseShared();
    ReleaseD3D9Bridge();

    if (!IsD3D12CpuFormat(fmt))
    {
        Log("[feed32] native D3D12 unsupported backbuffer format %u", static_cast<unsigned>(fmt));
        FeedDisable("native D3D12 backbuffer format is not supported by the CPU bridge yet");
        return false;
    }
    if (!EnsureHost()) return false;

    g.width = w;
    g.height = h;
    g.input_width = w;
    g.input_height = h;
    g.bb_fmt = DXGI_FORMAT_R8G8B8A8_UNORM;
    g.color_fmt = DXGI_FORMAT_R8G8B8A8_UNORM;
    g.output_fmt = DXGI_FORMAT_R8G8B8A8_UNORM;
    g.d3d12_fmt = fmt;
    g.cpu_color_rgba.assign(static_cast<size_t>(w) * h * 4u, 0);
    g.cpu_output_rgba.assign(static_cast<size_t>(w) * h * 4u, 0);
    g.cpu_present_rgba.assign(static_cast<size_t>(w) * h * 4u, 0);

    const bool hdr = false;
    const bool inverted = g_cfg.depth_inverted >= 0 ? g_cfg.depth_inverted != 0 : g.depth_reversed;

    FeedBuild b = {};
    b.output_width = w;
    b.output_height = h;
    b.width = w;
    b.height = h;
    b.color_fmt = DXGI_FORMAT_R8G8B8A8_UNORM;
    b.output_fmt = DXGI_FORMAT_R8G8B8A8_UNORM;
    b.hdr = hdr ? 1 : 0;
    b.depth_inverted = inverted ? 1 : 0;
    b.flags_override = g_cfg.flags;
    b.transport = g_cfg.mode == 1 ? 1 : 0;
    b.has_mask = 0;
    b.cpu_bridge = 1;
    b.mv_scale_x = g_cfg.mv_scale_x;
    b.mv_scale_y = g_cfg.mv_scale_y;

    BYTE tag = 'B';
    FeedBuildAck ack = {};
    if (!PipeWrite(&tag, 1) || !PipeWrite(&b, sizeof(b)) || !PipeRead(&ack, sizeof(ack)))
    { HostLost("native D3D12 build exchange failed"); return false; }
    if (!ack.ok)
    {
        Log("[feed32] native D3D12 host build failed (ngx 0x%08X)", ack.ngx_result);
        return false;
    }

    Log("[feed32] native D3D12 CPU bridge ready: %ux%u fmt=%u (host ngx 0x%08X, %s)",
        w, h, static_cast<unsigned>(fmt), ack.ngx_result, g_cfg.mode == 1 ? "transport" : "DLSS");
    g.built = true;
    g.need_reset = true;
    g.consecutive_fails = 0;
    return true;
}

static void D3D12Barrier(ID3D12GraphicsCommandList *list, ID3D12Resource *res,
                         D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after)
{
    if (before == after) return;
    D3D12_RESOURCE_BARRIER b = {};
    b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    b.Transition.pResource = res;
    b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    b.Transition.StateBefore = before;
    b.Transition.StateAfter = after;
    list->ResourceBarrier(1, &b);
}

static BYTE Unorm10ToByte(UINT v)
{
    return static_cast<BYTE>((v * 255u + 511u) / 1023u);
}

static UINT ByteToUnorm10(BYTE v)
{
    return (static_cast<UINT>(v) * 1023u + 127u) / 255u;
}

static bool ReadD3D12CaptureRgba()
{
    if (!g.d3d12_capture_valid || g.readback12 == nullptr)
        return false;

    BYTE *mapped = nullptr;
    D3D12_RANGE range = { static_cast<SIZE_T>(g.d3d12_footprint.Offset),
                          static_cast<SIZE_T>(g.d3d12_footprint.Offset + g.d3d12_total_bytes) };
    HRESULT hr = g.readback12->Map(0, &range, reinterpret_cast<void **>(&mapped));
    if (FAILED(hr) || mapped == nullptr)
    {
        Log("[feed32] native D3D12 readback map failed 0x%08X", hr);
        return false;
    }

    const BYTE *base = mapped + g.d3d12_footprint.Offset;
    g.cpu_color_rgba.resize(static_cast<size_t>(g.width) * g.height * 4u);
    const DXGI_FORMAT typed = TypedColorFormat(g.d3d12_fmt);
    for (UINT y = 0; y < g.height; ++y)
    {
        const BYTE *src = base + static_cast<size_t>(g.d3d12_footprint.Footprint.RowPitch) * y;
        BYTE *dst = g.cpu_color_rgba.data() + static_cast<size_t>(g.width) * y * 4u;
        for (UINT x = 0; x < g.width; ++x)
        {
            const BYTE *p = src + x * 4u;
            BYTE *q = dst + x * 4u;
            if (typed == DXGI_FORMAT_B8G8R8A8_UNORM)
            {
                q[0] = p[2]; q[1] = p[1]; q[2] = p[0]; q[3] = 0xFF;
            }
            else if (typed == DXGI_FORMAT_R10G10B10A2_UNORM)
            {
                const UINT v = *reinterpret_cast<const UINT *>(p);
                q[0] = Unorm10ToByte(v & 0x3FFu);
                q[1] = Unorm10ToByte((v >> 10) & 0x3FFu);
                q[2] = Unorm10ToByte((v >> 20) & 0x3FFu);
                q[3] = 0xFF;
            }
            else
            {
                q[0] = p[0]; q[1] = p[1]; q[2] = p[2]; q[3] = 0xFF;
            }
        }
    }
    D3D12_RANGE written = {};
    g.readback12->Unmap(0, &written);
    g.d3d12_capture_valid = false;
    return true;
}

static bool WriteD3D12UploadRgba()
{
    if (g.upload12 == nullptr ||
        g.cpu_present_rgba.size() < static_cast<size_t>(g.width) * g.height * 4u)
        return false;

    BYTE *mapped = nullptr;
    D3D12_RANGE no_read = {};
    HRESULT hr = g.upload12->Map(0, &no_read, reinterpret_cast<void **>(&mapped));
    if (FAILED(hr) || mapped == nullptr)
    {
        Log("[feed32] native D3D12 upload map failed 0x%08X", hr);
        return false;
    }

    BYTE *base = mapped + g.d3d12_footprint.Offset;
    const DXGI_FORMAT typed = TypedColorFormat(g.d3d12_fmt);
    for (UINT y = 0; y < g.height; ++y)
    {
        BYTE *dst = base + static_cast<size_t>(g.d3d12_footprint.Footprint.RowPitch) * y;
        const BYTE *src = g.cpu_present_rgba.data() + static_cast<size_t>(g.width) * y * 4u;
        for (UINT x = 0; x < g.width; ++x)
        {
            BYTE *p = dst + x * 4u;
            const BYTE *q = src + x * 4u;
            if (typed == DXGI_FORMAT_B8G8R8A8_UNORM)
            {
                p[0] = q[2]; p[1] = q[1]; p[2] = q[0]; p[3] = 0xFF;
            }
            else if (typed == DXGI_FORMAT_R10G10B10A2_UNORM)
            {
                const UINT r = ByteToUnorm10(q[0]);
                const UINT gch = ByteToUnorm10(q[1]);
                const UINT b = ByteToUnorm10(q[2]);
                const UINT a = 3u;
                *reinterpret_cast<UINT *>(p) = r | (gch << 10) | (b << 20) | (a << 30);
            }
            else
            {
                p[0] = q[0]; p[1] = q[1]; p[2] = q[2]; p[3] = 0xFF;
            }
        }
    }
    D3D12_RANGE written = { static_cast<SIZE_T>(g.d3d12_footprint.Offset),
                            static_cast<SIZE_T>(g.d3d12_footprint.Offset + g.d3d12_total_bytes) };
    g.upload12->Unmap(0, &written);
    return true;
}

static bool RecordD3D12CaptureAndPresent(ID3D12GraphicsCommandList *list, ID3D12Resource *back,
                                         bool present)
{
    if (list == nullptr || back == nullptr || g.readback12 == nullptr)
        return false;

    D3D12_TEXTURE_COPY_LOCATION back_loc = {};
    back_loc.pResource = back;
    back_loc.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
    back_loc.SubresourceIndex = 0;

    D3D12_TEXTURE_COPY_LOCATION read_loc = {};
    read_loc.pResource = g.readback12;
    read_loc.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
    read_loc.PlacedFootprint = g.d3d12_footprint;

    D3D12Barrier(list, back, D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_COPY_SOURCE);
    list->CopyTextureRegion(&read_loc, 0, 0, 0, &back_loc, nullptr);

    if (present && WriteD3D12UploadRgba())
    {
        D3D12_TEXTURE_COPY_LOCATION upload_loc = {};
        upload_loc.pResource = g.upload12;
        upload_loc.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
        upload_loc.PlacedFootprint = g.d3d12_footprint;

        D3D12Barrier(list, back, D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_COPY_DEST);
        list->CopyTextureRegion(&back_loc, 0, 0, 0, &upload_loc, nullptr);
        D3D12Barrier(list, back, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_RENDER_TARGET);
    }
    else
    {
        D3D12Barrier(list, back, D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_RENDER_TARGET);
    }

    g.d3d12_capture_valid = true;
    return true;
}

static bool ExchangeCpuFrameWithHost(const char *label, UINT64 *delivered)
{
    if (!HostAlive()) { HostLost("process died"); return false; }
    if (g.cpu_color_rgba.size() < static_cast<size_t>(g.width) * g.height * 4u)
        return false;

    const UINT64 n = ++g.frame_n;
    const int reset = (g.need_reset || g_cfg.reset_every) ? 1 : 0;
    g.need_reset = false;

    BYTE tag = 'F';
    FeedFrameMsg fm = { n, static_cast<uint32_t>(reset), g_nr_enabled ? 1u : 0u,
                        static_cast<uint32_t>(g_cfg.iterations) };
    const DWORD bytes = g.width * g.height * 4u;
    FeedCpuFrameAck ca = {};
    Breadcrumb("native CPU pipe write frame");
    if (g.frames_done < 3) Log("[feed32] %s frame %llu: sending %lu bytes", label, n, bytes);
    if (!PipeWrite(&tag, 1) || !PipeWrite(&fm, sizeof(fm)) ||
        !PipeWrite(g.cpu_color_rgba.data(), bytes) ||
        !PipeRead(&ca, sizeof(ca)))
    {
        HostLost("native CPU frame exchange failed");
        return false;
    }
    if (!ca.ok || ca.row_bytes != g.width * 4u)
    {
        Log("[feed32] %s host returned no output (ok=%d row=%u)", label, ca.ok, ca.row_bytes);
        return false;
    }

    g.cpu_output_rgba.resize(bytes);
    Breadcrumb("native CPU pipe read output");
    if (g.frames_done < 3) Log("[feed32] %s frame %llu: reading %lu output bytes", label, n, bytes);
    if (!PipeRead(g.cpu_output_rgba.data(), bytes))
    {
        HostLost("native CPU output read failed");
        return false;
    }

    ComposeCpuPresent();
    const UINT64 done = ++g.frames_done;
    g.consecutive_fails = 0;
    if (delivered != nullptr)
        *delivered = done;
    LogCurrentCpuDiff(done);
    if (done <= static_cast<UINT64>(g_cfg.log_frames) || (done % 1800) == 0)
        Log("[feed32] %s frame %llu delivered (%ux%u, reset=%d, nr=%s, iterations=%d, compare=%d)",
            label, done, g.width, g.height, reset, g_nr_enabled ? "on" : "off",
            g_cfg.iterations, g_cfg.compare_mode);
    return true;
}

static void SaveDualScreenshotCpu(const char *label)
{
    if (!g.dual_shot_pending) return;
    if (g.dual_shot_delay > 0)
    {
        --g.dual_shot_delay;
        return;
    }
    g.dual_shot_pending = false;
    wchar_t normal[MAX_PATH], dlss[MAX_PATH];
    ScreenshotPaths(normal, sizeof(normal) / sizeof(normal[0]), dlss, sizeof(dlss) / sizeof(dlss[0]));
    const bool normal_ok = SaveCpuBmp(g.cpu_color_rgba, g.width, g.height, normal);
    const bool dlss_ok = SaveCpuBmp(g.cpu_output_rgba, g.width, g.height, dlss);
    if (normal_ok && dlss_ok)
        Warn("saved native CPU normal+DLSS screenshots");
    Log("[feed32] %s PrintScreen saved normal=%d dlss=%d", label, normal_ok ? 1 : 0, dlss_ok ? 1 : 0);
}

static void FeedFrameD3D12(reshade::api::effect_runtime *rt, reshade::api::command_list *cl,
                           reshade::api::resource_view rtv)
{
    reshade::api::device *dev_api = rt->get_device();
    auto *list = reinterpret_cast<ID3D12GraphicsCommandList *>(cl->get_native());
    if (list == nullptr)
        return;

    ID3D12Resource *back = GetD3D12BackbufferResource(dev_api, rtv);
    if (back == nullptr)
        return;

    D3D12_RESOURCE_DESC desc = back->GetDesc();
    if (desc.Dimension != D3D12_RESOURCE_DIMENSION_TEXTURE2D || desc.SampleDesc.Count != 1 ||
        desc.Width > UINT_MAX || desc.Height > UINT_MAX)
    {
        Log("[feed32] native D3D12 unsupported backbuffer: dimension=%u %llux%u samples=%u fmt=%u",
            static_cast<unsigned>(desc.Dimension), static_cast<unsigned long long>(desc.Width),
            desc.Height, desc.SampleDesc.Count, static_cast<unsigned>(desc.Format));
        back->Release();
        FeedDisable("native D3D12 backbuffer layout is not supported by the CPU bridge yet");
        return;
    }

    if (g.dev12 == nullptr)
    {
        Breadcrumb("native D3D12 GetDevice");
        HRESULT hr = back->GetDevice(__uuidof(ID3D12Device), reinterpret_cast<void **>(&g.dev12));
        if (FAILED(hr) || g.dev12 == nullptr)
        {
            Log("[feed32] native D3D12 GetDevice failed 0x%08X", hr);
            back->Release();
            return;
        }
    }

    if ((g.frames_done % 60) == 0 && CfgReload())
        g.built = false;
    if (!g_cfg.enabled || g_cfg.mode == 0)
    {
        back->Release();
        return;
    }
    if (!NativeProbeAllowsFallback())
    {
        back->Release();
        return;
    }

    if (!g.built || desc.Width != g.width || desc.Height != g.height || desc.Format != g.d3d12_fmt ||
        g.readback12 == nullptr || g.upload12 == nullptr)
    {
        if (GetTickCount64() < g_retry_at)
        {
            back->Release();
            return;
        }
        Log("[feed32] native D3D12 building: %llux%u fmt=%u",
            static_cast<unsigned long long>(desc.Width), desc.Height, static_cast<unsigned>(desc.Format));
        if (!EnsureD3D12Buffers(g.dev12, desc) ||
            !BuildD3D12CpuBridge(static_cast<UINT>(desc.Width), desc.Height, desc.Format))
        {
            if (!g.disabled) FeedFail("native D3D12 CPU bridge build");
            back->Release();
            return;
        }
    }

    bool have_present = false;
    if (ReadD3D12CaptureRgba())
    {
        UINT64 delivered = 0;
        have_present = ExchangeCpuFrameWithHost("native D3D12", &delivered);
        if (have_present)
            SaveDualScreenshotCpu("native D3D12");
    }

    if (!RecordD3D12CaptureAndPresent(list, back, have_present))
        FeedFail("native D3D12 capture/present command recording");

    back->Release();
}

static int D3D12FrameExceptionFilter(unsigned int code)
{
    Log("[feed32] native D3D12 frame faulted with exception 0x%08X at %s; disabling native D3D12 bridge",
        code, g_where);
    g.disabled = true;
    ReleaseD3D12Bridge();
    HostClose();
    return EXCEPTION_EXECUTE_HANDLER;
}

static void FeedFrameD3D12Guarded(reshade::api::effect_runtime *rt, reshade::api::command_list *cl,
                                  reshade::api::resource_view rtv)
{
    __try
    {
        FeedFrameD3D12(rt, cl, rtv);
    }
    __except (D3D12FrameExceptionFilter(GetExceptionCode()))
    {
    }
}

// ---------------------------------------------------------------------------
// Per frame
// ---------------------------------------------------------------------------

static void TimingTick(LONGLONG entry, LONGLONG exit)
{
    if (g.qpf == 0)
    {
        LARGE_INTEGER f;
        QueryPerformanceFrequency(&f);
        g.qpf = f.QuadPart;
        g.span_start = entry;
    }
    g.cpu_ticks += (exit - entry);
    if (++g.timed_frames < 600) return;
    const double span_ms = 1000.0 * double(exit - g.span_start) / double(g.qpf);
    const double cpu_ms  = 1000.0 * double(g.cpu_ticks) / double(g.qpf);
    const double n       = double(g.timed_frames);
    Log("[feed32] 600 frames: feed CPU %.2f ms/frame | frame interval %.2f ms (%.1f fps) | feed is %.0f%% of the frame",
        cpu_ms / n, span_ms / n, 1000.0 / (span_ms / n), 100.0 * cpu_ms / span_ms);
    g.cpu_ticks = 0;
    g.timed_frames = 0;
    g.span_start = exit;
}

static ID3D11Texture2D *AsTexture2D(ID3D11Resource *res, D3D11_TEXTURE2D_DESC *desc)
{
    if (res == nullptr) return nullptr;
    ID3D11Texture2D *tex = nullptr;
    if (FAILED(res->QueryInterface(__uuidof(ID3D11Texture2D), reinterpret_cast<void **>(&tex))) || tex == nullptr)
        return nullptr;
    tex->GetDesc(desc);
    return tex;
}

static void FeedFrame(reshade::api::effect_runtime *rt, reshade::api::command_list *cl, reshade::api::resource_view rtv)
{
    PollToggleHotkey();
    PollCompareHotkey();
    PollDualScreenshotHotkey();

    if (!g_cfg.enabled || g.disabled || g_cfg.mode == 0) return;

    LARGE_INTEGER t0, t1;
    QueryPerformanceCounter(&t0);

    reshade::api::device *dev_api = rt->get_device();
    if (dev_api->get_api() == reshade::api::device_api::d3d9)
    {
        FeedFrameD3D9Guarded(rt, rtv);
        QueryPerformanceCounter(&t1);
        TimingTick(t0.QuadPart, t1.QuadPart);
        return;
    }
    if (dev_api->get_api() == reshade::api::device_api::d3d12)
    {
        FeedFrameD3D12Guarded(rt, cl, rtv);
        QueryPerformanceCounter(&t1);
        TimingTick(t0.QuadPart, t1.QuadPart);
        return;
    }
    if (dev_api->get_api() != reshade::api::device_api::d3d11)
    { FeedDisable("only Direct3D 9, Direct3D 11 and Direct3D 12 games are supported by this feeder add-on"); return; }

    auto *ctx = reinterpret_cast<ID3D11DeviceContext *>(cl->get_native());
    if (ctx == nullptr || ctx->GetType() != D3D11_DEVICE_CONTEXT_IMMEDIATE) return;

    if ((g.frames_done % 60) == 0 && CfgReload()) g.built = false;
    if (!g_cfg.enabled || g_cfg.mode == 0) return;
    if (!NativeProbeAllowsFallback()) return;

    reshade::api::resource_view mv_srv = {}, mv_srgb = {}, d_srv = {}, d_srgb = {}, m_srv = {}, m_srgb = {};
    if (g.mv_var.handle != 0)    rt->get_texture_binding(g.mv_var, &mv_srv, &mv_srgb);
    if (g.depth_var.handle != 0) rt->get_texture_binding(g.depth_var, &d_srv, &d_srgb);
    if (g.mask_var.handle != 0)  rt->get_texture_binding(g.mask_var, &m_srv, &m_srgb);
    const bool zero_guides = mv_srv.handle == 0 || d_srv.handle == 0;
    if (zero_guides)
    {
        if (!g.missing_reported)
        {
            g.missing_reported = true;
            Warn(g.handles_ok
                 ? "DLSS5_Feed.fx texture variables were found, but ReShade returned empty texture bindings; using zero guide fallback."
                 : "DLSS5_Feed.fx textures not found. Install DLSS5_Feed.fx and enable the DLSS 5 Feed technique.");
            Log("[feed32] binding state: handles tech=%llu mv=%llu depth=%llu mask=%llu | bindings mv=%llu depth=%llu mask=%llu",
                static_cast<unsigned long long>(g.technique.handle),
                static_cast<unsigned long long>(g.mv_var.handle),
                static_cast<unsigned long long>(g.depth_var.handle),
                static_cast<unsigned long long>(g.mask_var.handle),
                static_cast<unsigned long long>(mv_srv.handle),
                static_cast<unsigned long long>(d_srv.handle),
                static_cast<unsigned long long>(m_srv.handle));
        }
    }

    auto *color_res = reinterpret_cast<ID3D11Resource *>(dev_api->get_resource_from_view(rtv).handle);
    auto *mv_res    = !zero_guides ? reinterpret_cast<ID3D11Resource *>(dev_api->get_resource_from_view(mv_srv).handle) : nullptr;
    auto *depth_res = !zero_guides ? reinterpret_cast<ID3D11Resource *>(dev_api->get_resource_from_view(d_srv).handle) : nullptr;
    auto *mask_res  = m_srv.handle != 0 ? reinterpret_cast<ID3D11Resource *>(dev_api->get_resource_from_view(m_srv).handle) : nullptr;
    auto *rtv11     = reinterpret_cast<ID3D11RenderTargetView *>(rtv.handle);

    D3D11_TEXTURE2D_DESC cd = {}, md = {}, dd = {}, kd = {};
    ID3D11Texture2D *color = AsTexture2D(color_res, &cd);
    ID3D11Texture2D *mv    = AsTexture2D(mv_res, &md);
    ID3D11Texture2D *depth = AsTexture2D(depth_res, &dd);
    ID3D11Texture2D *mask  = mask_res != nullptr ? AsTexture2D(mask_res, &kd) : nullptr;
    if (color == nullptr || (!zero_guides && (mv == nullptr || depth == nullptr)))
    { SafeRelease(color); SafeRelease(mv); SafeRelease(depth); SafeRelease(mask); return; }

    const bool mask_ok = mask != nullptr && kd.Width == cd.Width && kd.Height == cd.Height && kd.Format == DXGI_FORMAT_R8_UNORM;
    if (g.built && mask_ok != g.bound_mask_available)
    {
        ResetBridgeForRebuild(mask_ok ? "bound mask appeared; rebuilding the host" : "bound mask disappeared; rebuilding the host");
    }
    if (mask != nullptr && !mask_ok)
    {
        static bool mask_said = false;
        if (!mask_said)
        {
            mask_said = true;
            Log("[feed32] mask ignored: DLSS5_Mask %ux%u fmt=%u, expected %ux%u fmt=%u",
                kd.Width, kd.Height, kd.Format, cd.Width, cd.Height, DXGI_FORMAT_R8_UNORM);
        }
    }

    bool ok = true;
    if (!zero_guides && (cd.Width != md.Width || cd.Height != md.Height || cd.Width != dd.Width || cd.Height != dd.Height ||
        cd.SampleDesc.Count != 1 || md.Format != DXGI_FORMAT_R16G16_FLOAT || dd.Format != DXGI_FORMAT_R32_FLOAT)
        )
    {
        static bool said = false;
        if (!said)
        {
            said = true;
            Log("[feed32] input mismatch: color %ux%u fmt=%u samp=%u | mv %ux%u fmt=%u | depth %ux%u fmt=%u",
                cd.Width, cd.Height, cd.Format, cd.SampleDesc.Count, md.Width, md.Height, md.Format,
                dd.Width, dd.Height, dd.Format);
        }
        ok = false;
    }

    if (ok && g.dev == nullptr)
    {
        ctx->GetDevice(&g.dev);
        if (g.dev != nullptr) g.dev->Release();   // not owned; the game outlives us
        if (FAILED(ctx->QueryInterface(__uuidof(ID3D11DeviceContext4), reinterpret_cast<void **>(&g.ctx4))))
        { FeedDisable("ID3D11DeviceContext4 unavailable (Windows 10 1703+ required)"); ok = false; }
    }

    if (ok && (!g.built || cd.Width != g.width || cd.Height != g.height || cd.Format != g.bb_fmt))
    {
        if (GetTickCount64() < g_retry_at)
            ok = false;                       // backing off after a failed build
        else
        {
            Log("[feed32] building: %ux%u backbuffer fmt=%u (depth reversed=%d, mode=%d)",
                cd.Width, cd.Height, cd.Format, g.depth_reversed ? 1 : 0, g_cfg.mode);
            ok = BuildShared(cd.Width, cd.Height, cd.Format, mask_ok);
            if (ok) g.consecutive_fails = 0;
            else if (!g.disabled) FeedFail("shared build");
        }
    }

    if (ok && g.built)
    {
        if (!HostAlive()) { HostLost("process died"); }
        else
        {
            Breadcrumb("copying inputs");
            auto *mv_view    = reinterpret_cast<ID3D11ShaderResourceView *>(mv_srv.handle);
            auto *depth_view = reinterpret_cast<ID3D11ShaderResourceView *>(d_srv.handle);
            auto *mask_view = mask_ok ? reinterpret_cast<ID3D11ShaderResourceView *>(m_srv.handle) : nullptr;
            if (!PrepareDlssInputs(ctx, color, mv_view, depth_view, mask_view))
            {
                Log("[feed32] input preparation failed");
                ok = false;
            }
        }
    }

    if (ok && g.built)
    {
        const UINT64 n = ++g.frame_n;
        const int reset = (g.need_reset || g_cfg.reset_every) ? 1 : 0;
        g.need_reset = false;

        g.ctx4->Signal(g.fence_in, n);
        ctx->Flush();

        BYTE tag = 'F';
        FeedFrameMsg fm = { n, static_cast<uint32_t>(reset), g_nr_enabled ? 1u : 0u,
                            static_cast<uint32_t>(g_cfg.iterations) };
        if (!PipeWrite(&tag, 1) || !PipeWrite(&fm, sizeof(fm)))
            HostLost("frame message failed");
        else
        {
            Breadcrumb("waiting for the host's result");
            g.ctx4->Wait(g.fence_out, n);       // GPU-side; the host CPU-signals on failure
            BlitOutputToBackbuffer(ctx, rtv11);
            SaveDualScreenshot(ctx);
            const UINT64 done = ++g.frames_done;
            g.consecutive_fails = 0;
            if (done <= static_cast<UINT64>(g_cfg.log_frames) || (done % 1800) == 0)
                Log("[feed32] frame %llu delivered (input %ux%u -> output %ux%u, reset=%d, nr=%s, iterations=%d, compare=%d)",
                    done, g.input_width, g.input_height, g.width, g.height, reset, g_nr_enabled ? "on" : "off",
                    g_cfg.iterations, g_cfg.compare_mode);
        }
    }

    SafeRelease(color);
    SafeRelease(mv);
    SafeRelease(depth);
    SafeRelease(mask);

    QueryPerformanceCounter(&t1);
    TimingTick(t0.QuadPart, t1.QuadPart);
}

// ---------------------------------------------------------------------------
// ReShade events
// ---------------------------------------------------------------------------

static bool NameMatchesTexture(const char *name, const char *target)
{
    if (_stricmp(name, target) == 0)
        return true;
    const size_t n = strlen(name), t = strlen(target);
    return n > t + 2 && name[n - t - 2] == ':' && name[n - t - 1] == ':' &&
           _stricmp(name + n - t, target) == 0;
}

static reshade::api::effect_texture_variable FindTextureVariable(reshade::api::effect_runtime *rt, const char *name)
{
    reshade::api::effect_texture_variable var = rt->find_texture_variable(kEffectFile, name);
    if (var.handle != 0)
        return var;

    var = rt->find_texture_variable(nullptr, name);
    if (var.handle != 0)
        return var;

    rt->enumerate_texture_variables(nullptr, [&](reshade::api::effect_runtime *runtime, reshade::api::effect_texture_variable candidate)
    {
        if (var.handle != 0)
            return;
        char candidate_name[256] = {};
        runtime->get_texture_variable_name(candidate, candidate_name);
        if (NameMatchesTexture(candidate_name, name))
            var = candidate;
    });
    return var;
}

static void LogVisibleTextures(reshade::api::effect_runtime *rt)
{
    Log("[feed32] texture lookup diagnostic: visible ReShade texture variables follow");
    int count = 0;
    rt->enumerate_texture_variables(nullptr, [&](reshade::api::effect_runtime *runtime, reshade::api::effect_texture_variable var)
    {
        if (count >= 80)
            return;
        char name[256] = {};
        char effect[256] = {};
        runtime->get_texture_variable_name(var, name);
        runtime->get_texture_variable_effect_name(var, effect);
        Log("[feed32]   texture[%02d] %s (%s)", count, name[0] ? name : "<unnamed>", effect[0] ? effect : "<unknown effect>");
        ++count;
    });
    Log("[feed32] texture lookup diagnostic: %d texture variable(s) listed%s", count, count >= 80 ? " (truncated)" : "");
}

static void ResolveHandles(reshade::api::effect_runtime *rt)
{
    const bool old_handles_ok = g.handles_ok;
    const bool old_mask_available = g.mask_available;
    g.technique = rt->find_technique(kEffectFile, kTechnique);
    g.mv_var    = FindTextureVariable(rt, "DLSS5_MV");
    g.depth_var = FindTextureVariable(rt, "DLSS5_Depth");
    g.mask_var  = FindTextureVariable(rt, "DLSS5_Mask");
    const int mode = ReadMvProviderMode(rt);
    g.launchpad = {};
    const char *provider = "none";
    const char *provider_file = nullptr;
    reshade::api::effect_technique other = {};
    const char *other_tech = nullptr;
    int other_mode = -1;
    for (const auto &p : kMvProviders)
    {
        const reshade::api::effect_technique t = rt->find_technique(p.file, p.tech);
        if (t.handle == 0) continue;
        const bool on = rt->get_technique_state(t);
        if (p.mode == mode)
        {
            if (g.launchpad.handle == 0 || on) { g.launchpad = t; provider = p.tech; provider_file = p.file; }
        }
        else if (on && other.handle == 0) { other = t; other_tech = p.tech; other_mode = p.mode; }
    }
    char compile_error[512] = {};
    const bool provider_broken = provider_file != nullptr && ProviderCompileError(provider_file, compile_error, sizeof(compile_error));

    if (!g_host_nr_loaded)
    {
        ReadHostNR(&g_host_nr);
        g_host_nr_loaded = true;
        g_nr_enabled = g_host_nr.uplift != 0;
        Log("[feed32] host DLSS 5 settings loaded into the overlay page: uplift=%d intensity=%.2f style=%d structure=%.2f tone=%.2f automask=%d uicorr=%d",
            g_host_nr.uplift, g_host_nr.intensity, g_host_nr.style, g_host_nr.structure_, g_host_nr.tone,
            g_host_nr.automask, g_host_nr.uicorr);
    }

    char v[16] = {};
    g.depth_reversed = true;
    if (rt->get_preprocessor_definition("RESHADE_DEPTH_INPUT_IS_REVERSED", v))
        g.depth_reversed = atoi(v) != 0;

    g.handles_ok = g.technique.handle != 0 && g.mv_var.handle != 0 && g.depth_var.handle != 0;
    g.mask_available = g.mask_var.handle != 0;
    if (old_mask_available != g.mask_available && g.built)
    {
        ResetBridgeForRebuild("mask availability changed; restarting the host before rebuilding");
    }
    if (g.handles_ok != old_handles_ok)
        g.missing_reported = false;

    const bool provider_on = g.launchpad.handle && rt->get_technique_state(g.launchpad);
    const int signature = (g.technique.handle ? 1 : 0) | (g.mv_var.handle ? 2 : 0) | (g.depth_var.handle ? 4 : 0) |
                          (g.launchpad.handle ? 8 : 0) | (g.depth_reversed ? 16 : 0) | (provider_on ? 32 : 0) |
                          (mode << 6) | (other.handle ? 512 : 0) | ((other_mode & 7) << 10) |
                          (provider_broken ? 8192 : 0) | (g.mask_available ? 16384 : 0);
    static int last_signature = -1;
    if (signature == last_signature) return;
    last_signature = signature;

    _snprintf_s(g_mv_status, sizeof(g_mv_status), _TRUNCATE, "DLSS5_MV_PROVIDER=%d (%s) -> %s (%s)",
                mode, kMvModeName[mode], provider,
                g.launchpad.handle ? (provider_broken ? "FAILED TO COMPILE" : provider_on ? "enabled" : "DISABLED") : "not installed");
    g_mv_problem[0] = '\0';
    Log("[feed32] effects: technique %s, DLSS5_MV %s, DLSS5_Depth %s, DLSS5_Mask %s, %s, depth reversed=%d",
        g.technique.handle ? "found" : "MISSING", g.mv_var.handle ? "found" : "MISSING",
        g.depth_var.handle ? "found" : "MISSING", g.mask_available ? "found" : "absent (no bias mask)",
        g_mv_status, g.depth_reversed ? 1 : 0);
    if (!g.handles_ok)
    {
        static bool listed_textures = false;
        if (!listed_textures)
        {
            listed_textures = true;
            LogVisibleTextures(rt);
        }
    }
    if (g.handles_ok && g.launchpad.handle == 0)
        _snprintf_s(g_mv_problem, sizeof(g_mv_problem), _TRUNCATE,
                    mode == 0
                    ? "No external texMotionVectors provider is installed, so the feeder will run with zero motion vectors. This is expected for the bundled fallback path; quality is lower, but DLSS should still run."
                    : "DLSS5_Feed.fx is compiled for motion-vector provider %d (%s) but no known %s shader is installed: motion vectors will be zero. Install one, or change the DLSS5_MV_PROVIDER preprocessor definition.",
                    mode, kMvModeName[mode], kMvModeName[mode]);
    else if (g.handles_ok && provider_broken)
        _snprintf_s(g_mv_problem, sizeof(g_mv_problem), _TRUNCATE,
                    "motion-vector provider %s FAILED TO COMPILE, so it writes nothing and DLSS runs on zero vectors. ReShade.log: %s -- use another provider (VORT: DLSS5_MV_PROVIDER=2).",
                    provider, compile_error);
    else if (g.handles_ok && !provider_on)
        _snprintf_s(g_mv_problem, sizeof(g_mv_problem), _TRUNCATE,
                    "motion-vector provider %s is installed but DISABLED: enable it above DLSS 5 Feed.", provider);
    if (other.handle != 0)
    {
        char more[320];
        _snprintf_s(more, sizeof(more), _TRUNCATE,
                    "%s%s is enabled, but DLSS5_Feed.fx is compiled for provider %d (%s) and does not read it -- set the DLSS5_MV_PROVIDER preprocessor definition to %d to use it.",
                    g_mv_problem[0] ? " " : "", other_tech, mode, kMvModeName[mode], other_mode);
        strncat_s(g_mv_problem, sizeof(g_mv_problem), more, _TRUNCATE);
    }
    if (g_mv_problem[0]) Warn("%s", g_mv_problem);
}

static void OnInitEffectRuntime(reshade::api::effect_runtime *rt)
{
    g.runtime = rt;
    StartKeyboardHook();
    ResolveHandles(rt);
    static int inits = 0;
    if (++inits <= 8) Log("[feed32] effect runtime %p initialised", (void *)rt);
}

static void OnDestroyEffectRuntime(reshade::api::effect_runtime *rt)
{
    if (rt != g.runtime) return;
    // The shared textures live on the game's device and survive runtime churn; keep them.
    g.runtime = nullptr;
    g.technique = {}; g.launchpad = {}; g.mv_var = {}; g.depth_var = {}; g.mask_var = {};
    g.handles_ok = false;
    g.mask_available = false;
    g.bound_mask_available = false;
}

static void OnReloadedEffects(reshade::api::effect_runtime *rt)
{
    if (rt == g.runtime || g.runtime == nullptr) { g.runtime = rt; ResolveHandles(rt); }
}

static void OnFinishEffects(reshade::api::effect_runtime *rt, reshade::api::command_list *cl,
                            reshade::api::resource_view rtv, reshade::api::resource_view /*rtv_srgb*/)
{
    (void)cl;
    (void)rtv;
    if (g.runtime == nullptr)
        g.runtime = rt;
    if (rt != g.runtime)
        return;

    ++g.finish_events;

    // Some D3D9->D3D11 wrapper paths create the runtime before ReShade has
    // finished compiling effects. Re-resolve lazily from the live frame loop,
    // but do not feed from here: texture bindings are more reliable in the
    // per-technique callback immediately after DLSS5_Feed rendered.
    if (!g.handles_ok || (g.finish_events % 120) == 0)
        ResolveHandles(rt);

    if (g.handles_ok && g.technique_events == 0 && g.finish_events > 180)
    {
        static bool said = false;
        if (!said)
        {
            said = true;
            Log("[feed32] no reshade_render_technique callback observed; using reshade_finish_effects fallback for frame delivery");
        }
        FeedFrame(rt, cl, rtv);
    }
}

static void OnRenderTechnique(reshade::api::effect_runtime *rt, reshade::api::effect_technique technique,
                              reshade::api::command_list *cl, reshade::api::resource_view rtv,
                              reshade::api::resource_view /*rtv_srgb*/)
{
    if (g.runtime == nullptr)
        g.runtime = rt;
    if (rt != g.runtime)
        return;

    if (!g.handles_ok || g.technique.handle == 0)
        ResolveHandles(rt);
    if (g.technique.handle == 0 || technique.handle != g.technique.handle)
        return;

    ++g.technique_events;
    g.last_technique_frame = g.finish_events;
    if (g.technique_events == 1)
        Log("[feed32] using reshade_render_technique callback for frame delivery");
    FeedFrame(rt, cl, rtv);
}

static void OnDestroyDevice(reshade::api::device *dev)
{
    if (g.dev9 != nullptr && reinterpret_cast<IDirect3DDevice9 *>(dev->get_native()) == g.dev9)
    {
        Log("[feed32] native D3D9 device destroyed; shutting down");
        ReleaseD3D9Bridge();
        HostClose();
        return;
    }

    if (g.dev12 != nullptr && reinterpret_cast<ID3D12Device *>(dev->get_native()) == g.dev12)
    {
        Log("[feed32] native D3D12 device destroyed; shutting down");
        ReleaseD3D12Bridge();
        HostClose();
        return;
    }

    if (g.dev != nullptr && reinterpret_cast<ID3D11Device *>(dev->get_native()) == g.dev)
    {
        Log("[feed32] game device destroyed; shutting down");
        ReleaseShared();
        SafeRelease(g.fence_in);
        SafeRelease(g.fence_out);
        SafeRelease(g.ctx4);
        SafeRelease(g.blit_vs);
        SafeRelease(g.blit_ps);
        SafeRelease(g.compare_ps);
        SafeRelease(g.diff_ps);
        SafeRelease(g.diff_enhanced_ps);
        SafeRelease(g.blit_sampler);
        g.dev = nullptr;
        HostClose();
    }
}

// ---------------------------------------------------------------------------
// ReShade overlay page (Add-ons tab -> DLSS 5 Feed): the local dlss5-feed.cfg, and --
// unlike the 64-bit add-on -- the DLSS 5 host's own neural-rendering settings, which
// on this path live in a separate process's ReShade.ini. Replaces the old approach of
// bridging them through hidden shader uniforms in DLSS5_Feed.fx.
// ---------------------------------------------------------------------------

static void HelpMarker(const char *desc)
{
    ImGui::TextDisabled("(?)");
    if (ImGui::IsItemHovered())
    {
        ImGui::BeginTooltip();
        ImGui::PushTextWrapPos(ImGui::GetFontSize() * 30.0f);
        ImGui::TextUnformatted(desc);
        ImGui::PopTextWrapPos();
        ImGui::EndTooltip();
    }
}

static bool EditFxBool(reshade::api::effect_runtime *rt, const char *name, const char *label)
{
    auto var = rt->find_uniform_variable(kEffectFile, name);
    if (var.handle == 0) return false;
    bool v = false;
    rt->get_uniform_value_bool(var, &v, 1);
    if (!ImGui::Checkbox(label, &v)) return false;
    rt->set_uniform_value_bool(var, &v, 1);
    return true;
}

static bool EditFxFloat(reshade::api::effect_runtime *rt, const char *name, const char *label,
                        float min_v, float max_v, float step = 0.01f)
{
    auto var = rt->find_uniform_variable(kEffectFile, name);
    if (var.handle == 0) return false;
    float v = 0.0f;
    rt->get_uniform_value_float(var, &v, 1);
    if (!ImGui::DragFloat(label, &v, step, min_v, max_v, "%.3f", 0)) return false;
    if (v < min_v) v = min_v;
    if (v > max_v) v = max_v;
    rt->set_uniform_value_float(var, &v, 1);
    return true;
}

static bool EditFxFloat2(reshade::api::effect_runtime *rt, const char *name, const char *label,
                         float min_v, float max_v, float step = 0.01f)
{
    auto var = rt->find_uniform_variable(kEffectFile, name);
    if (var.handle == 0) return false;
    float v[2] = {};
    rt->get_uniform_value_float(var, v, 2);
    if (!ImGui::DragFloat2(label, v, step, min_v, max_v, "%.3f", 0)) return false;
    for (int i = 0; i < 2; ++i)
    {
        if (v[i] < min_v) v[i] = min_v;
        if (v[i] > max_v) v[i] = max_v;
    }
    rt->set_uniform_value_float(var, v, 2);
    return true;
}

static bool EditFxIntCombo(reshade::api::effect_runtime *rt, const char *name, const char *label,
                           const char *items, int count)
{
    auto var = rt->find_uniform_variable(kEffectFile, name);
    if (var.handle == 0) return false;
    int32_t v = 0;
    rt->get_uniform_value_int(var, &v, 1);
    int vi = static_cast<int>(v);
    if (!ImGui::Combo(label, &vi, items, count)) return false;
    v = vi;
    rt->set_uniform_value_int(var, &v, 1);
    return true;
}

static bool EditHostCheckbox(const char *label, int *value)
{
    bool v = *value != 0;
    if (!ImGui::Checkbox(label, &v)) return false;
    *value = v ? 1 : 0;
    return true;
}

static bool EditHostCombo(const char *label, int *value, const char *const items[], int count)
{
    int v = *value;
    if (!ImGui::Combo(label, &v, items, count)) return false;
    *value = v;
    return true;
}

static bool EditHostSlider(const char *label, float *value, float min_v, float max_v)
{
    return ImGui::SliderFloat(label, value, min_v, max_v);
}

static void DrawFeedFxControls(reshade::api::effect_runtime *rt)
{
    bool dirty = false;
    if (ImGui::CollapsingHeader("Feed shader controls"))
    {
        dirty |= EditFxBool(rt, "MV_VALIDATE", "Validate motion vectors");
        dirty |= EditFxBool(rt, "VALIDATE_STATIC", "Static-hypothesis test");
        dirty |= EditFxFloat(rt, "STATIC_BIAS", "Static bias", 0.0f, 1.0f);
        dirty |= EditFxFloat(rt, "STATIC_MIN_CONTRAST", "Static minimum contrast", 0.0f, 0.1f, 0.001f);
        dirty |= EditFxBool(rt, "VALIDATE_LUMA", "Luma test");
        dirty |= EditFxFloat(rt, "LUMA_TOLERANCE", "Luma tolerance", 0.0f, 1.0f);
        dirty |= EditFxBool(rt, "VALIDATE_DEPTH", "Depth test");
        dirty |= EditFxFloat(rt, "DEPTH_TOLERANCE", "Depth tolerance", 0.0f, 0.5f, 0.005f);
        dirty |= EditFxBool(rt, "VALIDATE_MV", "Vector consistency test");
        dirty |= EditFxFloat(rt, "MV_CONSISTENCY", "Vector consistency (px)", 0.0f, 16.0f, 0.1f);
        dirty |= EditFxFloat(rt, "MASK_STRENGTH", "Bias-current mask strength", 0.0f, 1.0f, 0.05f);
        dirty |= EditFxFloat2(rt, "MV_SIGN", "Motion vector sign", -1.0f, 1.0f, 2.0f);
        dirty |= EditFxFloat(rt, "MV_SCALE", "Motion vector scale", 0.0f, 4.0f);
        dirty |= EditFxIntCombo(rt, "DEBUG_VIEW", "Debug view",
            "Motion vectors\0Raw depth\0Provider confidence\0Validation mask\0Mask over image\0Validation tests\0Geometry vectors\0Geometry decision\0Geometry fit quality\0", 9);

        ImGui::Separator();
        dirty |= EditFxBool(rt, "GEOM_ENABLE", "Use geometry vectors");
        dirty |= EditFxFloat(rt, "GEOM_PARALLAX", "Parallax depth scale", 0.001f, 0.5f, 0.001f);
        dirty |= EditFxFloat(rt, "GEOM_OUTLIER_PX", "Fit outlier rejection (px)", 0.5f, 32.0f, 0.5f);
        dirty |= EditFxFloat(rt, "GEOM_AGREE_PX", "Agreement (px)", 0.0f, 16.0f, 0.1f);
        dirty |= EditFxFloat(rt, "GEOM_DYNAMIC_MARGIN", "Moving-object margin", 0.0f, 0.9f, 0.01f);
        dirty |= EditFxFloat(rt, "GEOM_MASK_REJECTED", "Rejected-flow mask strength", 0.0f, 1.0f, 0.05f);
        dirty |= EditFxIntCombo(rt, "MV_LOWRES_FILTER", "Low-res provider filter", "Bilinear\0Point\0", 2);

        if (dirty)
            rt->save_current_preset();
    }
}

static void DrawOverlay(reshade::api::effect_runtime *rt)
{
    bool dirty = false;
    bool enabled = g_cfg.enabled != 0;
    if (ImGui::Checkbox("Enabled", &enabled)) { g_cfg.enabled = enabled ? 1 : 0; dirty = true; }

    ImGui::Separator();
    ImGui::TextUnformatted("Status");
    ImGui::Text("Feed: %s", g.disabled ? "disabled (see dlss5-feed.log)" : g.built ? "built" : "not built");
    ImGui::Text("DLSS5 NR: %s (%s toggles)", g_nr_enabled ? "on" : "off", HotkeyName(g_cfg.hotkey_toggle));
    ImGui::Text("Display view: %s (%s cycles)", CompareModeName(g_cfg.compare_mode), HotkeyName(g_cfg.hotkey_compare));
    ImGui::Text("Host process: %s", HostAlive() ? "running" : "not running");
    if (g.frames_done > 0) ImGui::Text("Frames delivered: %llu", static_cast<unsigned long long>(g.frames_done));
    ImGui::TextWrapped("Motion vectors: %s", g_mv_status);
    if (g_mv_problem[0])
        ImGui::TextColored(ImVec4(1.0f, 0.45f, 0.3f, 1.0f), "%s", g_mv_problem);
    if (g.disabled && ImGui::Button("Re-enable"))
    {
        g.disabled = false;
        g.consecutive_fails = 0;
        g_retry_at = 0;
        Log("[feed32] re-enabled from the overlay");
    }

    ImGui::Separator();
    ImGui::TextUnformatted("DLSS contract");
    static const char *kModes[] = { "Inert", "Transport test (no NGX, left half only)", "Full DLSS path" };
    if (ImGui::Combo("Mode", &g_cfg.mode, kModes, 3)) dirty = true;
    static const char *kTri[] = { "Auto", "Force off", "Force on" };
    int hdr_idx = g_cfg.hdr + 1, di_idx = g_cfg.depth_inverted + 1;
    if (ImGui::Combo("HDR", &hdr_idx, kTri, 3)) { g_cfg.hdr = hdr_idx - 1; dirty = true; }
    if (ImGui::Combo("Depth inverted", &di_idx, kTri, 3)) { g_cfg.depth_inverted = di_idx - 1; dirty = true; }
    bool reset_every = g_cfg.reset_every != 0;
    if (ImGui::Checkbox("Reset every frame (diagnostic)", &reset_every)) { g_cfg.reset_every = reset_every ? 1 : 0; dirty = true; }
    if (ImGui::SliderFloat("Render scale", &g_cfg.render_scale, 0.33f, 1.0f)) dirty = true;
    ImGui::SameLine(); HelpMarker("Mode 2 only. Less than 1.0 makes the feeder downsample before DLSS, so the host runs a real upscale instead of same-size DLAA.");
    if (ImGui::SliderInt("Frame iterations", &g_cfg.iterations, 1, 10)) dirty = true;
    ImGui::SameLine(); HelpMarker("Mode 2 only. The host evaluates the same delivered frame this many times before returning the final output. Reset is only sent on the first pass.");
    static const char *kCompareModes[] = {
        "Original",
        "DLSS output",
        "Split: original | DLSS",
        "Split: original | amplified difference",
        "Split: amplified difference | DLSS"
    };
    if (ImGui::Combo("Display view", &g_cfg.compare_mode, kCompareModes, 5)) dirty = true;
    ImGui::SameLine(); HelpMarker("Controls what is shown on-screen. F9 cycles the same views. PrintScreen still saves both original and DLSS output.");
    if (ImGui::SliderFloat("MV scale X", &g_cfg.mv_scale_x, 0.0f, 4.0f)) dirty = true;
    if (ImGui::SliderFloat("MV scale Y", &g_cfg.mv_scale_y, 0.0f, 4.0f)) dirty = true;

    DrawFeedFxControls(rt);

    bool show_host_window = g_cfg.host_window != 0;
    if (ImGui::Checkbox("Show the DLSS 5 host window", &show_host_window)) { g_cfg.host_window = show_host_window ? 1 : 0; dirty = true; }
    ImGui::SameLine(); HelpMarker("The helper process's own window. Only needed for settings not listed here.");
    ImGui::Text("Toggle hotkey: %s (virtual-key %d)", HotkeyName(g_cfg.hotkey_toggle), g_cfg.hotkey_toggle);
    ImGui::Text("Display hotkey: %s (virtual-key %d)", HotkeyName(g_cfg.hotkey_compare), g_cfg.hotkey_compare);

    if (ImGui::CollapsingHeader("Advanced"))
    {
        if (ImGui::InputInt("Toggle hotkey virtual-key", &g_cfg.hotkey_toggle)) dirty = true;
        ImGui::SameLine(); HelpMarker("Set to 0 to disable. F10 is virtual-key 121 if you want the old DLSS toggle.");
        if (ImGui::InputInt("Display hotkey virtual-key", &g_cfg.hotkey_compare)) dirty = true;
        ImGui::SameLine(); HelpMarker("Set to 0 to disable. F9 is virtual-key 120.");
        if (ImGui::InputInt("Screenshot hotkey virtual-key", &g_cfg.hotkey_screenshot)) dirty = true;
        ImGui::SameLine(); HelpMarker("Set to 0 to disable. PrintScreen is virtual-key 44. In wrapped games, disable ReShade's own screenshot key if it closes the game.");
        if (ImGui::InputInt("Raw create flags (-1 = auto)", &g_cfg.flags)) dirty = true;
        if (ImGui::SliderInt("Log first N frames", &g_cfg.log_frames, 0, 20)) dirty = true;
        if (ImGui::SliderInt("Native RenoDX probe seconds", &g_cfg.native_probe_seconds, 0, 60)) dirty = true;
        ImGui::SameLine(); HelpMarker("When root renodx-dlss5.addon64 is present, wait this long for native NGX activity before starting the feeder fallback.");
    }

    ImGui::Separator();
    ImGui::TextUnformatted("DLSS 5 neural-rendering settings (on the host)");
    bool host_changed = false;
    host_changed |= EditHostCheckbox("Neural uplift", &g_host_nr.uplift);
    host_changed |= EditHostCheckbox("NR upscaling", &g_host_nr.upscaling);
    static const char *kNrPresets[] = { "Default", "Preset #1", "Preset #2", "Preset #3" };
    host_changed |= EditHostCombo("NR preset", &g_host_nr.preset, kNrPresets, 4);
    static const char *kNrStyles[] = { "Natural", "Cinematic" };
    host_changed |= EditHostCombo("NR style", &g_host_nr.style, kNrStyles, 2);
    host_changed |= EditHostSlider("NR intensity", &g_host_nr.intensity, 0.0f, 2.0f);
    host_changed |= EditHostSlider("NR local structure", &g_host_nr.structure_, 0.0f, 2.0f);
    host_changed |= EditHostSlider("NR local tone", &g_host_nr.tone, 0.0f, 2.0f);
    host_changed |= EditHostSlider("NR skin structure", &g_host_nr.skin, 0.0f, 2.0f);
    host_changed |= EditHostCheckbox("NR auto mask", &g_host_nr.automask);
    host_changed |= EditHostCheckbox("NR UI correction", &g_host_nr.uicorr);
    host_changed |= EditHostSlider("Scene paper-white scale", &g_host_nr.paper_white, 0.0f, 16.0f);
    host_changed |= EditHostSlider("HDR transfer strength", &g_host_nr.transfer_strength, 0.0f, 2.0f);
    host_changed |= EditHostSlider("Color strength", &g_host_nr.color_strength, 0.0f, 2.0f);
    static const char *kDepthModes[] = { "Use game NGX flag", "Force normal depth", "Force inverted depth" };
    host_changed |= EditHostCombo("Depth convention", &g_host_nr.depth_mode, kDepthModes, 3);
    host_changed |= EditHostSlider("NR motion scale X", &g_host_nr.mvec_scale_x, 0.0f, 4.0f);
    host_changed |= EditHostSlider("NR motion scale Y", &g_host_nr.mvec_scale_y, 0.0f, 4.0f);
    if (host_changed)
        g_host_nr_dirty = true;
    if (!g_host_nr_dirty)
        ImGui::BeginDisabled();
    const bool apply_host = ImGui::Button("Apply to the DLSS 5 host");
    if (!g_host_nr_dirty)
        ImGui::EndDisabled();
    ImGui::SameLine();
    ImGui::TextDisabled(g_host_nr_dirty ? "(pending changes)" : "(no pending changes)");

    if (apply_host && g_host_nr_dirty)
    {
        HostApplySettings();
        g_host_nr_dirty = false;
    }

    if (dirty) CfgSave();
}

// ---------------------------------------------------------------------------

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_self = module;
        DisableThreadLibraryCalls(module);
        InitializeCriticalSection(&g_log_cs);
        GetModuleFileNameA(module, g_log_path, MAX_PATH);
        if (char *s = strrchr(g_log_path, '\\'))
            strcpy_s(s + 1, MAX_PATH - (s + 1 - g_log_path), "dlss5-feed.log");
        { FILE *f = nullptr; if (fopen_s(&f, g_log_path, "w") == 0 && f) fclose(f); }

        if (!reshade::register_addon(module)) return FALSE;
        Log("dlss5-feed %s %s (built %s %s) attached.", FEED_ARCH_LABEL, FEED_VERSION, __DATE__, __TIME__);
        {
            wchar_t exe[MAX_PATH] = {};
            GetModuleFileNameW(nullptr, exe, MAX_PATH);
            Log("  host game: %ls", exe);
        }
        CfgWriteDefault();
        CfgReload();

        reshade::register_event<reshade::addon_event::init_effect_runtime>(OnInitEffectRuntime);
        reshade::register_event<reshade::addon_event::destroy_effect_runtime>(OnDestroyEffectRuntime);
        reshade::register_event<reshade::addon_event::reshade_reloaded_effects>(OnReloadedEffects);
        reshade::register_event<reshade::addon_event::reshade_finish_effects>(OnFinishEffects);
        reshade::register_event<reshade::addon_event::reshade_render_technique>(OnRenderTechnique);
        reshade::register_event<reshade::addon_event::destroy_device>(OnDestroyDevice);
        reshade::register_overlay(nullptr, DrawOverlay);
    }
    else if (reason == DLL_PROCESS_DETACH)
    {
        reshade::unregister_overlay(nullptr, DrawOverlay);
        reshade::unregister_event<reshade::addon_event::init_effect_runtime>(OnInitEffectRuntime);
        reshade::unregister_event<reshade::addon_event::destroy_effect_runtime>(OnDestroyEffectRuntime);
        reshade::unregister_event<reshade::addon_event::reshade_reloaded_effects>(OnReloadedEffects);
        reshade::unregister_event<reshade::addon_event::reshade_finish_effects>(OnFinishEffects);
        reshade::unregister_event<reshade::addon_event::reshade_render_technique>(OnRenderTechnique);
        reshade::unregister_event<reshade::addon_event::destroy_device>(OnDestroyDevice);
        StopKeyboardHook();
        HostClose();
        ReleaseShared();
        ReleaseD3D9Bridge();
        ReleaseD3D12Bridge();
        reshade::unregister_addon(module);
        Log("shut down cleanly.");
    }
    return TRUE;
}
