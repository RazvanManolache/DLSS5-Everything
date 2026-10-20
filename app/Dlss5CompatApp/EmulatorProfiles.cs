namespace Dlss5CompatApp;

public sealed record EmulatorRenderer(string Api, string Label);
public sealed record EmulatorProfile(string Key, string Name, string System,
    IReadOnlyList<string> Executables, IReadOnlyList<EmulatorRenderer> Renderers, string Hint);

/// <summary>Renderer selection metadata only; executable recognition does not establish runtime compatibility.</summary>
public static class EmulatorProfiles
{
    public const string SourceRevision = "9fb0b7c3563d350aa46eaa01d3e5ca4cbab4a7cb";
    public const string SourceUrl = "https://github.com/rakanki911/DLSS5-Swapper/blob/9fb0b7c3563d350aa46eaa01d3e5ca4cbab4a7cb/src/core/emulators.js";
    // Metadata ported from DLSS5-Swapper (itself credits DLSS5-Autopilot's MIT detection table).
    // MIT License - Copyright (c) 2026 Rakan Alkhaldi
    // Copyright (c) 2026 DLSS 5 Autopilot contributors
    // Permission is hereby granted, free of charge, to any person obtaining a copy
    // of this software and associated documentation files (the "Software"), to deal
    // in the Software without restriction, including without limitation the rights
    // to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
    // copies of the Software, and to permit persons to whom the Software is
    // furnished to do so, subject to the following conditions:
    // The above copyright notice and this permission notice shall be included in all
    // copies or substantial portions of the Software.
    // THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
    // IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
    // FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
    // AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
    // LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
    // OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
    // SOFTWARE.
    public static IReadOnlyList<EmulatorProfile> All { get; } = Array.AsReadOnly(new[]
    {
        P("duckstation", "DuckStation", "PlayStation 1", ["duckstation-qt-x64.exe", "duckstation-qt-x64-releaseltcg.exe", "duckstation-nogui-x64.exe", "duckstation.exe"], ["dxgi", "vulkan", "opengl"], "Settings > Graphics > Renderer: Direct3D 11/12"),
        P("pcsx2", "PCSX2", "PlayStation 2", ["pcsx2-qt.exe", "pcsx2x64.exe", "pcsx2x64-avx2.exe", "pcsx2.exe"], ["dxgi", "vulkan", "opengl"], "Settings > Graphics > Renderer: Direct3D 11/12"),
        P("dolphin", "Dolphin", "GameCube / Wii", ["dolphin.exe", "dolphinqt.exe"], ["dxgi", "vulkan", "opengl"], "Graphics > Backend: Direct3D 11/12"),
        P("ppsspp", "PPSSPP", "PSP", ["ppssppwindows64.exe", "ppssppwindows.exe"], ["dxgi", "vulkan", "opengl"], "Settings > Graphics > Backend: Direct3D 11"),
        P("xenia", "Xenia", "Xbox 360", ["xenia.exe", "xenia_canary.exe"], ["dxgi", "vulkan"], "Use the Direct3D 12 backend"),
        P("cemu", "Cemu", "Wii U", ["cemu.exe"], ["vulkan", "opengl"], "Options > General settings > Graphics: Vulkan"),
        P("rpcs3", "RPCS3", "PlayStation 3", ["rpcs3.exe"], ["vulkan", "opengl"], "Configuration > GPU > Renderer: Vulkan"),
        P("ryujinx", "Ryujinx", "Nintendo Switch", ["ryujinx.exe", "ryujinx.ava.exe", "ryujinx.headless.sdl2.exe"], ["vulkan", "opengl"], "Settings > Graphics > Backend: Vulkan"),
        P("yuzu", "yuzu / suyu / Eden / Citron", "Nintendo Switch", ["yuzu.exe", "suyu.exe", "eden.exe", "citron.exe", "sudachi.exe"], ["vulkan", "opengl"], "Graphics API: Vulkan"),
        P("shadps4", "shadPS4", "PlayStation 4", ["shadps4.exe"], ["vulkan"], "Vulkan renderer"),
        P("azahar", "Azahar / Citra / Lime3DS", "Nintendo 3DS", ["azahar.exe", "citra.exe", "citra-qt.exe", "lime3ds.exe"], ["vulkan", "opengl"], "Graphics API: Vulkan"),
        P("melonds", "melonDS", "Nintendo DS", ["melonds.exe"], ["opengl"], "OpenGL renderer"),
        P("flycast", "Flycast", "Dreamcast", ["flycast.exe"], ["dxgi", "vulkan", "opengl"], "Video > Renderer: DirectX 11"),
        P("xemu", "xemu", "Xbox", ["xemu.exe"], ["vulkan", "opengl"], "Renderer: Vulkan"),
        P("vita3k", "Vita3K", "PlayStation Vita", ["vita3k.exe"], ["vulkan", "opengl"], "Backend Renderer: Vulkan"),
        P("retroarch", "RetroArch", "Multi-system", ["retroarch.exe"], ["dxgi", "vulkan", "opengl"], "Video driver: d3d11 or d3d12"),
        P("mgba", "mGBA", "Game Boy Advance", ["mgba.exe"], ["opengl"], "OpenGL renderer"),
        P("snes9x", "Snes9x", "SNES", ["snes9x-x64.exe", "snes9x.exe"], ["dxgi"], "Output method: Direct3D"),
        P("play", "Play!", "PlayStation 2", ["play.exe"], ["vulkan", "opengl"], "Renderer: Vulkan")
    });

    static readonly IReadOnlyDictionary<string, EmulatorProfile> ByExecutable = All
        .SelectMany(p => p.Executables.Select(e => (Name: e, Profile: p)))
        .ToDictionary(e => e.Name, e => e.Profile, StringComparer.OrdinalIgnoreCase);

    public static EmulatorProfile? ProfileFor(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;
        var filename = executable.Replace('\\', '/').Split('/')[^1];
        return ByExecutable.GetValueOrDefault(filename);
    }

    public static IReadOnlyList<EmulatorRenderer> ApiChoices(EmulatorProfile? profile) => profile?.Renderers ?? [];

    static EmulatorProfile P(string key, string name, string system, string[] executables, string[] apis, string hint) =>
        new(key, name, system, Array.AsReadOnly(executables), Array.AsReadOnly(apis.Select(api => new EmulatorRenderer(api,
            api switch { "dxgi" => "DirectX 11/12", "vulkan" => "Vulkan", "opengl" => "OpenGL", _ => throw new ArgumentException("Unknown renderer.") })).ToArray()), hint);
}
