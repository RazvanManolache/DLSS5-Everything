#include "../source/screen/screen-contract.h"
#include <iostream>
#include <limits>

int main() {
    unsigned checks = 0;
    auto require = [&](bool ok) { ++checks; if (!ok) throw std::runtime_error("Screen contract failed"); };
    auto rejects = [&](auto action) { bool caught = false; try { action(); } catch (const std::exception&) { caught = true; } require(caught); };
    screen::Options options; options.validate(); ++checks;
    auto size = screen::processing_size(2560, 1080, 1);
    require(size.width == 2560 && size.height == 1080);
    size = screen::processing_size(2560, 1080, .5f);
    require(size.width == 1280 && size.height == 540);
    auto canvas=screen::native_canvas({482,332}); require(canvas.width==482&&canvas.height==332);
    canvas=screen::native_canvas({1280,540}); require(canvas.width==1280&&canvas.height==540);
    canvas=screen::native_canvas({2560,1080}); require(canvas.width==2560&&canvas.height==1080);
    for (const auto extent : {screen::Size{1,1},screen::Size{257,255},screen::Size{2560,1440},
                             screen::Size{1080,1920},screen::Size{3840,2160},screen::Size{16384,16384}}) {
        canvas=screen::native_canvas(extent); require(canvas.width==extent.width&&canvas.height==extent.height);
    }
    rejects([&] { screen::native_canvas({0,1440}); });
    rejects([&] { screen::native_canvas({16385,1440}); });
    for (int value : {0, 11, -1}) rejects([&] { auto o = options; o.iterations = value; o.validate(); });
    for (int value : {-1, 241}) rejects([&] { auto o = options; o.fps = value; o.validate(); });
    for (float value : {0.f, .24f, 1.01f, std::numeric_limits<float>::quiet_NaN()}) {
        rejects([&] { auto o = options; o.scale = value; o.validate(); });
        rejects([&] { screen::processing_size(1920, 1080, value); });
    }
    for (int value : {-1, 4}) rejects([&] { auto o = options; o.comparison = value; o.validate(); });
    rejects([&] { screen::processing_size(0, 1080, 1); });
    rejects([&] { screen::processing_size(16385, 1080, 1); });
    rejects([&] { auto o = options; o.screenshot = -1; o.validate(); });
    rejects([&] { auto o = options; o.hdr = true; o.virtual_camera = true; o.validate(); });
    for(float value : {79.f,401.f,std::numeric_limits<float>::quiet_NaN()}) rejects([&] { auto o=options; o.paper_white=value; o.validate(); });
    for(auto name : {std::string(), std::string("bad\nname"), std::string(101,'x'),std::string("bad\0name",8)}) rejects([&] { auto o=options; o.spout_name=name; o.validate(); });
    std::cout << checks << " Screen Studio CPU contract checks passed. No inference test.\n";
}
