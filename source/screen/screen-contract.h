#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <stdexcept>
#include <string>

namespace screen {
struct Options {
    int schema = 1;
    uint64_t revision = 0;
    bool enabled = true;
    int iterations = 1;
    int fps = 60;
    float scale = 1;
    int comparison = 0; // enhanced, input, wipe, difference
    float divider = .5f;
    float gain = 4;
    bool residual = true;
    float residual_strength = 1;
    bool lens = false;
    float fov = 110;
    float vignette = 0;
    float aberration = 0;
    float noise = 0;
    int screenshot = 0;
    bool hdr = false, spout = false, virtual_camera = false;
    float paper_white = 203;
    std::string spout_name = "DLSS5 Everything";
    void validate() const {
        if (virtual_camera && hdr) throw std::runtime_error("Virtual webcam output requires SDR");
        if (schema != 1 || iterations < 1 || iterations > 10 || fps < 0 || fps > 240 ||
            !std::isfinite(scale) || scale < .25f || scale > 1 || comparison < 0 || comparison > 3 ||
            !std::isfinite(divider) || divider < 0 || divider > 1 ||
            !std::isfinite(gain) || gain < 1 || gain > 64 ||
            !std::isfinite(residual_strength) || residual_strength < 0 || residual_strength > 2 ||
            !std::isfinite(fov) || fov < 30 || fov > 160 ||
            !std::isfinite(vignette) || vignette < 0 || vignette > 1 ||
            !std::isfinite(aberration) || aberration < 0 || aberration > 1 ||
            !std::isfinite(noise) || noise < 0 || noise > 1 || screenshot < 0 ||
            !std::isfinite(paper_white) || paper_white < 80 || paper_white > 400 ||
            spout_name.empty() || spout_name.size()>100 || std::any_of(spout_name.begin(),spout_name.end(),[](unsigned char c){return c<32 || c>126;}))
            throw std::runtime_error("Invalid Screen Studio settings");
    }
};
struct Size { uint32_t width, height; };
inline Size native_canvas(Size image) {
    if (!image.width || !image.height || image.width>16384 || image.height>16384)
        throw std::runtime_error("Phoenix DLSS5 processing dimensions must be between 1 and 16384 per axis.");
    return image;
}
inline Size processing_size(uint32_t width, uint32_t height, float scale) {
    if (!width || !height || width > 16384 || height > 16384 ||
        !std::isfinite(scale) || scale < .25f || scale > 1)
        throw std::runtime_error("Invalid screen dimensions or scale");
    // Direct Phoenix is always 1:1 at its processing size, never an SR hook.
    return { std::max(1u, static_cast<uint32_t>(std::lround(width * scale))),
             std::max(1u, static_cast<uint32_t>(std::lround(height * scale))) };
}
}
