#include "../source/openxr-layer/openxr_layer.cpp"
#include <filesystem>
#include <iostream>
#include <stdexcept>

int main()
{
    namespace fs = std::filesystem;
    const auto folder = fs::temp_directory_path() / (L"openxr-profile-" + std::to_wstring(GetCurrentProcessId()));
    fs::create_directories(folder);
    const auto ini = folder / L"ReShade.ini";
    OpenXrBridge bridge;
    unsigned checks = 0;
    auto expect = [&](bool result) { ++checks; if (!result) throw std::runtime_error("OpenXR profile contract failed"); };
    bridge.LoadFrameSettings(ini.c_str());
    expect(bridge.FrameMessage(1, 0).iterations == 1);
    for (const auto *value : {L"1", L"7", L"10", L"0", L"-1", L"11", L"invalid"})
    {
        if (!WritePrivateProfileStringW(L"DLSS5_HOST", L"FrameIterations", value, ini.c_str()))
            throw std::runtime_error("Could not write CPU fixture");
        bridge.LoadFrameSettings(ini.c_str());
        const auto expected = std::clamp(_wtoi(value), 1, 10);
        const auto frame = bridge.FrameMessage(42, 1);
        expect(frame.iterations == static_cast<uint32_t>(expected));
        expect(frame.n == 42 && frame.eye_index == 1 && frame.reset == 1 && frame.nr_enabled == 1);
    }
    bridge.frames_done = 2;
    expect(bridge.FrameMessage(43, 0).reset == 0);
    fs::remove(ini);
    fs::remove(folder);
    std::cout << checks << " OpenXR native profile/IPC checks passed; no host, GPU or inference executed\n";
}
