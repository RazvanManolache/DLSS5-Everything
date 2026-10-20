#pragma once
#include <DirectXPackedVector.h>
#include <cmath>
#include <cstdint>
#include <vector>
#include <dxgiformat.h>

enum class PhoenixColorRoute { Unsupported, DirectFloat, AdaptBytes };
inline bool PhoenixByteFormat(DXGI_FORMAT format) {
    return format == DXGI_FORMAT_R8G8B8A8_UNORM || format == DXGI_FORMAT_B8G8R8A8_UNORM;
}
inline bool PhoenixFloatFormat(DXGI_FORMAT format) {
    return format == DXGI_FORMAT_R16G16B16A16_FLOAT || format == DXGI_FORMAT_R11G11B10_FLOAT;
}
inline PhoenixColorRoute PhoenixHostColorRoute(DXGI_FORMAT input, DXGI_FORMAT output) {
    if (!PhoenixByteFormat(input) && !PhoenixFloatFormat(input)) return PhoenixColorRoute::Unsupported;
    if (PhoenixFloatFormat(output)) return PhoenixColorRoute::DirectFloat;
    return PhoenixByteFormat(output) ? PhoenixColorRoute::AdaptBytes : PhoenixColorRoute::Unsupported;
}
inline bool PhoenixHostDirectColorPath(DXGI_FORMAT input, DXGI_FORMAT output, bool processing) {
    const auto route = PhoenixHostColorRoute(input, output);
    return route != PhoenixColorRoute::Unsupported &&
           (processing ? route == PhoenixColorRoute::DirectFloat : input == output);
}
inline uint32_t PhoenixByteRowPitch(uint32_t width) { return (width * 4u + 255u) & ~255u; }
inline uint32_t PhoenixPackPixel(const uint8_t* rgba, bool bgra) {
    return uint32_t(rgba[bgra ? 2 : 0]) | (uint32_t(rgba[1]) << 8) |
           (uint32_t(rgba[bgra ? 0 : 2]) << 16) | (uint32_t(rgba[3]) << 24);
}

// Preserve SDR code values; Phoenix's configured transfer handles the color domain.
inline std::vector<uint16_t> PhoenixProbeHalfPixels(const std::vector<uint8_t>& rgba) {
    std::vector<uint16_t> result(rgba.size());
    for (size_t i = 0; i < rgba.size(); ++i)
        result[i] = DirectX::PackedVector::XMConvertFloatToHalf(rgba[i] / 255.0f);
    return result;
}

inline uint8_t PhoenixProbeByte(uint16_t half) {
    const float value = DirectX::PackedVector::XMConvertHalfToFloat(half);
    if (!std::isfinite(value) || value <= 0.0f) return 0;
    if (value >= 1.0f) return 255;
    return static_cast<uint8_t>(value * 255.0f + 0.5f);
}
