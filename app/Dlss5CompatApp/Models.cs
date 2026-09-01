namespace Dlss5CompatApp;

enum CpuArch
{
    Unknown,
    X86,
    X64
}

enum GraphicsApi
{
    Unknown,
    DirectX7OrOlder,
    DirectX8,
    DirectX9,
    DirectX11,
    DirectX12,
    Dxgi,
    Vulkan,
    OpenGl
}

enum InstallRoute
{
    Unsupported,
    X86Dx9ViaDgVoodoo,
    X86DxgiFeeder,
    X64DirectRenoDx
}

sealed record GameCandidate(
    string Root,
    string ExePath,
    string DisplayName,
    string Name,
    CpuArch Arch,
    GraphicsApi Api,
    string Detection,
    long Size)
{
    public string DisplayApi => Api switch
    {
        GraphicsApi.DirectX7OrOlder => "DX7/DirectDraw or older",
        GraphicsApi.DirectX8 => "DirectX 8",
        GraphicsApi.DirectX9 => "DirectX 9",
        GraphicsApi.DirectX11 => "DirectX 11",
        GraphicsApi.DirectX12 => "DirectX 12",
        GraphicsApi.Dxgi => "DXGI",
        GraphicsApi.Vulkan => "Vulkan",
        GraphicsApi.OpenGl => "OpenGL",
        _ => "Unknown"
    };

    public InstallRoute Route => (Arch, Api) switch
    {
        (CpuArch.X86, GraphicsApi.DirectX9) => InstallRoute.X86Dx9ViaDgVoodoo,
        (CpuArch.X86, GraphicsApi.DirectX11) => InstallRoute.X86DxgiFeeder,
        (CpuArch.X86, GraphicsApi.Dxgi) => InstallRoute.X86DxgiFeeder,
        (CpuArch.X64, GraphicsApi.DirectX11) => InstallRoute.X64DirectRenoDx,
        (CpuArch.X64, GraphicsApi.DirectX12) => InstallRoute.X64DirectRenoDx,
        (CpuArch.X64, GraphicsApi.Dxgi) => InstallRoute.X64DirectRenoDx,
        _ => InstallRoute.Unsupported
    };

    public string DisplayRoute => Route switch
    {
        InstallRoute.X86Dx9ViaDgVoodoo => "x86 DX9 -> dgVoodoo2 -> x86 feeder -> host64",
        InstallRoute.X86DxgiFeeder => "x86 DXGI/D3D11 -> x86 feeder -> host64",
        InstallRoute.X64DirectRenoDx => "x64 direct ReShade + RenoDX",
        _ when Api is GraphicsApi.DirectX8 or GraphicsApi.DirectX7OrOlder => "Unsupported: DX8 or older",
        _ => "Unsupported"
    };
}

sealed record PayloadInfo(
    string Root,
    string? ReShadeSetup,
    string? RenoDxDlss5Addon,
    string? DgVoodooD3D9,
    string? DgVoodooCpl,
    IReadOnlyList<string> NvidiaDlls,
    IReadOnlyList<string> StreamlineDlls,
    IReadOnlyList<string> ExtraAddons)
{
    public bool HasCoreDlss => NvidiaDlls.Any(p => Path.GetFileName(p).Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase))
        && NvidiaDlls.Any(p => Path.GetFileName(p).Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase));

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            parts.Add("bundled ReShade DLLs");
            parts.Add(RenoDxDlss5Addon is null ? "RenoDX DLSS5 missing" : "RenoDX DLSS5 found");
            parts.Add(HasCoreDlss ? "DLSS/DLSSNR found" : "DLSS/DLSSNR missing");
            parts.Add(DgVoodooD3D9 is null ? "dgVoodoo2 D3D9 missing" : "dgVoodoo2 D3D9 found");
            if (ExtraAddons.Count > 0) parts.Add($"{ExtraAddons.Count} extra add-on(s)");
            return string.Join("; ", parts);
        }
    }
}

sealed class InstallManifest
{
    public int Version { get; set; } = 1;
    public DateTimeOffset Date { get; set; } = DateTimeOffset.Now;
    public string GameRoot { get; set; } = "";
    public string ExeRelativePath { get; set; } = "";
    public string Route { get; set; } = "";
    public List<ManifestFile> Replaced { get; set; } = [];
    public List<string> Added { get; set; } = [];
}

sealed class ManifestFile
{
    public string RelativePath { get; set; } = "";
}
