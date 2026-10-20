#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <iostream>
#include "../source/host/phoenix-host-config.h"
#include "../source/host/phoenix-host-pixels.h"
#include "../source/host/phoenix-format-adapter.h"
#include "../source/host/phoenix-host-safety.h"

int main() {
    namespace fs = std::filesystem;
    using Json = nlohmann::json;
    const auto directory = fs::temp_directory_path() / ("phoenix-host-config-" + std::to_string(GetCurrentProcessId()));
    fs::create_directories(directory);
    const auto model = directory / "model.dll";
    { std::ofstream stream(model); stream << "CPU-only model placeholder"; }
    Json config{{"schema", "phoenix-live/1"}, {"armed", true}, {"input_provider", "feeder_d3d12"},
                {"sandbox", directory.u8string()}, {"in_place_game_directory", directory.u8string()},
                {"in_place_approval", "I approve driving this installed game in place"},
                {"direct_transfer", "srgb"}, {"color_domain", "game_nr_sdr_proxy"}, {"frame_limit", 4000000},
                {"weight_source", {{"kind", "nvidia_dll"}, {"path", model.u8string()}, {"sha256", std::string(64, 'a')}}}};
    unsigned checks = 0;
    auto expect = [&](bool result) { ++checks; if (!result) throw std::runtime_error("CPU contract check failed"); };
    unsigned executions = 0, signals = 0;
    expect(!PhoenixSubmitCommands([] { return false; }, [&] { ++executions; }, [&] { ++signals; return true; }));
    expect(executions == 0 && signals == 0);
    expect(!PhoenixSubmitCommands([] { return true; }, [&] { ++executions; }, [&] { ++signals; return false; }));
    expect(executions == 1 && signals == 1);
    expect(PhoenixSubmitCommands([] { return true; }, [&] { ++executions; }, [&] { ++signals; return true; }));
    expect(executions == 2 && signals == 2);
    for (bool processed : {false, true})
        for (bool retired : {false, true})
            for (bool terminal : {false, true})
                expect(PhoenixMayPublishShared(processed, retired, terminal) == (processed && retired && !terminal));
    uint64_t tick = 0, completed = 3;
    unsigned wakes = 0, arms = 0;
    expect(PhoenixWaitFence(4, 10, [&] { return completed; }, [&] { ++arms; return true; },
        [&](uint32_t remaining) { expect(remaining <= 10); ++tick; if (++wakes == 2) completed = 4; return true; },
        [&] { return tick; }));
    expect(wakes == 2 && arms == 1); // A stale registration woke the first wait, not this fence.
    tick = 0; completed = 0; wakes = 0;
    expect(!PhoenixWaitFence(4, 3, [&] { return completed; }, [] { return true; },
        [&](uint32_t) { ++tick; ++wakes; return true; }, [&] { return tick; }));
    expect(wakes == 3);
    unsigned removed_waits = 0;
    expect(!PhoenixWaitFence(4, 10, [] { return kPhoenixFailedSubmission; }, [] { return true; },
        [&](uint32_t) { ++removed_waits; return true; }, [] { return uint64_t(0); }));
    expect(removed_waits == 0);
    expect(!PhoenixWaitFence(kPhoenixFailedSubmission, 10, [] { return uint64_t(0); }, [] { return true; },
        [](uint32_t) { return true; }, [] { return uint64_t(0); }));
    expect(!PhoenixWaitFence(4, 10, [] { return uint64_t(0); }, [] { return false; },
        [](uint32_t) { return true; }, [] { return uint64_t(0); }));
    expect(!PhoenixWaitFence(4, 10, [] { return uint64_t(0); }, [] { return true; },
        [](uint32_t) { return false; }, [] { return uint64_t(0); }));
    for (const auto* shader : {kPhoenixUnpackShader, kPhoenixPackShader}) {
        Microsoft::WRL::ComPtr<ID3DBlob> code, errors;
        const auto result = D3DCompile(shader, strlen(shader), "phoenix-format-adapter", nullptr, nullptr,
                                       "main", "cs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &errors);
        if (FAILED(result) && errors) std::cerr << static_cast<const char*>(errors->GetBufferPointer());
        expect(SUCCEEDED(result)); // Compile the actual production shaders without creating a GPU device.
    }
    std::vector<uint8_t> ramp(256);
    for (unsigned i = 0; i < ramp.size(); ++i) ramp[i] = static_cast<uint8_t>(i);
    const auto half_ramp = PhoenixProbeHalfPixels(ramp);
    for (unsigned i = 0; i < ramp.size(); ++i) expect(PhoenixProbeByte(half_ramp[i]) == ramp[i]);
    expect(PhoenixProbeByte(0x7c00) == 0); // Non-finite output cannot wrap into a pixel.
    expect(PhoenixProbeByte(0xbc00) == 0);
    expect(PhoenixProbeByte(0x4000) == 255);
    const DXGI_FORMAT supported[]{DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_FORMAT_B8G8R8A8_UNORM,
                                  DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_FORMAT_R11G11B10_FLOAT};
    for (auto input : supported) {
        for (auto output : supported) {
            expect(PhoenixHostColorRoute(input, output) ==
                   (PhoenixByteFormat(output) ? PhoenixColorRoute::AdaptBytes : PhoenixColorRoute::DirectFloat));
            for (bool processing : {false, true})
                expect(PhoenixHostDirectColorPath(input, output, processing) ==
                       (processing ? PhoenixFloatFormat(output) : input == output));
        }
        for (auto invalid : {DXGI_FORMAT_UNKNOWN, DXGI_FORMAT_R8_UNORM, DXGI_FORMAT_R8G8B8A8_UNORM_SRGB}) {
            expect(PhoenixHostColorRoute(input, invalid) == PhoenixColorRoute::Unsupported);
            expect(PhoenixHostColorRoute(invalid, input) == PhoenixColorRoute::Unsupported);
        }
    }
    expect(PhoenixByteRowPitch(1) == 256);
    expect(PhoenixByteRowPitch(64) == 256);
    expect(PhoenixByteRowPitch(65) == 512);
    expect(PhoenixByteRowPitch(2560) == 10240);
    for (unsigned i = 0; i < 256; ++i) {
        const uint8_t rgba[]{static_cast<uint8_t>(i), static_cast<uint8_t>(255 - i),
                             static_cast<uint8_t>(i ^ 0x55), static_cast<uint8_t>(i ^ 0xaa)};
        for (bool bgra : {false, true}) {
            const auto packed = PhoenixPackPixel(rgba, bgra);
            expect((packed & 255) == rgba[bgra ? 2 : 0]);
            expect(((packed >> 8) & 255) == rgba[1]);
            expect(((packed >> 16) & 255) == rgba[bgra ? 0 : 2]);
            expect((packed >> 24) == rgba[3]);
        }
    }
    expect(PhoenixHostIterations(1, 7) == 7); // OpenXR IPC requests one; its host INI selects seven.
    expect(PhoenixHostIterations(5, 0) == 5);
    expect(PhoenixHostIterations(0, 0) == 1);
    expect(PhoenixHostIterations(42, 0) == 10);
    bool bad_iterations_rejected = false;
    try { PhoenixHostIterations(1, 11); } catch (const std::runtime_error&) { bad_iterations_rejected = true; }
    expect(bad_iterations_rejected);
    auto rejects = [&](Json modified) {
        try { ValidatePhoenixHostConfig(modified, directory); return false; } catch (const std::exception&) { return true; }
    };
    expect(ValidatePhoenixHostConfig(config, directory));
    auto modified = config; modified["armed"] = false; expect(!ValidatePhoenixHostConfig(modified, directory));
    modified = config; modified["input_provider"] = "native_nr"; expect(rejects(modified));
    modified = config; modified["input_provider"] = "game_dlss"; expect(rejects(modified));
    modified = config; modified["portable_worker"] = Json::object(); expect(rejects(modified));
    modified = config; modified["sandbox"] = directory.parent_path().u8string(); expect(rejects(modified));
    modified = config; modified["in_place_approval"] = ""; expect(rejects(modified));
    modified = config; modified["direct_transfer"] = "HDR guess"; expect(rejects(modified));
    modified = config; modified["weight_source"]["path"] = "relative.dll"; expect(rejects(modified));
    modified = config; modified["weight_source"]["sha256"] = "uppercase/missing"; expect(rejects(modified));
    modified = config; modified["frame_limit"] = 0; expect(rejects(modified));
    Json before{{"phoenix_routing", {{"frames", 0}}}, {"completed_frames", 0}, {"worker_execution", {{"presented", 0}}}};
    Json after{{"schema", "production-game-phoenix/2"}, {"input_provider", "feeder_d3d12"}, {"terminal", false},
        {"direct_game", {{"enabled", true}, {"status", "Own network output written back to the game DLSS output"}}},
        {"phoenix_routing", {{"frames", 1}, {"active_mode", 3}}}, {"completed_frames", 1}, {"worker_execution", {{"kind", "native"}}}};
    expect(PhoenixOutputAdvanced(before, after));
    modified = after; modified["input_provider"] = "game_dlss"; expect(!PhoenixOutputAdvanced(before, modified));
    modified = after; modified["completed_frames"] = 0; expect(!PhoenixOutputAdvanced(before, modified));
    modified = after; modified["phoenix_routing"]["frames"] = 0; expect(!PhoenixOutputAdvanced(before, modified));
    modified = after; modified["terminal"] = true; expect(!PhoenixOutputAdvanced(before, modified));
    modified = after; modified["direct_game"]["status"] = "Game output unchanged; showing the game DLSS input"; expect(!PhoenixOutputAdvanced(before, modified));
    modified = after; modified["phoenix_routing"]["active_mode"] = 0; expect(!PhoenixOutputAdvanced(before, modified));
    modified = after; modified["phoenix_routing"]["active_mode"] = 1;
    expect(!PhoenixOutputAdvanced(before, modified));
    modified["direct_game"]["status"] = "Native NR output (resolved) written back to the game DLSS output";
    expect(PhoenixOutputAdvanced(before, modified));
    modified = after; modified["worker_execution"] = {{"kind", "portable_worker"}, {"presented", 0}, {"delivered", 1}};
    expect(!PhoenixOutputAdvanced(before, modified));
    modified["worker_execution"]["presented"] = 1; expect(!PhoenixOutputAdvanced(before, modified));
    modified["worker_execution"]["state"] = "composing_reprojected_result";
    expect(PhoenixOutputAdvanced(before, modified));
    modified["worker_execution"]["error"] = "worker died"; expect(!PhoenixOutputAdvanced(before, modified));
    Json panel{{"schema", "phoenix-panel/1"}, {"error", ""}, {"last_mode", 3},
        {"phoenix", {{"submitted_frames", 1}, {"recorded_frames", 1},
            {"status", "Own network output written back to the game DLSS output"}}},
        {"execution", {{"kind", "portable_worker"}, {"state", "composing_reprojected_result"}, {"delivered", 1}, {"presented", 0}}}};
    // Compact panel polls must preserve the worker delivery proof: a recorded
    // or submitted frame alone must never be returned as a processed result.
    expect(!PhoenixOutputAdvanced(before, PhoenixCompactPanelProgress(panel)));
    panel["execution"]["presented"] = 1;
    expect(PhoenixOutputAdvanced(before, PhoenixCompactPanelProgress(panel)));
    panel["error"] = "device lost";
    expect(!PhoenixOutputAdvanced(before, PhoenixCompactPanelProgress(panel)));
    panel["error"] = ""; panel["execution"]["error"] = "worker exited";
    expect(!PhoenixOutputAdvanced(before, PhoenixCompactPanelProgress(panel)));
    bool invalid_panel_rejected = false;
    try { PhoenixCompactPanelProgress(Json{{"schema", "unknown"}}); }
    catch (const std::exception&) { invalid_panel_rejected = true; }
    expect(invalid_panel_rejected);
    expect(!PhoenixOutputAdvanced(Json::object(), Json::object()));
    fs::remove(model); fs::remove(directory);
    std::cout << checks << " Phoenix DLSS5 host CPU configuration checks passed; no GPU/inference executed\n";
}
