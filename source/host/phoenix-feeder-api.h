#pragma once
#include <d3d12.h>
#include <cstdint>
#include <cmath>

// Borrowed D3D12 objects; the caller submits the observed list and waits for its
// fence. No NGX feature or NVIDIA SR evaluation is involved in this interface.
inline constexpr uint32_t PhoenixFeederAbiV1 = 1;
enum class PhoenixFeederStatusV1 : uint32_t { Deferred = 0, Recorded = 1, Bypass = 2 };
struct PhoenixFeederFrameV1 {
    uint32_t size = sizeof(PhoenixFeederFrameV1);
    uint32_t version = PhoenixFeederAbiV1;
    ID3D12GraphicsCommandList* command_list = nullptr;
    ID3D12CommandQueue* queue = nullptr;
    // The host seeds output with the original frame. Phoenix processes it in
    // place. All resources enter/leave in COMMON with fresh observed barriers.
    ID3D12Resource* output = nullptr;
    ID3D12Resource* motion = nullptr;
    ID3D12Resource* depth = nullptr;
    uint32_t reset = 0;
    uint32_t stream_id = 0; // 0 mono, 1 left eye, 2 right eye
    float motion_scale_x = 1.0f;
    float motion_scale_y = 1.0f;
    uint32_t depth_inverted = 0;
    uint32_t reserved = 0;
};
struct PhoenixFeederResultV1 {
    uint32_t size = sizeof(PhoenixFeederResultV1);
    uint32_t version = PhoenixFeederAbiV1;
    PhoenixFeederStatusV1 status = PhoenixFeederStatusV1::Deferred;
    uint32_t reserved = 0;
    uint64_t recorded_frames = 0;
};
inline bool PhoenixFeederContractV1(const PhoenixFeederFrameV1& frame,
                                   const PhoenixFeederResultV1& result) noexcept {
    return frame.size == sizeof(frame) && frame.version == PhoenixFeederAbiV1 &&
           result.size == sizeof(result) && result.version == PhoenixFeederAbiV1 &&
           frame.command_list && frame.queue && frame.output && frame.motion &&
           frame.output != frame.motion && frame.output != frame.depth &&
           (!frame.depth || frame.depth != frame.motion) &&
           frame.stream_id <= 2 && frame.reset <= 1 && frame.depth_inverted <= 1 &&
           frame.reserved == 0 && result.reserved == 0 &&
           std::isfinite(frame.motion_scale_x) && std::isfinite(frame.motion_scale_y);
}
using PhoenixFeederVersionProc = uint32_t(WINAPI*)();
using PhoenixFeederEvaluateProc = HRESULT(WINAPI*)(const PhoenixFeederFrameV1*, PhoenixFeederResultV1*);
// Optional immutable-capture extension; the V1 layout and ABI stay unchanged.
using PhoenixFeederEvaluateLockedProc = HRESULT(WINAPI*)(const PhoenixFeederFrameV1*, PhoenixFeederResultV1*, uint64_t);
using PhoenixFeederProgressJsonProc = uint32_t(WINAPI*)(char*, uint32_t);
