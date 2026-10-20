// Vulkan SDR -> synchronous CPU IPC -> D3D12 NGX/Phoenix host -> same Vulkan image.
// This deliberately trades throughput for an explicit, bounded cross-API contract.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <filesystem>
#include <fstream>
#include <vector>
#include <algorithm>
#include <cstring>
#include <stdexcept>
#include <string>
#include <cstdlib>
#include "../external/reshade/include/reshade.hpp"
#include "../src/feed_ipc.h"

namespace api = reshade::api;
static HMODULE self;
static api::effect_runtime *owner;
static api::resource readback{}, upload{};
static HANDLE pipe = INVALID_HANDLE_VALUE, host_process;
static uint32_t width, height;
static uint64_t frame;
static bool disabled;

static std::filesystem::path Directory() {
    wchar_t path[32768]{};
    if (!GetModuleFileNameW(self, path, 32768)) throw std::runtime_error("Vulkan addon location unavailable");
    return std::filesystem::path(path).parent_path();
}

static void Transfer(bool write, void *data, DWORD bytes) {
    auto *cursor = static_cast<unsigned char *>(data);
    while (bytes) {
        OVERLAPPED operation{};
        operation.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!operation.hEvent) throw std::runtime_error("Vulkan IPC event failed");
        DWORD transferred = 0;
        const BOOL started = write ? WriteFile(pipe, cursor, bytes, &transferred, &operation)
                                   : ReadFile(pipe, cursor, bytes, &transferred, &operation);
        bool ok = started != FALSE;
        if (!ok && GetLastError() == ERROR_IO_PENDING) {
            ok = WaitForSingleObject(operation.hEvent, 15000) == WAIT_OBJECT_0 &&
                 GetOverlappedResult(pipe, &operation, &transferred, FALSE);
            if (!ok) { CancelIoEx(pipe, &operation); GetOverlappedResult(pipe, &operation, &transferred, TRUE); }
        }
        CloseHandle(operation.hEvent);
        if (!ok || !transferred || transferred > bytes) throw std::runtime_error("Vulkan host IPC failed/timed out");
        cursor += transferred; bytes -= transferred;
    }
}

static void Connect() {
    const auto directory = Directory() / L"host64";
    const auto exe = directory / L"dlss5-feed-host64.exe";
    auto command = L"\"" + exe.wstring() + L"\" " + std::to_wstring(GetCurrentProcessId()) + L" --hide";
    STARTUPINFOW startup{sizeof(startup)};
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(exe.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW,
                        nullptr, directory.c_str(), &startup, &process)) throw std::runtime_error("Phoenix DLSS5 Vulkan host could not start");
    host_process = process.hProcess; CloseHandle(process.hThread);
    wchar_t name[128]{};
    swprintf_s(name, L"\\\\.\\pipe\\dlss5-feed.%lu", GetCurrentProcessId());
    const auto deadline = GetTickCount64() + 15000;
    do {
        pipe = CreateFileW(name, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);
        if (pipe != INVALID_HANDLE_VALUE) break;
        if (WaitForSingleObject(host_process, 0) == WAIT_OBJECT_0) throw std::runtime_error("Phoenix DLSS5 Vulkan host exited during startup");
        Sleep(50);
    } while (GetTickCount64() < deadline);
    if (pipe == INVALID_HANDLE_VALUE) throw std::runtime_error("Phoenix DLSS5 Vulkan host pipe unavailable");
    FeedHello hello{FEED_IPC_MAGIC, FEED_IPC_VERSION, GetCurrentProcessId()};
    FeedHelloAck ack{};
    Transfer(true, &hello, sizeof(hello)); Transfer(false, &ack, sizeof(ack));
    if (ack.magic != FEED_IPC_MAGIC || ack.version != FEED_IPC_VERSION) throw std::runtime_error("Phoenix DLSS5 Vulkan IPC version mismatch");
}

static void Release(api::effect_runtime *runtime) {
    if (owner != runtime) return;
    auto *device = runtime->get_device();
    runtime->get_command_queue()->wait_idle();
    if (readback.handle) device->destroy_resource(readback);
    if (upload.handle) device->destroy_resource(upload);
    readback = {}; upload = {}; width = height = 0;
    if (pipe != INVALID_HANDLE_VALUE) { CloseHandle(pipe); pipe = INVALID_HANDLE_VALUE; }
    if (host_process) { CloseHandle(host_process); host_process = nullptr; }
    owner = nullptr;
}

static void Build(api::effect_runtime *runtime, const api::resource_desc& desc) {
    auto *device = runtime->get_device();
    auto *queue = runtime->get_command_queue();
    queue->wait_idle();
    if (readback.handle) device->destroy_resource(readback);
    if (upload.handle) device->destroy_resource(upload);
    readback = {}; upload = {};
    width = desc.texture.width; height = desc.texture.height;
    if (!width || !height || width > 8192 || height > 8192) throw std::runtime_error("Vulkan bridge extent outside 1..8192");
    const auto bytes = uint64_t(width) * height * 4;
    if (!device->check_capability(api::device_caps::copy_buffer_to_texture) ||
        !device->create_resource(api::resource_desc(bytes, api::memory_heap::readback, api::resource_usage::copy_dest),
                                 nullptr, api::resource_usage::copy_dest, &readback) ||
        !device->create_resource(api::resource_desc(bytes, api::memory_heap::upload, api::resource_usage::copy_source),
                                 nullptr, api::resource_usage::copy_source, &upload)) throw std::runtime_error("Vulkan bridge staging allocation unsupported");
    if (pipe == INVALID_HANDLE_VALUE) Connect();
    FeedBuild build{};
    build.width = build.output_width = width; build.height = build.output_height = height;
    build.color_fmt = build.output_fmt = 28; // DXGI_FORMAT_R8G8B8A8_UNORM
    build.cpu_bridge = 1; build.flags_override = -1; build.depth_inverted = 1;
    build.mv_scale_x = build.mv_scale_y = 1;
    unsigned char tag = 'B'; FeedBuildAck ack{};
    Transfer(true, &tag, 1); Transfer(true, &build, sizeof(build)); Transfer(false, &ack, sizeof(ack));
    if (!ack.ok) throw std::runtime_error("Phoenix DLSS5 Vulkan host rejected the DLAA feature");
    frame = 0;
    reshade::log::message(reshade::log::level::info, "Phoenix DLSS5 Vulkan SDR CPU bridge ready; synthetic depth/zero motion; inference unverified");
}

