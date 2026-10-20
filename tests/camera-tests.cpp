#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <wrl/client.h>
#include <winrt/Windows.Foundation.h>
#include <nlohmann/json.hpp>
#include <iostream>
#include <fstream>
#include <thread>
#include <atomic>
#include <vector>
using Microsoft::WRL::ComPtr;
using Json = nlohmann::json;
static void check(HRESULT value, const char* action) { if (FAILED(value)) throw std::runtime_error(std::string(action) + " HRESULT=" + std::to_string(static_cast<unsigned>(value))); }
#include "../source/screen/camera-input.h"
#include "../source/screen/virtual-camera-output.h"
static int checks = 0;
static void require(bool value, const char* reason) { if (!value) throw std::runtime_error(reason); ++checks; }

int main(int argc, char** argv) {
    try {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
        BYTE pixels[32]{}; BYTE nv12[6]{};
        for (int row = 0; row < 2; ++row) for (int col = 0; col < 2; ++col) { auto* pixel = pixels + row * 16 + col * 4; pixel[0] = 255; pixel[3] = 255; }
        camera_output::rgba_nv12(pixels, 16, 2, 2, nv12);
        require(nv12[0] == 82 && nv12[3] == 82, "BT.601 red luma or padded row handling");
        require(nv12[4] == 90 && nv12[5] == 240, "RGBA red/blue channel order or NV12 U/V order");
        memset(pixels, 255, sizeof(pixels)); camera_output::rgba_nv12(pixels,16,2,2,nv12);
        require(nv12[0] == 235 && nv12[4] == 128 && nv12[5] == 128, "White must be limited range neutral");
        memset(pixels, 0, sizeof(pixels)); camera_output::rgba_nv12(pixels,16,2,2,nv12);
        require(nv12[0] == 16 && nv12[4] == 128 && nv12[5] == 128, "Black must be limited range neutral");
        {
            CameraPlatform platform; ComPtr<CameraSamples> callbacks; callbacks.Attach(new CameraSamples());
            ComPtr<IMFSample> sample; check(MFCreateSample(&sample), "Create synthetic camera sample");
            callbacks->OnReadSample(S_OK,0,0,0,sample.Get()); bool completed=false;
            require(callbacks->take(completed).Get()==sample.Get() && completed,"Camera callback must deliver the acquired sample");
            require(!callbacks->take(completed) && !completed,"Consumed samples must not be replayed as new frames");
            callbacks->OnReadSample(S_OK,0,MF_SOURCE_READERF_STREAMTICK,0,nullptr);
            require(!callbacks->take(completed) && completed,"An empty camera stream tick must allow another asynchronous read");
            callbacks->OnReadSample(E_ACCESSDENIED,0,0,0,nullptr); bool denied=false;
            try { callbacks->take(completed); } catch (...) { denied=true; }
            require(denied,"Camera permission failures must be reported instead of waiting forever");
        }
        camera_output::VirtualCamera writer;
        try { writer.start(641,480,30); require(false,"Odd output width accepted"); } catch (const std::runtime_error&) { ++checks; }
        if (argc > 2 && std::string(argv[1]) == "--list-selected-modes") {
            std::ifstream stream(argv[2]); auto selection = Json::parse(stream).at("Camera");
            std::cout << camera_modes(selection.at("DeviceId")).dump(2) << '\n'; return 0;
        }
        if (argc > 2 && (std::string(argv[1]) == "--probe-selected-camera" || std::string(argv[1]) == "--probe-selected-camera-gpu")) {
            ComPtr<ID3D11Device> gpu; ComPtr<ID3D11DeviceContext> context;
            if (std::string(argv[1]) == "--probe-selected-camera-gpu") {
                D3D_FEATURE_LEVEL level;
                check(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&gpu,&level,&context),"Create probe GPU");
            }
            std::ifstream stream(argv[2]); auto selection = Json::parse(stream).at("Camera");
            CameraInput input(selection.at("DeviceId"), selection.at("Width"), selection.at("Height"), selection.at("Fps"));
            std::vector<BYTE> frame; unsigned received = 0;
            const auto deadline = GetTickCount64() + 8000;
            while (received < 5 && GetTickCount64() < deadline) { if (input.frame(frame)) ++received; Sleep(3); }
            require(received == 5, "Selected camera delivered no five-frame capture");
            std::cout << Json({{"frames",received},{"width",input.width},{"height",input.height},{"fps",input.rate},{"images_saved",false}}).dump() << '\n';
            input.close(); return 0;
        }
        if (argc > 2 && std::string(argv[1]) == "--read-generated-video") {
            CameraPlatform platform;
            ComPtr<IMFSourceResolver> resolver; check(MFCreateSourceResolver(&resolver), "Create synthetic media resolver");
            ComPtr<IUnknown> object; MF_OBJECT_TYPE kind{};
            check(resolver->CreateObjectFromURL(winrt::to_hstring(argv[2]).c_str(), MF_RESOLUTION_MEDIASOURCE, nullptr, &kind, &object), "Open generated test clip");
            ComPtr<IMFMediaSource> source; check(object.As(&source), "Use generated test source");
            CameraInput input(source.Get(), 1280, 720, 30);
            std::vector<BYTE> frame; unsigned received = 0;
            const auto deadline=GetTickCount64()+8000;
            while(received<5 && GetTickCount64()<deadline) {
                if(input.frame(frame)) {
                    ++received;
                    require(frame.size()==1280*720*4,"Delivered camera reader buffer size");
                    // Input fixture has red on the top and blue on the bottom: also proves orientation.
                    auto top=(100*1280+640)*4, bottom=(620*1280+640)*4;
                    require(frame[top+2]>210 && frame[top]<30,"Camera RGB32 red/blue order or vertical orientation");
                    require(frame[bottom]>210 && frame[bottom+2]<30,"Bottom camera row was not BLUE");
                }
                Sleep(3);
            }
            require(received==5,"Five generated frames must pass through the same asynchronous camera reader");
            input.close();
        }
        if (argc > 1 && (std::string(argv[1]) == "--virtual-output" || std::string(argv[1]) == "--serve-fixture")) {
            // Explicit synthetic fixture: never opens a physical webcam.
            ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context; D3D_FEATURE_LEVEL level;
            check(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,&level,&context),"Create synthetic camera GPU");
            std::vector<BYTE> red(1280*720*4);
            for (size_t pixel=0;pixel<red.size();pixel+=4) { red[pixel]=255; red[pixel+3]=255; }
            D3D11_TEXTURE2D_DESC desc{};desc.Width=1280;desc.Height=720;desc.MipLevels=desc.ArraySize=desc.SampleDesc.Count=1;desc.Format=DXGI_FORMAT_R8G8B8A8_UNORM;
            D3D11_SUBRESOURCE_DATA data{red.data(),1280*4,0}; ComPtr<ID3D11Texture2D> texture;
            check(device->CreateTexture2D(&desc,&data,&texture),"Create synthetic red frame");
            writer.send(device.Get(),context.Get(),texture.Get(),30);
            HANDLE map=OpenFileMappingW(FILE_MAP_READ,FALSE,L"OBSVirtualCamVideo"); require(map!=nullptr,"Published queue missing");
            auto* queue=static_cast<const camera_output::QueueHeader*>(MapViewOfFile(map,FILE_MAP_READ,0,0,0));
            require(queue && queue->state==2 && queue->width==1280 && queue->height==720,"Shared queue descriptor");
            UnmapViewOfFile(queue);CloseHandle(map);
            camera_output::VirtualCamera duplicate;bool rejected=false;
            try { duplicate.start(1280,720,30); } catch (...) { rejected=true; }
            require(rejected,"Must not overwrite another virtual camera sender");
            if (std::string(argv[1]) == "--serve-fixture") {
                std::cout << "Synthetic virtual camera ready" << std::endl;
                const auto deadline=GetTickCount64()+30000;
                while(GetTickCount64()<deadline) { writer.send(device.Get(),context.Get(),texture.Get(),30);Sleep(30); }
            }
            writer.stop();require(!writer.active(),"Stop must release output mapping");
        }
        std::cout<<checks<<" camera checks passed. Physical camera not opened.\n";return 0;
    } catch(const std::exception& error) { std::cerr<<error.what()<<'\n';return 1; }
}
