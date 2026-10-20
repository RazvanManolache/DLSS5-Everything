#pragma once
#include <algorithm>
#include <cstdint>
#include <vector>

// Interoperability with the separately installed OBS Virtual Camera DirectShow filter.
// The public wire layout is documented in docs/camera-studio.md. No OBS binary is bundled.
namespace camera_output {
struct alignas(8) QueueHeader {
    volatile LONG writing = 0, published = 0, state = 1;
    uint32_t slots[3]{};
    uint32_t type = 0, width = 0, height = 0;
    uint64_t interval = 0;
    uint32_t reserved[8]{};
};
static_assert(sizeof(QueueHeader) == 80 && offsetof(QueueHeader, interval) == 40);
inline uint32_t aligned(uint32_t size) { return (size + 31u) & ~31u; }
inline BYTE channel(int value) { return static_cast<BYTE>(std::clamp(value, 0, 255)); }

// Full RGB -> limited-range BT.601 NV12. Chroma averages each 2x2 pixel group.
// Both stride and channel order are explicit: the compositor outputs RGBA, MF input is BGRA.
inline void rgba_nv12(const BYTE* rgba, UINT pitch, UINT width, UINT height, BYTE* output) {
    for (UINT row = 0; row < height; ++row) {
        const BYTE* pixel = rgba + static_cast<size_t>(row) * pitch;
        BYTE* luma = output + static_cast<size_t>(row) * width;
        for (UINT col = 0; col < width; ++col, pixel += 4)
            luma[col] = channel(((66 * pixel[0] + 129 * pixel[1] + 25 * pixel[2] + 128) >> 8) + 16);
    }
    BYTE* chroma = output + static_cast<size_t>(width) * height;
    for (UINT row = 0; row < height; row += 2) {
        for (UINT col = 0; col < width; col += 2) {
            int red = 0, green = 0, blue = 0;
            for (UINT dy = 0; dy < 2; ++dy) for (UINT dx = 0; dx < 2; ++dx) {
                const BYTE* pixel = rgba + static_cast<size_t>(row + dy) * pitch + (col + dx) * 4;
                red += pixel[0]; green += pixel[1]; blue += pixel[2];
            }
            red = (red + 2) / 4; green = (green + 2) / 4; blue = (blue + 2) / 4;
            chroma[static_cast<size_t>(row / 2) * width + col] = channel(((-38 * red - 74 * green + 112 * blue + 128) >> 8) + 128);
            chroma[static_cast<size_t>(row / 2) * width + col + 1] = channel(((112 * red - 94 * green - 18 * blue + 128) >> 8) + 128);
        }
    }
}

class VirtualCamera {
    HANDLE mapping = nullptr;
    QueueHeader* queue = nullptr;
    ComPtr<ID3D11Texture2D> staging;
    UINT width = 0, height = 0;
    uint64_t interval = 0;
public:
    ~VirtualCamera() { stop(); }
    bool active() const { return queue != nullptr; }
    void stop() {
        if (queue) { InterlockedExchange(&queue->state, 3); UnmapViewOfFile(queue); queue = nullptr; }
        if (mapping) { CloseHandle(mapping); mapping = nullptr; }
        staging.Reset(); width = height = 0;
    }
    void start(UINT w, UINT h, UINT fps) {
        if ((w & 1) || (h & 1) || !w || !h || w > 3840 || h > 2160)
            throw std::runtime_error("Virtual camera requires an even SDR size up to 3840 x 2160.");
        interval = 10000000ull / (fps ? fps : 30);
        const uint32_t frame_bytes = w * h * 3 / 2;
        QueueHeader layout; layout.width = w; layout.height = h; layout.interval = interval;
        uint32_t total = aligned(sizeof(QueueHeader));
        for (auto& slot : layout.slots) { slot = total; total = aligned(total + 32 + frame_bytes); }
        // Create-and-check is atomic. Never overwrite an OBS or another Phoenix writer.
        mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, total, L"OBSVirtualCamVideo");
        if (!mapping) throw std::runtime_error("Cannot create virtual-camera output");
        if (GetLastError() == ERROR_ALREADY_EXISTS) { CloseHandle(mapping); mapping = nullptr; throw std::runtime_error("OBS Virtual Camera is already in use. Stop its other sender before enabling this output."); }
        queue = static_cast<QueueHeader*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, total));
        if (!queue) { stop(); throw std::runtime_error("Cannot map virtual-camera output"); }
        memcpy(queue, &layout, sizeof(layout)); width = w; height = h;
    }
    void send(ID3D11Device* device, ID3D11DeviceContext* context, ID3D11Texture2D* texture, UINT fps) {
        D3D11_TEXTURE2D_DESC desc{}; texture->GetDesc(&desc);
        if (desc.Format != DXGI_FORMAT_R8G8B8A8_UNORM) throw std::runtime_error("Virtual camera supports SDR RGBA output only");
        const auto requested_interval = 10000000ull / (fps ? fps : 30);
        if (active() && (width != desc.Width || height != desc.Height || interval != requested_interval))
            throw std::runtime_error("Stop and restart the camera before changing its virtual output size/frame rate.");
        if (!active()) start(desc.Width, desc.Height, fps);
        if (!staging) {
            desc.Usage = D3D11_USAGE_STAGING; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ; desc.BindFlags = desc.MiscFlags = 0;
            check(device->CreateTexture2D(&desc, nullptr, &staging), "Allocate virtual camera readback");
        }
        context->CopyResource(staging.Get(), texture);
        D3D11_MAPPED_SUBRESOURCE pixels{};
        check(context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &pixels), "Read enhanced virtual camera frame");
        const auto serial = static_cast<uint32_t>(queue->writing) + 1;
        BYTE* slot = reinterpret_cast<BYTE*>(queue) + queue->slots[serial % 3];
        LARGE_INTEGER ticks{}, frequency{}; QueryPerformanceCounter(&ticks); QueryPerformanceFrequency(&frequency);
        *reinterpret_cast<uint64_t*>(slot) = static_cast<uint64_t>(static_cast<long double>(ticks.QuadPart) * 1000000000.0L / frequency.QuadPart);
        rgba_nv12(static_cast<const BYTE*>(pixels.pData), pixels.RowPitch, width, height, slot + 32);
        context->Unmap(staging.Get(), 0);
        InterlockedExchange(&queue->writing, static_cast<LONG>(serial));
        InterlockedExchange(&queue->published, static_cast<LONG>(serial));
        InterlockedExchange(&queue->state, 2);
    }
};
}
