#pragma once
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mutex>
#include <atomic>

// Only enumeration happens before Start. Activating the media source opens the camera.
class CameraPlatform {
public:
    CameraPlatform() { check(MFStartup(MF_VERSION, MFSTARTUP_FULL), "Start Windows camera services"); }
    ~CameraPlatform() { MFShutdown(); }
};
inline Json camera_devices() {
    CameraPlatform platform;
    ComPtr<IMFAttributes> attributes;
    check(MFCreateAttributes(&attributes, 1), "Create camera enumeration attributes");
    check(attributes->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID), "Select video cameras");
    IMFActivate** devices = nullptr; UINT32 count = 0;
    check(MFEnumDeviceSources(attributes.Get(), &devices, &count), "Enumerate cameras");
    Json result = Json::array();
    for (UINT32 index = 0; index < count; ++index) {
        wchar_t* name = nullptr; wchar_t* link = nullptr; UINT32 length = 0;
        if (SUCCEEDED(devices[index]->GetAllocatedString(MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, &name, &length)) &&
            SUCCEEDED(devices[index]->GetAllocatedString(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK, &link, &length)))
            result.push_back({{"name", winrt::to_string(name)}, {"id", winrt::to_string(link)}});
        CoTaskMemFree(name); CoTaskMemFree(link); devices[index]->Release();
    }
    CoTaskMemFree(devices); return result;
}

inline ComPtr<IMFMediaSource> open_camera(const std::string& id) {
    if (id.empty() || id.size() > 8192) throw std::runtime_error("Invalid camera identity");
    ComPtr<IMFAttributes> attributes; ComPtr<IMFMediaSource> source;
    check(MFCreateAttributes(&attributes, 2), "Create selected camera attributes");
    check(attributes->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID), "Select camera type");
    check(attributes->SetString(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK, winrt::to_hstring(id).c_str()), "Select camera device");
    check(MFCreateDeviceSource(attributes.Get(), &source), "Open camera. Check Settings > Privacy & security > Camera and close other camera apps");
    return source;
}

inline Json camera_modes(const std::string& id) {
    CameraPlatform platform;
    auto source = open_camera(id);
    try {
        ComPtr<IMFSourceReader> reader;
        check(MFCreateSourceReaderFromMediaSource(source.Get(), nullptr, &reader), "Read available camera modes");
        Json result = Json::array();
        for (DWORD index = 0; index < 1024; ++index) {
            ComPtr<IMFMediaType> type;
            auto status = reader->GetNativeMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), index, &type);
            if (status == MF_E_NO_MORE_TYPES) break;
            check(status, "Read camera mode");
            UINT32 width = 0, height = 0, numerator = 0, denominator = 0;
            MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &width, &height);
            MFGetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, &numerator, &denominator);
            GUID subtype{}; type->GetGUID(MF_MT_SUBTYPE, &subtype);
            result.push_back({{"width",width},{"height",height},{"fps_numerator",numerator},{"fps_denominator",denominator},
                {"subtype",subtype.Data1}});
        }
        reader.Reset(); source->Shutdown(); return result;
    } catch (...) { source->Shutdown(); throw; }
}

