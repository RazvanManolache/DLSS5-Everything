#pragma once
#include <filesystem>
#include <fstream>
#include <nlohmann/json.hpp>

inline uint32_t PhoenixHostIterations(uint32_t requested, unsigned configured) {
    if (configured > 10) throw std::runtime_error("Invalid host FrameIterations; expected 0..10");
    return configured ? configured : (requested < 1 ? 1 : requested > 10 ? 10 : requested);
}

// Kept separate from GPU setup so configuration rejection can be CPU-tested.
inline bool ValidatePhoenixHostConfig(const nlohmann::json& config,
                                     const std::filesystem::path& directory) {
    using namespace std::filesystem;
    if (config.at("schema") != "phoenix-live/1" || config.at("input_provider") != "feeder_d3d12")
        throw std::runtime_error("Phoenix DLSS5 host needs phoenix-live/1 and input_provider=feeder_d3d12; SR/DLAA fallback is unavailable");
    if (config.contains("portable_worker"))
        throw std::runtime_error("Python worker configurations are not supported by this host");
    if (weakly_canonical(u8path(config.at("sandbox").get<std::string>())) != weakly_canonical(directory) ||
        weakly_canonical(u8path(config.at("in_place_game_directory").get<std::string>())) != weakly_canonical(directory) ||
        config.at("in_place_approval") != "I approve driving this installed game in place")
        throw std::runtime_error("Phoenix DLSS5 configuration does not name this exact host directory");
    const auto transfer = config.at("direct_transfer").get<std::string>();
    if (transfer != "linear" && transfer != "srgb") throw std::runtime_error("Invalid Phoenix DLSS5 transfer");
    if (config.at("color_domain") != "game_nr_sdr_proxy") throw std::runtime_error("Unsupported Phoenix DLSS5 host color domain");
    const auto& source = config.at("weight_source");
    const auto model = u8path(source.at("path").get<std::string>());
    if (!model.is_absolute()) throw std::runtime_error("Phoenix DLSS5 model path must be absolute");
    if (source.at("kind") == "nvidia_dll") {
        const auto hash = source.at("sha256").get<std::string>();
        if (!is_regular_file(model) || hash.size() != 64 || hash.find_first_not_of("0123456789abcdef") != std::string::npos)
            throw std::runtime_error("Phoenix DLSS5 model DLL or checksum is invalid");
    } else if (source.at("kind") != "model_folder" || !is_regular_file(model / "model.json"))
        throw std::runtime_error("Phoenix DLSS5 model folder is invalid");
    const auto limit = config.at("frame_limit").get<unsigned>();
    if (!limit || limit > 4000000) throw std::runtime_error("Invalid Phoenix DLSS5 frame cap");
    return config.at("armed").get<bool>();
}

// A recorded call is not completion. Require evidence for this evaluated output
// after the host submission fence has retired, before returning it to a feeder.
inline nlohmann::json PhoenixCompactPanelProgress(const nlohmann::json& panel) {
    if (panel.at("schema") != "phoenix-panel/1") throw std::runtime_error("Unknown Phoenix panel schema");
    const auto& own = panel.at("phoenix");
    // These submission counters are usable only after the host's queue fence
    // retires. Portable workers additionally need delivered/presented output.
    return {{"schema","production-game-phoenix/2"},{"input_provider","feeder_d3d12"},
        {"terminal",!panel.value("error",std::string()).empty()},
        {"completed_frames",own.at("submitted_frames")},
        {"phoenix_routing",{{"frames",own.at("recorded_frames")},{"active_mode",panel.at("last_mode")}}},
        {"direct_game",{{"enabled",true},{"status",own.at("status")}}},
        {"worker_execution",panel.at("execution")}};
}
inline bool PhoenixOutputAdvanced(const nlohmann::json& before, const nlohmann::json& after) {
    try {
        if (after.at("schema") != "production-game-phoenix/2" || after.at("input_provider") != "feeder_d3d12" ||
            after.at("terminal").get<bool>() ||
            !after.at("direct_game").at("enabled").get<bool>()) return false;
        const auto& route = after.at("phoenix_routing");
        if (route.at("frames").get<uint64_t>() <= before.at("phoenix_routing").at("frames").get<uint64_t>()) return false;
        const auto status = after.at("direct_game").at("status").get<std::string>();
        if (route.at("active_mode") == 1)
            return status == "Native NR output (resolved) written back to the game DLSS output";
        if (route.at("active_mode") != 2 && route.at("active_mode") != 3) return false;
        const auto& worker = after.at("worker_execution");
        if (worker.at("kind") != "native")
            // The queue fence has retired. This state records actual residual
            // composition; older cores mislabeled its human-readable status.
            return (worker.value("state", std::string()) == "composing_reprojected_result" ||
                    worker.value("state", std::string()) == "presenting_reprojected_result") &&
                worker.value("presented", uint64_t(0)) > before.at("worker_execution").value("presented", uint64_t(0)) &&
                worker.value("delivered", uint64_t(0)) > 0 && worker.value("error", std::string()).empty();
        if (status != "Own network output written back to the game DLSS output") return false;
        return after.at("completed_frames").get<uint64_t>() > before.at("completed_frames").get<uint64_t>();
    } catch (const std::exception&) { return false; }
}
