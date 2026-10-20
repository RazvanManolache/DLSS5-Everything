#include "pyrowave-bridge.h"
#include "volk.h"
#include "pyrowave.h"
#include <algorithm>
#include <cstring>
#include <memory>
#include <cstdio>
#include <string>
#include <vector>

namespace {
thread_local const char *create_error = "Pyrowave initialization failed.";
thread_local std::string device_error;
constexpr int MaxRgba = 64 * 1024 * 1024, MaxEncoded = 16 * 1024 * 1024;
constexpr size_t Boundary = 16 * 1024;
struct Context {
    pyrowave_device device = nullptr;
    pyrowave_encoder encoder = nullptr;
    pyrowave_decoder decoder = nullptr;
    int width = 0, height = 0, budget = 0;
    std::vector<uint8_t> planes, bitstream;
    std::vector<pyrowave_packet> packets;
    std::vector<uint16_t> valid_ballots;
    ~Context() {
        if (decoder) pyrowave_decoder_destroy(decoder);
        if (encoder) pyrowave_encoder_destroy(encoder);
        if (device) pyrowave_device_destroy(device);
    }
    pyrowave_cpu_buffer buffer() {
        pyrowave_cpu_buffer b = {}; b.width = width; b.height = height;
        b.format = PYROWAVE_CPU_BUFFER_FORMAT_YUV444P;
        const size_t pixels = size_t(width) * height;
        for (int i = 0; i < 3; ++i) {
            b.data[i] = planes.data() + i * pixels;
            b.row_stride_in_bytes[i] = width; b.plane_size_in_bytes[i] = pixels;
        }
        return b;
    }
};
uint32_t read32(const uint8_t *p) { return uint32_t(p[0]) | uint32_t(p[1]) << 8 | uint32_t(p[2]) << 16 | uint32_t(p[3]) << 24; }
void write32(uint8_t *p, uint32_t v) { for (int i = 0; i < 4; ++i) p[i] = uint8_t(v >> (8 * i)); }
uint8_t clamp(int v) { return uint8_t(std::max(0, std::min(255, v))); }
unsigned population(uint32_t value) { unsigned count = 0; for (; value; value &= value - 1) ++count; return count; }
const char *encoder_error(pyrowave_device device) {
    VkPhysicalDevice gpu=VK_NULL_HANDLE;
    pyrowave_device_get_vk_device_handles(device,nullptr,&gpu,nullptr);
    VkPhysicalDeviceSubgroupSizeControlProperties sizes={VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SUBGROUP_SIZE_CONTROL_PROPERTIES};
    VkPhysicalDeviceSubgroupProperties subgroups={VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SUBGROUP_PROPERTIES,&sizes};
    VkPhysicalDeviceProperties2 props={VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2,&subgroups};
    vkGetPhysicalDeviceProperties2(gpu,&props);
    VkPhysicalDeviceVulkan13Features f13={VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_3_FEATURES};
    VkPhysicalDeviceVulkan12Features f12={VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_2_FEATURES,&f13};
    VkPhysicalDeviceFeatures2 features={VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2,&f12};
    vkGetPhysicalDeviceFeatures2(gpu,&features);
    char text[512];
    std::snprintf(text,sizeof(text),"Pyrowave encoder rejected %s: subgroup ops=0x%x (needs 0x7f), sizes=%u..%u, control=%u, full=%u, compute-stage=%u, int16=%u, storage8=%u.",
        props.properties.deviceName,subgroups.supportedOperations,sizes.minSubgroupSize,sizes.maxSubgroupSize,
        f13.subgroupSizeControl,f13.computeFullSubgroups,!!(sizes.requiredSubgroupSizeStages&VK_SHADER_STAGE_COMPUTE_BIT),
        features.features.shaderInt16,f12.storageBuffer8BitAccess);
    device_error=text;return device_error.c_str();
}
// Validate the published v1 coefficient layout before GPU decode. See upstream
// bitstream.md and Encoder::Impl::validate_bitstream; MIT notices accompany the build.
bool valid_packet(Context &c, const uint8_t *data, size_t size, std::vector<bool> &seen,
                  int &sequence, int &total, int &active) {
    size_t offset = 0;
    while (offset < size) {
        if (size - offset < 8) return false;
        const uint8_t *p = data + offset; uint32_t first = read32(p), second = read32(p + 4);
        int seq = int((first >> 28) & 7);
        if (first & 0x80000000u) {
            if ((first & 0x3fff) + 1 != uint32_t(c.width) || ((first >> 14) & 0x3fff) + 1 != uint32_t(c.height) ||
                ((second >> 24) & 3) != 0 || ((second >> 26) & 1) != 1 || (second & 0xf8000000u) != 0 ||
                (second & 0xffffffu) > c.valid_ballots.size()) return false;
            if (sequence != -1 && (sequence != seq || total != int(second & 0xffffffu))) return false;
            sequence = seq; total = int(second & 0xffffffu); offset += 8; continue;
        }
        uint32_t index = second >> 8, words = (first >> 16) & 0xfff, ballot = first & 0xffff;
        size_t bytes = size_t(words) * 4;
        if (sequence != seq || words < 2 || bytes > size - offset || index >= seen.size() || seen[index] ||
            !ballot || (ballot & ~uint32_t(c.valid_ballots[index])) != 0) return false;
        unsigned count = population(ballot); size_t cursor = 8 + 3 * count;
        if (cursor > bytes) return false;
        unsigned significant = 0;
        for (unsigned i = 0; i < count; ++i) {
            uint32_t code = uint32_t(p[8 + 2*i]) | uint32_t(p[9 + 2*i]) << 8;
            unsigned base = p[8 + 2*count + i] & 15;
            for (unsigned sub = 0; sub < 8; ++sub) {
                unsigned planes = base + ((code >> (2*sub)) & 3), bits = 0;
                if (planes > bytes - cursor) return false;
                for (unsigned plane = 0; plane < planes; ++plane) bits |= p[cursor++];
                significant += population(bits);
            }
        }
        cursor += (significant + 7) / 8;
        if ((cursor + 3) / 4 != words) return false;
        seen[index] = true; ++active; offset += bytes;
    }
    return true;
}
}
void *pw_create(int width, int height, int budget) {
    try {
        create_error = "Invalid Pyrowave dimensions or frame budget.";
        if (width < 1 || height < 1 || width > 16384 || height > 16384 || int64_t(width) * height * 4 > MaxRgba ||
            budget < 4096 || budget > 8 * 1024 * 1024) return nullptr;
        uint32_t major = 0, minor = 0, patch = 0; pyrowave_get_api_version(&major, &minor, &patch);
        create_error = "Unsupported Pyrowave API version.";
        if (major != 1) return nullptr;
        auto c = std::make_unique<Context>(); c->width = width; c->height = height; c->budget = budget;
        int aw = std::max(128, (width + 31) & ~31), ah = std::max(128, (height + 31) & ~31);
        for (int level = 4; level >= 0; --level) for (int component = 0; component < 3; ++component)
            for (int band = level == 4 ? 0 : 1; band < 4; ++band) {
                int bw = (aw >> (level + 1)), bh = (ah >> (level + 1));
                for (int y = 0; y < (bh + 31)/32; ++y) for (int x = 0; x < (bw + 31)/32; ++x) {
                    uint16_t ballot = 0;
                    for (int sy = 0; sy < std::min(4, (bh+7)/8-4*y); ++sy)
                        for (int sx = 0; sx < std::min(4, (bw+7)/8-4*x); ++sx) ballot |= uint16_t(1u << (4*sy+sx));
                    c->valid_ballots.push_back(ballot);
                }
            }
        c->planes.resize(size_t(width) * height * 3);
        create_error = "Pyrowave could not create a Vulkan device. Select Raw or update the graphics driver.";
        if (pyrowave_create_default_device(&c->device) != PYROWAVE_SUCCESS) return nullptr;
        pyrowave_encoder_create_info ei = {}; ei.device = c->device; ei.width = width; ei.height = height;
        ei.chroma = PYROWAVE_CHROMA_SUBSAMPLING_444;
        create_error = "This Vulkan device cannot initialize the Pyrowave encoder. Select Raw.";
        if (pyrowave_encoder_create(&ei, &c->encoder) != PYROWAVE_SUCCESS) {create_error=encoder_error(c->device);return nullptr;}
        pyrowave_decoder_create_info di = {}; di.device = c->device; di.width = width; di.height = height;
        di.chroma = PYROWAVE_CHROMA_SUBSAMPLING_444;
        di.fragment_path = pyrowave_decoder_device_prefers_fragment_path(c->device);
        create_error = "This Vulkan device cannot initialize the Pyrowave decoder. Select Raw.";
        if (pyrowave_decoder_create(&di, &c->decoder) != PYROWAVE_SUCCESS) return nullptr;
        create_error = "";
        return c.release();
    } catch (...) { create_error = "Pyrowave initialization ran out of resources or encountered a native error."; return nullptr; }
}
const char *pw_create_error() { return create_error; }
int pw_encode(void *context, const uint8_t *rgba, int bytes, uint8_t *out, int capacity) {
    try {
        auto *c = static_cast<Context *>(context);
        if (!c || !rgba || !out || bytes != c->width * c->height * 4 || capacity < 4 || capacity > MaxEncoded) return -1;
        const size_t n = size_t(c->width) * c->height;
        // Full-range BT.709, identical on both ends; no chroma subsampling or alpha transport.
        for (size_t i = 0; i < n; ++i) {
            int r = rgba[4*i], g = rgba[4*i+1], b = rgba[4*i+2];
            c->planes[i] = clamp((54*r + 183*g + 19*b + 128) >> 8);
            c->planes[n+i] = clamp(128 + ((-29*r - 99*g + 128*b + 128) >> 8));
            c->planes[2*n+i] = clamp(128 + ((128*r - 116*g - 12*b + 128) >> 8));
        }
        auto buffer = c->buffer(); pyrowave_rate_control rate = { size_t(c->budget) };
        if (pyrowave_encoder_encode_cpu(c->encoder, &buffer, &rate) != PYROWAVE_SUCCESS) return -2;
        size_t count = 0;
        if (pyrowave_encoder_compute_num_packets(c->encoder, Boundary, &count) != PYROWAVE_SUCCESS || !count || count > 1024) return -3;
        c->packets.resize(count); c->bitstream.resize(MaxEncoded);
        if (pyrowave_encoder_packetize(c->encoder, c->packets.data(), Boundary, &count,
                c->bitstream.data(), c->bitstream.size()) != PYROWAVE_SUCCESS || count > c->packets.size()) return -4;
        size_t used = 4; write32(out, uint32_t(count));
        for (size_t i = 0; i < count; ++i) {
            const auto &p = c->packets[i];
            if (!p.size || p.size > Boundary || p.offset > c->bitstream.size() || p.size > c->bitstream.size()-p.offset ||
                used + 4 + p.size > size_t(capacity)) return -5;
            write32(out + used, uint32_t(p.size)); used += 4;
            memcpy(out + used, c->bitstream.data() + p.offset, p.size); used += p.size;
        }
        return int(used);
    } catch (...) { return -6; }
}
int pw_decode(void *context, const uint8_t *in, int bytes, uint8_t *rgba, int rgba_bytes) {
    try {
        auto *c = static_cast<Context *>(context);
        if (!c || !in || !rgba || bytes < 8 || bytes > MaxEncoded || rgba_bytes != c->width * c->height * 4) return -1;
        const uint32_t count = read32(in);
        if (!count || count > 1024) return -2;
        size_t offset = 4;
        std::vector<bool> seen(c->valid_ballots.size()); int sequence = -1, total = -1, active = 0;
        // Validate the whole envelope before passing any packet to the upstream parser.
        for (uint32_t i = 0; i < count; ++i) {
            if (offset + 4 > size_t(bytes)) return -3;
            const auto length = read32(in + offset); offset += 4;
            if (!length || length > Boundary || length % 4 || length > size_t(bytes) - offset) return -3;
            if (!valid_packet(*c, in + offset, length, seen, sequence, total, active)) return -3;
            offset += length;
        }
        if (offset != size_t(bytes) || sequence == -1 || active != total) return -3;
        pyrowave_decoder_clear(c->decoder); offset = 4;
        for (uint32_t i = 0; i < count; ++i) {
            auto length = read32(in + offset); offset += 4;
            if (pyrowave_decoder_push_packet(c->decoder, in + offset, length) != PYROWAVE_SUCCESS) return -4;
            offset += length;
        }
        if (!pyrowave_decoder_decode_is_ready(c->decoder, false)) return -5;
        auto buffer = c->buffer();
        if (pyrowave_decoder_decode_cpu_buffer_synchronous(c->decoder, &buffer) != PYROWAVE_SUCCESS) return -6;
        const size_t n = size_t(c->width) * c->height;
        for (size_t i = 0; i < n; ++i) {
            int y = c->planes[i], u = int(c->planes[n+i])-128, v = int(c->planes[2*n+i])-128;
            rgba[4*i] = clamp(y + ((403*v + 128) >> 8));
            rgba[4*i+1] = clamp(y - ((48*u + 120*v + 128) >> 8));
            rgba[4*i+2] = clamp(y + ((475*u + 128) >> 8)); rgba[4*i+3] = 255;
        }
        return rgba_bytes;
    } catch (...) { return -7; }
}
void pw_destroy(void *context) { try { delete static_cast<Context *>(context); } catch (...) {} }
