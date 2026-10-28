using System.Text;

namespace Dlss5CompatApp;

/// <summary>COM activation can bypass an otherwise correctly installed local DDraw wrapper.</summary>
sealed record LegacyComPlan(bool DirectDraw, bool DirectPlayUserRegistration)
{
    static readonly Guid DirectDrawClass = new("d7b70ee0-4340-11cf-b063-0020afc2cd35");
    static readonly Guid DirectDraw7Class = new("3c305196-50db-11d3-9cfe-00c04fd930c5");
    static readonly Guid DirectPlayLobbyClass = new("2fe8f810-b2a5-11d0-a787-0000f803abfc");

    public static LegacyComPlan? For(string executable, InstallRoute translation)
    {
        if (translation is not (InstallRoute.X86DirectXLegacyViaDgVoodoo or InstallRoute.X64DirectXLegacyViaDgVoodoo)) return null;
        if (new FileInfo(executable).Length > 64 * 1024 * 1024) return null;
        return Detect(File.ReadAllBytes(executable));
    }

    internal static LegacyComPlan? Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IndexOf(Encoding.ASCII.GetBytes("CoCreateInstance")) < 0) return null;
        var draw = bytes.IndexOf(DirectDrawClass.ToByteArray()) >= 0 || bytes.IndexOf(DirectDraw7Class.ToByteArray()) >= 0;
        var lobby = bytes.IndexOf(DirectPlayLobbyClass.ToByteArray()) >= 0;
        return draw || lobby ? new(draw, lobby) : null;
    }
}
