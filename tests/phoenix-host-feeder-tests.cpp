// Exercise the production ABI loader, reply gate and exception boundary on CPU.
#define main PhoenixHostProgramMain
#include "../source/host/dlss5-feed-host64.cpp"
#undef main
#include <iostream>
#include <limits>
#include <cstddef>

static HRESULT WINAPI FaultingExport(const PhoenixFeederFrameV1*, PhoenixFeederResultV1*)
{
    RaiseException(EXCEPTION_ACCESS_VIOLATION, 0, 0, nullptr);
    return E_FAIL;
}

int wmain(int argc, wchar_t **argv)
{
    try {
        static_assert(sizeof(PhoenixFeederFrameV1) == 72, "x64 feeder frame layout changed");
        static_assert(sizeof(PhoenixFeederResultV1) == 24, "x64 feeder result layout changed");
        static_assert(offsetof(PhoenixFeederFrameV1, queue) == 16, "native queue layout changed");
        static_assert(offsetof(PhoenixFeederFrameV1, stream_id) == 52, "stream layout changed");
        strcpy_s(g_log_path, "NUL");
        unsigned checks = 0;
        auto expect = [&](bool condition) {
            ++checks;
            if (!condition) throw std::runtime_error("Direct feeder CPU check " + std::to_string(checks) + " failed");
        };
        expect(argc == 5);
        PhoenixFeederFrameV1 frame;
        PhoenixFeederResultV1 result;
        expect(!PhoenixFeederContractV1(frame, result));
        // Non-null tokens are never dereferenced by the CPU-only contract/fixture.
        frame.command_list = reinterpret_cast<ID3D12GraphicsCommandList*>(uintptr_t(1));
        frame.queue = reinterpret_cast<ID3D12CommandQueue*>(uintptr_t(2));
        frame.output = reinterpret_cast<ID3D12Resource*>(uintptr_t(3));
        frame.motion = reinterpret_cast<ID3D12Resource*>(uintptr_t(4));
        expect(PhoenixFeederContractV1(frame, result));
        frame.depth = reinterpret_cast<ID3D12Resource*>(uintptr_t(5));
        expect(PhoenixFeederContractV1(frame, result));
        for (uint32_t stream = 0; stream <= 3; ++stream) {
            auto changed = frame; changed.stream_id = stream;
            expect(PhoenixFeederContractV1(changed, result) == (stream <= 2));
        }
        for (uint32_t bit = 0; bit <= 2; ++bit) {
            auto changed = frame; changed.reset = bit;
            expect(PhoenixFeederContractV1(changed, result) == (bit <= 1));
            changed = frame; changed.depth_inverted = bit;
            expect(PhoenixFeederContractV1(changed, result) == (bit <= 1));
        }
        auto bad = frame; bad.queue = nullptr; expect(!PhoenixFeederContractV1(bad, result));
        bad = frame; bad.command_list = nullptr; expect(!PhoenixFeederContractV1(bad, result));
        bad = frame; bad.output = nullptr; expect(!PhoenixFeederContractV1(bad, result));
        bad = frame; bad.motion = nullptr; expect(!PhoenixFeederContractV1(bad, result));
        bad = frame; bad.motion = bad.output; expect(!PhoenixFeederContractV1(bad, result));
        bad = frame; bad.depth = bad.output; expect(!PhoenixFeederContractV1(bad, result));
        bad = frame; bad.depth = bad.motion; expect(!PhoenixFeederContractV1(bad, result));
        bad = frame; bad.size--; expect(!PhoenixFeederContractV1(bad, result));
        bad = frame; bad.version++; expect(!PhoenixFeederContractV1(bad, result));
        bad = frame; bad.reserved = 1; expect(!PhoenixFeederContractV1(bad, result));
        for (float scale : {std::numeric_limits<float>::infinity(), std::numeric_limits<float>::quiet_NaN()}) {
            bad = frame; bad.motion_scale_x = scale; expect(!PhoenixFeederContractV1(bad, result));
            bad = frame; bad.motion_scale_y = scale; expect(!PhoenixFeederContractV1(bad, result));
        }
        auto bad_result = result; bad_result.size--; expect(!PhoenixFeederContractV1(frame, bad_result));
        bad_result = result; bad_result.version++; expect(!PhoenixFeederContractV1(frame, bad_result));
        bad_result = result; bad_result.reserved = 1; expect(!PhoenixFeederContractV1(frame, bad_result));
        for (auto hr : {S_OK, S_FALSE, HRESULT(2), E_FAIL, E_INVALIDARG}) {
            for (auto status : {PhoenixFeederStatusV1::Deferred, PhoenixFeederStatusV1::Recorded,
                                PhoenixFeederStatusV1::Bypass, static_cast<PhoenixFeederStatusV1>(3)}) {
                auto reply = result; reply.status = status;
                const bool allowed = (hr == S_FALSE && status == PhoenixFeederStatusV1::Deferred) ||
                    (hr == S_OK && (status == PhoenixFeederStatusV1::Recorded || status == PhoenixFeederStatusV1::Bypass));
                expect(ValidFeederReply(hr, frame, reply) == allowed);
            }
        }
        expect(!ResolvePhoenixFeeder(nullptr));
        expect(!g_feeder_evaluate);
        for (int i = 1; i < argc; ++i) {
            auto module = LoadLibraryW(argv[i]);
            expect(module != nullptr);
            expect(ResolvePhoenixFeeder(module) == (i == 1));
            if (i == 1) {
                DWORD exception = 0;
                expect(SafeEvaluatePhoenix(&frame, &result, &exception) == S_OK);
                expect(exception == 0 && result.recorded_frames == 1);
                expect(ValidFeederReply(S_OK, frame, result));
            } else {
                expect(g_feeder_evaluate == nullptr);
            }
            g_feeder_evaluate = nullptr;
            FreeLibrary(module);
        }
        g_feeder_evaluate = FaultingExport;
        DWORD exception = 0;
        expect(SafeEvaluatePhoenix(&frame, &result, &exception) == E_UNEXPECTED);
        expect(exception == EXCEPTION_ACCESS_VIOLATION);
        g_feeder_evaluate = nullptr;
        LogFeederModules("CPU ABI tests");
        expect(GetModuleHandleW(L"nvngx_dlss.dll") == nullptr);
        std::cout << checks << " Phoenix DLSS5 direct feeder CPU ABI checks passed; no GPU/inference executed\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
