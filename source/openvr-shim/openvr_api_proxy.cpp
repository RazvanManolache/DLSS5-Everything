#include <Windows.h>
#include <cstdint>
#include <string>

extern "C" IMAGE_DOS_HEADER __ImageBase;

namespace
{
constexpr wchar_t kOriginalOpenVr[] = L"openvr_api.dlss5-original.dll";
constexpr wchar_t kReShadeDll[] = L"ReShade64.dll";

HMODULE g_original = nullptr;
HMODULE g_reshade = nullptr;
INIT_ONCE g_original_once = INIT_ONCE_STATIC_INIT;
INIT_ONCE g_reshade_once = INIT_ONCE_STATIC_INIT;

std::wstring ModuleSiblingPath(const wchar_t *file_name)
{
    wchar_t module_path[MAX_PATH] = {};
    GetModuleFileNameW(reinterpret_cast<HMODULE>(&__ImageBase), module_path, ARRAYSIZE(module_path));

    wchar_t *slash = wcsrchr(module_path, L'\\');
    if (slash == nullptr)
        return file_name;

    slash[1] = L'\0';
    return std::wstring(module_path) + file_name;
}

BOOL CALLBACK InitOriginal(PINIT_ONCE, PVOID, PVOID *)
{
    g_original = LoadLibraryW(ModuleSiblingPath(kOriginalOpenVr).c_str());
    return TRUE;
}

BOOL CALLBACK InitReShade(PINIT_ONCE, PVOID, PVOID *)
{
    InitOnceExecuteOnce(&g_original_once, InitOriginal, nullptr, nullptr);
    const auto reshade_path = ModuleSiblingPath(kReShadeDll);
    if (GetFileAttributesW(reshade_path.c_str()) != INVALID_FILE_ATTRIBUTES)
        g_reshade = LoadLibraryW(reshade_path.c_str());

    return TRUE;
}

void EnsureOriginal()
{
    InitOnceExecuteOnce(&g_original_once, InitOriginal, nullptr, nullptr);
}

void EnsureReShade()
{
    InitOnceExecuteOnce(&g_reshade_once, InitReShade, nullptr, nullptr);
}

DWORD WINAPI DelayedReShadeLoadThread(LPVOID)
{
    Sleep(500);
    EnsureReShade();
    return 0;
}

FARPROC Proc(const char *name)
{
    EnsureOriginal();
    return g_original != nullptr ? GetProcAddress(g_original, name) : nullptr;
}

template <typename Ret, typename... Args>
Ret Call(const char *name, Ret fallback, Args... args)
{
    using Fn = Ret(__cdecl *)(Args...);
    if (const auto proc = reinterpret_cast<Fn>(Proc(name)))
        return proc(args...);
    return fallback;
}

template <typename... Args>
void CallVoid(const char *name, Args... args)
{
    using Fn = void(__cdecl *)(Args...);
    if (const auto proc = reinterpret_cast<Fn>(Proc(name)))
        proc(args...);
}
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        if (HANDLE thread = CreateThread(nullptr, 0, DelayedReShadeLoadThread, nullptr, 0, nullptr))
            CloseHandle(thread);
    }

    return TRUE;
}

extern "C" __declspec(dllexport) void * __cdecl LiquidVR()
{
    return Call<void *>("LiquidVR", nullptr);
}

extern "C" __declspec(dllexport) void * __cdecl VR_GetGenericInterface(const char *interface_name, int *error)
{
    return Call<void *>("VR_GetGenericInterface", nullptr, interface_name, error);
}

extern "C" __declspec(dllexport) uint32_t __cdecl VR_GetInitToken()
{
    return Call<uint32_t>("VR_GetInitToken", 0);
}

extern "C" __declspec(dllexport) const char * __cdecl VR_GetRuntimePath()
{
    return Call<const char *>("VR_GetRuntimePath", nullptr);
}

extern "C" __declspec(dllexport) const char * __cdecl VR_GetStringForHmdError(int error)
{
    return Call<const char *>("VR_GetStringForHmdError", "", error);
}

extern "C" __declspec(dllexport) const char * __cdecl VR_GetVRInitErrorAsEnglishDescription(int error)
{
    return Call<const char *>("VR_GetVRInitErrorAsEnglishDescription", "", error);
}

extern "C" __declspec(dllexport) const char * __cdecl VR_GetVRInitErrorAsSymbol(int error)
{
    return Call<const char *>("VR_GetVRInitErrorAsSymbol", "", error);
}

extern "C" __declspec(dllexport) uint32_t __cdecl VR_InitInternal(int *error, int application_type)
{
    return Call<uint32_t>("VR_InitInternal", 0, error, application_type);
}

extern "C" __declspec(dllexport) uint32_t __cdecl VR_InitInternal2(int *error, int application_type, const char *startup_info)
{
    return Call<uint32_t>("VR_InitInternal2", 0, error, application_type, startup_info);
}

extern "C" __declspec(dllexport) bool __cdecl VR_IsHmdPresent()
{
    return Call<bool>("VR_IsHmdPresent", false);
}

extern "C" __declspec(dllexport) bool __cdecl VR_IsInterfaceVersionValid(const char *interface_name)
{
    return Call<bool>("VR_IsInterfaceVersionValid", false, interface_name);
}

extern "C" __declspec(dllexport) bool __cdecl VR_IsRuntimeInstalled()
{
    return Call<bool>("VR_IsRuntimeInstalled", false);
}

extern "C" __declspec(dllexport) const char * __cdecl VR_RuntimePath()
{
    return Call<const char *>("VR_RuntimePath", nullptr);
}

extern "C" __declspec(dllexport) void __cdecl VR_ShutdownInternal()
{
    CallVoid("VR_ShutdownInternal");
}

extern "C" __declspec(dllexport) void * __cdecl VRCompositorSystemInternal()
{
    return Call<void *>("VRCompositorSystemInternal", nullptr);
}

extern "C" __declspec(dllexport) void * __cdecl VRControlPanel()
{
    return Call<void *>("VRControlPanel", nullptr);
}

extern "C" __declspec(dllexport) void * __cdecl VRDashboardManager()
{
    return Call<void *>("VRDashboardManager", nullptr);
}

extern "C" __declspec(dllexport) void * __cdecl VRHeadsetView()
{
    return Call<void *>("VRHeadsetView", nullptr);
}

extern "C" __declspec(dllexport) void * __cdecl VROculusDirect()
{
    return Call<void *>("VROculusDirect", nullptr);
}

extern "C" __declspec(dllexport) void * __cdecl VRPaths()
{
    return Call<void *>("VRPaths", nullptr);
}

extern "C" __declspec(dllexport) void * __cdecl VRRenderModelsInternal()
{
    return Call<void *>("VRRenderModelsInternal", nullptr);
}

extern "C" __declspec(dllexport) void * __cdecl VRSceneGraph()
{
    return Call<void *>("VRSceneGraph", nullptr);
}

extern "C" __declspec(dllexport) void * __cdecl VRTrackedCameraInternal()
{
    return Call<void *>("VRTrackedCameraInternal", nullptr);
}

extern "C" __declspec(dllexport) void * __cdecl VRVirtualDisplay()
{
    return Call<void *>("VRVirtualDisplay", nullptr);
}

extern "C" __declspec(dllexport) void * __cdecl VR_Init(int *error, int application_type)
{
    return Call<void *>("VR_Init", nullptr, error, application_type);
}

extern "C" __declspec(dllexport) void __cdecl VR_Shutdown()
{
    CallVoid("VR_Shutdown");
}