// At most one pending frame: slow inference drops old camera frames instead of building latency.
class CameraSamples final : public IMFSourceReaderCallback {
    std::atomic<ULONG> references{1};
    std::mutex gate;
    ComPtr<IMFSample> latest;
    HRESULT failure = S_OK;
    bool ended = false;
    bool completed = false;
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** value) override {
        if (!value) return E_POINTER;
        *value = nullptr;
        if (id == __uuidof(IUnknown) || id == __uuidof(IMFSourceReaderCallback)) { *value = static_cast<IMFSourceReaderCallback*>(this); AddRef(); return S_OK; }
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references; }
    ULONG STDMETHODCALLTYPE Release() override { auto remaining = --references; if (!remaining) delete this; return remaining; }
    HRESULT STDMETHODCALLTYPE OnReadSample(HRESULT status, DWORD, DWORD flags, LONGLONG, IMFSample* sample) override {
        std::lock_guard lock(gate); failure = status;
        completed = true;
        ended = (flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0;
        if (sample) latest = sample;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnEvent(DWORD, IMFMediaEvent*) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE OnFlush(DWORD) override { return S_OK; }
    ComPtr<IMFSample> take(bool& read_completed) {
        std::lock_guard lock(gate);
        check(failure, "Camera read failed. Check Windows camera permissions and whether another app is using it");
        if (ended) throw std::runtime_error("The camera stopped delivering video. Reconnect it and restart.");
        read_completed = completed; completed = false;
        auto frame = latest; latest.Reset(); return frame;
    }
};

class CameraInput {
    CameraPlatform platform;
    ComPtr<IMFMediaSource> source;
    ComPtr<IMFSourceReader> reader;
    ComPtr<CameraSamples> samples;
    LONG default_stride = 0;
    ULONGLONG last_frame = 0;
    bool started = false;
public:
    UINT width = 0, height = 0;
    double rate = 0;
    CameraInput(const std::string& id, UINT requested_width, UINT requested_height, UINT requested_fps) {
        const bool automatic = !requested_width && !requested_height && !requested_fps;
        if (id.empty() || id.size() > 8192 || requested_width > 3840 || requested_height > 2160 ||
            (!automatic && (!requested_width || !requested_height || !requested_fps)) || requested_fps > 120)
            throw std::runtime_error("Invalid camera selection");
        try {
            source = open_camera(id);
            configure(requested_width, requested_height, requested_fps);
        } catch (...) { close(); throw; }
    }
    // The same reader can qualify a generated media source without opening any webcam.
    CameraInput(IMFMediaSource* supplied_source, UINT requested_width, UINT requested_height, UINT requested_fps) : source(supplied_source) {
        try { configure(requested_width, requested_height, requested_fps); }
        catch (...) { close(); throw; }
    }
private:
    void configure(UINT requested_width, UINT requested_height, UINT requested_fps) {
        ComPtr<IMFAttributes> attributes;
            samples.Attach(new CameraSamples());
            check(MFCreateAttributes(&attributes, 3), "Create camera reader attributes");
            check(attributes->SetUnknown(MF_SOURCE_READER_ASYNC_CALLBACK, samples.Get()), "Set camera frame callback");
            check(attributes->SetUINT32(MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, TRUE), "Enable camera color conversion");
            check(attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE), "Enable camera hardware decoding");
            check(MFCreateSourceReaderFromMediaSource(source.Get(), attributes.Get(), &reader), "Create camera reader");
            check(reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS), FALSE), "Disable unused camera streams");
            check(reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), TRUE), "Enable camera video");
            ComPtr<IMFMediaType> chosen;
            const bool automatic = !requested_width && !requested_height && !requested_fps;
            if (automatic) {
                ComPtr<IMFMediaType> current;
                if (SUCCEEDED(reader->GetCurrentMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), &current))) {
                    UINT32 w = 0, h = 0, num = 0, den = 0;
                    MFGetAttributeSize(current.Get(), MF_MT_FRAME_SIZE, &w, &h);
                    MFGetAttributeRatio(current.Get(), MF_MT_FRAME_RATE, &num, &den);
                    if (w && h && w <= 3840 && h <= 2160 && den && num && double(num)/den <= 120) chosen = current;
                }
            }
            for (DWORD index = 0;; ++index) {
                if (chosen) break;
                ComPtr<IMFMediaType> type;
                auto status = reader->GetNativeMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), index, &type);
                if (status == MF_E_NO_MORE_TYPES) break;
                check(status, "Read camera modes");
                UINT32 w = 0, h = 0, numerator = 0, denominator = 0;
                MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &w, &h);
                MFGetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, &numerator, &denominator);
                if (automatic ? w && h && w <= 3840 && h <= 2160 && numerator && denominator && double(numerator)/denominator <= 120 :
                    w == requested_width && h == requested_height && denominator && std::abs(static_cast<double>(numerator) / denominator - requested_fps) < .1)
                    { chosen = type; break; }
            }
            if (!chosen) throw std::runtime_error("This camera does not offer the selected resolution/frame rate. Select Automatic or Check modes to read its supported choices.");
            check(reader->SetCurrentMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), nullptr, chosen.Get()), "Set camera capture mode");
            ComPtr<IMFMediaType> output;
            check(MFCreateMediaType(&output), "Create camera color format");
            check(output->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video), "Set video output");
            check(output->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32), "Select camera RGB conversion");
            check(reader->SetCurrentMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), nullptr, output.Get()), "Convert camera to RGB32");
            check(reader->GetCurrentMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), &output), "Read delivered camera mode");
            check(MFGetAttributeSize(output.Get(), MF_MT_FRAME_SIZE, &width, &height), "Read delivered camera size");
            UINT32 numerator = 0, denominator = 0;
            check(MFGetAttributeRatio(output.Get(), MF_MT_FRAME_RATE, &numerator, &denominator), "Read delivered camera rate");
            rate = denominator ? static_cast<double>(numerator) / denominator : 0;
            if ((!automatic && (width != requested_width || height != requested_height || std::abs(rate - requested_fps) >= .1)) ||
                !width || !height || width > 3840 || height > 2160 || !std::isfinite(rate) || rate <= 0 || rate > 120)
                throw std::runtime_error("Camera changed the requested capture mode. Stop and choose a supported mode.");
            UINT32 stride = 0;
            if (SUCCEEDED(output->GetUINT32(MF_MT_DEFAULT_STRIDE, &stride))) default_stride = static_cast<LONG>(stride);
            else check(MFGetStrideForBitmapInfoHeader(MFVideoFormat_RGB32.Data1, width, &default_stride), "Read camera row stride");
            last_frame = GetTickCount64();
    }