static unsigned Iterations() {
    std::ifstream config(Directory() / L"dlss5-feed.cfg");
    std::string line;
    while (std::getline(config, line)) if (line.rfind("iterations=", 0) == 0)
        return std::clamp(std::atoi(line.c_str() + 11), 1, 10);
    return 1;
}

static void Frame(api::effect_runtime *runtime, api::command_list *commands, api::resource_view rtv, api::resource_view) {
    if (disabled || runtime->get_device()->get_api() != api::device_api::vulkan || (owner && owner != runtime)) return;
    owner = runtime;
    try {
        auto *device = runtime->get_device(); auto *queue = runtime->get_command_queue();
        if (commands->get_native() != queue->get_immediate_command_list()->get_native())
            throw std::runtime_error("Vulkan bridge requires the submitting immediate list");
        auto image = device->get_resource_from_view(rtv);
        const auto desc = device->get_resource_desc(image);
        const bool bgra = desc.texture.format == api::format::b8g8r8a8_unorm || desc.texture.format == api::format::b8g8r8a8_unorm_srgb;
        const bool rgba = desc.texture.format == api::format::r8g8b8a8_unorm || desc.texture.format == api::format::r8g8b8a8_unorm_srgb;
        if ((!bgra && !rgba) || desc.texture.samples != 1 || desc.texture.depth_or_layers != 1)
            throw std::runtime_error("Vulkan Phoenix DLSS5 bridge supports single-layer single-sample RGBA8/BGRA8 SDR only");
        if (width != desc.texture.width || height != desc.texture.height || !readback.handle) Build(runtime, desc);
        commands->barrier(image, api::resource_usage::render_target, api::resource_usage::copy_source);
        commands->copy_texture_to_buffer(image, 0, nullptr, readback, 0, width, height);
        commands->barrier(image, api::resource_usage::copy_source, api::resource_usage::render_target);
        queue->flush_immediate_command_list(); queue->wait_idle();
        const DWORD bytes = width * height * 4;
        std::vector<unsigned char> pixels(bytes);
        void *mapped = nullptr;
        if (!device->map_buffer_region(readback, 0, bytes, api::map_access::read_only, &mapped)) throw std::runtime_error("Vulkan readback map failed");
        std::memcpy(pixels.data(), mapped, bytes); device->unmap_buffer_region(readback);
        if (bgra) for (size_t i = 0; i < pixels.size(); i += 4) std::swap(pixels[i], pixels[i + 2]);
        unsigned char tag = 'F'; FeedFrameMsg message{};
        message.n = ++frame; message.reset = frame == 1; message.nr_enabled = 1; message.iterations = Iterations();
        Transfer(true, &tag, 1); Transfer(true, &message, sizeof(message)); Transfer(true, pixels.data(), bytes);
        FeedCpuFrameAck ack{}; Transfer(false, &ack, sizeof(ack));
        if (!ack.ok || ack.row_bytes != width * 4) throw std::runtime_error("Phoenix DLSS5 Vulkan frame failed; input retained");
        Transfer(false, pixels.data(), bytes);
        if (bgra) for (size_t i = 0; i < pixels.size(); i += 4) std::swap(pixels[i], pixels[i + 2]);
        if (!device->map_buffer_region(upload, 0, bytes, api::map_access::write_only, &mapped)) throw std::runtime_error("Vulkan upload map failed");
        std::memcpy(mapped, pixels.data(), bytes); device->unmap_buffer_region(upload);
        commands->barrier(image, api::resource_usage::render_target, api::resource_usage::copy_dest);
        commands->copy_buffer_to_texture(upload, 0, width, height, image, 0);
        commands->barrier(image, api::resource_usage::copy_dest, api::resource_usage::render_target);
        // Upload is reused on the next callback, so retire this copy before returning.
        queue->flush_immediate_command_list(); queue->wait_idle();
    } catch (const std::exception& error) {
        disabled = true;
        reshade::log::message(reshade::log::level::error, error.what());
        Release(runtime);
    }
}

extern "C" __declspec(dllexport) const char *NAME = "Phoenix DLSS5 Vulkan Feeder";
extern "C" __declspec(dllexport) const char *DESCRIPTION = "Vulkan SDR CPU bridge to Phoenix DLSS5 D3D12 NGX host";
BOOL WINAPI DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        self = module; DisableThreadLibraryCalls(module);
        if (!reshade::register_addon(module)) return FALSE;
        reshade::register_event<reshade::addon_event::reshade_finish_effects>(Frame);
        reshade::register_event<reshade::addon_event::destroy_effect_runtime>(Release);
    } else if (reason == DLL_PROCESS_DETACH) {
        reshade::unregister_event<reshade::addon_event::reshade_finish_effects>(Frame);
        reshade::unregister_event<reshade::addon_event::destroy_effect_runtime>(Release);
        reshade::unregister_addon(module);
    }
    return TRUE;
}
