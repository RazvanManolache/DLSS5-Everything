using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Dlss5CompatApp.Tests;

public sealed class PhoenixInstallationInfoTests
{
    [Fact]
    public async Task HistoricalGameReceiptKeepsItsVersionAfterTheLibraryChangesAndRejectsTamperedFiles()
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("Game/Game.exe", CpuArch.X64);
        var installed = workspace.WritePe("Game/Phoenix.addon64", CpuArch.X64);
        var receipt = new InstallManifest { GameRoot = Path.GetDirectoryName(exe)!, ExeRelativePath = "Game.exe",
            PhoenixVersion = "1.0-beta", PhoenixAddon64Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(installed))) };
        Directory.CreateDirectory(workspace.FilePath("Game/_DLSS5_Compat_Backup"));
        File.WriteAllText(workspace.FilePath("Game/_DLSS5_Compat_Backup/manifest.json"), JsonSerializer.Serialize(receipt));
        var report = await PhoenixInstallationInfo.InspectAsync(Game(exe), null, null, default);
        Assert.Contains("Installed Phoenix: 1.0-beta", report.Summary);
        Assert.Contains("Installation receipt and SHA-256", report.Details);
        File.AppendAllText(installed, "changed binary");
        Assert.DoesNotContain("Installed Phoenix: 1.0-beta", (await PhoenixInstallationInfo.InspectAsync(Game(exe), null, null, default)).Summary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReportsActualReleaseAndDistinguishesHardLinksFromCopies(bool link)
    {
        using var workspace = new TestWorkspace();
        var binary = workspace.WritePe("Components/Phoenix.addon64", CpuArch.X64);
        var gameExe = workspace.WritePe("Game/Game.exe", CpuArch.X64);
        var installed = workspace.FilePath("Game/Phoenix.addon64");
        if (link) Assert.True(CreateHardLinkW(installed, binary, 0)); else File.Copy(binary, installed);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(binary)));
        var root = workspace.FilePath("Components");
        File.WriteAllText(Path.Combine(root, PhoenixReleaseDownloader.SourceManifestFileName), JsonSerializer.Serialize(
            new PhoenixReleasePayload("local", "1.0-beta:Phoenix.zip", new FileInfo(binary).Length, hash, false)));
        var payload = PayloadScanner.Scan(root) with { PhoenixAddon = binary };
        var game = Game(gameExe);
        var report = await PhoenixInstallationInfo.InspectAsync(game, payload, null, default);
        Assert.Contains("Installed Phoenix: 1.0-beta", report.Summary);
        Assert.Contains(Path.GetFullPath(installed), report.Details); Assert.Contains(binary, report.Details);
        Assert.Contains(link ? "Hard link: same file as" : "Independent file", report.Details);
        Assert.Contains("SHA-256 matches cached release", report.Details);
    }

    [Fact]
    public async Task ExternalAddonAndRuntimePathsAreResolvedWithoutStartingTheGame()
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("Game/Game.exe", CpuArch.X86);
        var custom = workspace.WritePe("Shared/Phoenix.addon32", CpuArch.X86);
        var host = workspace.WritePe("Game/host64/Phoenix.addon64", CpuArch.X64);
        File.WriteAllText(workspace.FilePath("Game/ReShade.ini"), "[ADDON]\nAddonPath=../Shared\n[Phoenix.Compatibility]\nNativeRuntime=../Native\nPortableRuntime=C:/External/Portable\n[Phoenix]\nNvidiaDllFolder=../Models\n");
        var report = await PhoenixInstallationInfo.InspectAsync(Game(exe), null, null, default);
        Assert.Contains(Path.GetFullPath(custom), report.Details); Assert.Contains(Path.GetFullPath(host), report.Details);
        Assert.Contains(workspace.FilePath("Native"), report.Details);
        Assert.Contains(Path.GetFullPath("C:/External/Portable"), report.Details);
        Assert.Contains(workspace.FilePath("Models"), report.Details);
        Assert.Contains("unknown", report.Summary); Assert.DoesNotContain("matches cached release", report.Details);
    }

    [Fact]
    public async Task MissingInstallationAndCancellationAreHonest()
    {
        using var workspace = new TestWorkspace(); var exe = workspace.WritePe("Game.exe", CpuArch.X64);
        Assert.Contains("No Phoenix addon found", (await PhoenixInstallationInfo.InspectAsync(Game(exe), null, null, default)).Summary);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PhoenixInstallationInfo.InspectAsync(Game(exe), null, null, cancel.Token));
    }

    static GameCandidate Game(string exe) => new(Path.GetDirectoryName(exe)!, exe, "Synthetic game", Path.GetFileName(exe),
        PeReader.GetArchitecture(exe), GraphicsApi.DirectX11, "Fixture", 4096, [GraphicsApi.DirectX11]);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateHardLinkW(string path, string existing, nint security);
}