public:
    ~CameraInput() { close(); }
    void close() {
        // Shutdown releases device ownership and also interrupts pending reads on unplug/Stop.
        if (source) source->Shutdown();
        reader.Reset(); source.Reset(); samples.Reset();
    }
    void request() {
        check(reader->ReadSample(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), 0, nullptr, nullptr, nullptr, nullptr), "Request camera frame");
    }
    bool frame(std::vector<BYTE>& pixels) {
        // Model/host setup may take seconds. Start the camera clock and first asynchronous
        // read only when the consumer is ready, rather than leaving a pending read in setup.
        if (!started) { started = true; last_frame = GetTickCount64(); request(); }
        bool completed = false;
        auto sample = samples->take(completed);
        if (!sample) {
            if (completed) request(); // A stream tick is not a frame or an end-of-stream.
            if (GetTickCount64() - last_frame > 10000) throw std::runtime_error("No camera frames for 10 seconds. Check permissions, unplugged devices or another camera app.");
            return false;
        }
        last_frame = GetTickCount64();
        ComPtr<IMFMediaBuffer> buffer; check(sample->ConvertToContiguousBuffer(&buffer), "Read camera frame buffer");
        ComPtr<IMF2DBuffer> two_dimensional;
        BYTE* first = nullptr; LONG stride = default_stride; DWORD bytes = 0;
        bool locked2d = SUCCEEDED(buffer.As(&two_dimensional));
        if (locked2d) check(two_dimensional->Lock2D(&first, &stride), "Lock camera rows");
        else {
            check(buffer->Lock(&first, nullptr, &bytes), "Lock camera pixels");
            if (static_cast<uint64_t>(std::abs(stride)) * height > bytes) { buffer->Unlock(); throw std::runtime_error("Camera frame is truncated"); }
            if (stride < 0) first += static_cast<size_t>(-stride) * (height - 1);
        }
        if (std::abs(stride) < static_cast<LONG>(width * 4)) {
            if (locked2d) two_dimensional->Unlock2D(); else buffer->Unlock();
            throw std::runtime_error("Camera frame row is truncated");
        }
        pixels.resize(static_cast<size_t>(width) * height * 4);
        for (UINT row = 0; row < height; ++row)
            memcpy(pixels.data() + static_cast<size_t>(row) * width * 4, first + static_cast<ptrdiff_t>(row) * stride, width * 4);
        if (locked2d) two_dimensional->Unlock2D(); else buffer->Unlock();
        request(); return true;
    }
};
