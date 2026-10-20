using System.Diagnostics;
using System.Security.Cryptography;

namespace Dlss5CompatApp.Tests;

public sealed class ComponentInspectionTests
{
    [Fact]
    public async Task DirectCopiesMatchByHashIncludingTheModelAlias()
    {
        using var fixture = new Fixture();
        fixture.Copy(fixture.Payload.PhoenixAddon!, "Phoenix.addon64");
        fixture.Copy(fixture.Payload.NvidiaDlls[1], "PhoenixModel.dll");
        fixture.Copy(fixture.Payload.ReShade64Dll!, "dxgi.dll");
        var report = await ComponentInspectionService.InspectAsync(fixture.Game, fixture.Payload);
        Assert.Equal(3, report.Rows.Count);
        Assert.All(report.Rows, row => Assert.Equal(ComponentInspectionState.Current, row.State));
        var model = Assert.Single(report.Rows, r => r.Name.StartsWith("Phoenix DLSS5 model"));
        Assert.Equal(fixture.Payload.NvidiaDlls[1], model.PayloadPath);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(model.InstalledPath))), model.InstalledSha256);
        Assert.Equal(3, report.Summary.Available);
        Assert.Equal(1, report.Summary.Addons);
        Assert.False(report.MatchesFilter(ComponentInspectionFilter.Outdated));
        Assert.Contains("not verified", ComponentInspectionReport.EvidenceScope);
        Assert.Single(report.Rows, r => r.MatchesFilter(ComponentInspectionFilter.Addons));
        Assert.Equal(3, report.Rows.Count(r => r.MatchesFilter(ComponentInspectionFilter.Available)));
    }

    [Fact]
    public async Task ExplicitNativePreferenceUsesGameModelAliasWithoutAnAutomaticNativeSignal()
    {
        using var fixture = new Fixture();
        fixture.Copy(fixture.Payload.PhoenixAddon!, "Phoenix.addon64");
        fixture.Copy(fixture.Payload.NvidiaDlls[1], "PhoenixModel.dll");
        fixture.Copy(fixture.Payload.ReShade64Dll!, "dxgi.dll");
        var game = fixture.Game with { HasNativeDlssSignal = false, PreferredRoute = GameRoutePreference.Native };
        var report = await ComponentInspectionService.InspectAsync(game, fixture.Payload);
        Assert.Equal(InstallRoute.X64DirectPhoenix, game.Route);
        Assert.Equal(3, report.Rows.Count);
        Assert.All(report.Rows, r => Assert.Equal(ComponentInspectionState.Current, r.State));
        Assert.Equal(Path.Combine(game.Root, "PhoenixModel.dll"), Assert.Single(report.Rows, r => r.Name.StartsWith("Phoenix DLSS5 model")).InstalledPath);
    }

    [Fact]
    public async Task ExplicitFeederPreferenceUsesHostEvenWithAnAutomaticNativeSignal()
    {
        using var fixture = new Fixture();
        fixture.Copy(fixture.Payload.PhoenixAddon!, "host64/Phoenix.addon64");
        fixture.Copy(fixture.Payload.NvidiaDlls[1], "host64/PhoenixModel.dll");
        var game = fixture.Game with { PreferredRoute = GameRoutePreference.Feeder };
        var report = await ComponentInspectionService.InspectAsync(game, fixture.Payload);
        Assert.Equal(InstallRoute.X64DxgiFeeder, game.Route);
        Assert.Equal(ComponentInspectionState.Current, report.Rows[0].State);
        Assert.Equal(Path.Combine(game.Root, "host64", "PhoenixModel.dll"), report.Rows[1].InstalledPath);
        Assert.DoesNotContain(report.Rows, r => r.Name == "Phoenix DLSS5 (game)");
    }

    [Fact]
    public async Task OwnFeederInspectionDoesNotReportOptionalNvidiaRuntimesMissing()
    {
        using var fixture = new Fixture();
        fixture.Copy(fixture.Payload.PhoenixAddon!, "host64/Phoenix.addon64");
        fixture.Copy(fixture.Payload.NvidiaDlls[1], "host64/PhoenixModel.dll");
        fixture.Copy(fixture.Payload.ReShade64Dll!, "host64/dxgi.dll");
        fixture.Copy(fixture.Payload.ReShade64Dll!, "dxgi.dll");
        var report = await ComponentInspectionService.InspectAsync(fixture.Game with { Api = GraphicsApi.DirectX11 }, fixture.Payload);
        Assert.Equal(0, report.Summary.Missing);
        Assert.DoesNotContain(report.Rows, row => row.Name.StartsWith("NGX", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExplicitProxyPreferenceDoesNotReportTheAutomaticProxyMissing()
    {
        using var fixture = new Fixture();
        fixture.Copy(fixture.Payload.ReShade64Dll!, "d3d11.dll");
        var game = fixture.Game with { Api = GraphicsApi.DirectX11, ProxyName = "d3d11.dll" };
        var report = await ComponentInspectionService.InspectAsync(game, fixture.Payload);
        Assert.DoesNotContain(report.Rows, r => r.InstalledPath == Path.Combine(game.Root, "dxgi.dll"));
        Assert.Equal(ComponentInspectionState.Current, Assert.Single(report.Rows, r => r.InstalledPath == Path.Combine(game.Root, "d3d11.dll")).State);
    }

    [Theory]
    [InlineData((int)GraphicsApi.DirectX11, "host64")]
    [InlineData((int)GraphicsApi.OpenXr, "_DLSS5_OpenXR/host64")]
    public async Task HostLocationsAreInspectedWithoutConfusingGameAndHostArchitectures(int apiValue, string host)
    {
        var api = (GraphicsApi)apiValue;
        using var fixture = new Fixture();
        fixture.Copy(fixture.Payload.PhoenixAddon!, host + "/Phoenix.addon64");
        fixture.Copy(fixture.Payload.NvidiaDlls[1], host + "/PhoenixModel.dll");
        fixture.Copy(fixture.Payload.NvidiaDlls[0], host + "/nvngx_dlss.dll");
        fixture.Copy(fixture.Payload.NvidiaDlls[1], host + "/nvngx_dlssnr.dll");
        fixture.Copy(fixture.Payload.ReShade64Dll!, host + "/dxgi.dll");
        var report = await ComponentInspectionService.InspectAsync(fixture.Game with { Api = api }, fixture.Payload);
        Assert.Equal(5, report.Rows.Count(r => r.State == ComponentInspectionState.Current));
        Assert.All(report.Rows.Where(r => r.State == ComponentInspectionState.Current), r => Assert.Contains(host.Replace('/', Path.DirectorySeparatorChar), r.InstalledPath));
        Assert.Equal(api == GraphicsApi.OpenXr ? 0 : 1, report.Summary.Missing);
    }

    [Fact]
    public async Task MissingFilesAndMissingPayloadDoNotClaimCurrentness()
    {
        using var fixture = new Fixture();
        var missing = await ComponentInspectionService.InspectAsync(fixture.Game, fixture.Payload);
        Assert.Equal(3, missing.Summary.Missing);
        Assert.False(missing.MatchesFilter(ComponentInspectionFilter.Available));
        fixture.Copy(fixture.Payload.PhoenixAddon!, "Phoenix.addon64");
        var noPayload = await ComponentInspectionService.InspectAsync(fixture.Game, null);
        Assert.Equal(ComponentInspectionState.PayloadMissing, Assert.Single(noPayload.Rows, r => r.IsAddon).State);
        Assert.True(noPayload.MatchesFilter(ComponentInspectionFilter.Available));
    }

    [Fact]
    public async Task EqualVersionButDifferentBytesStillNeedsAttention()
    {
        using var fixture = new Fixture();
        var source = fixture.Payload.PhoenixAddon!;
        File.Copy(typeof(ComponentInspectionService).Assembly.Location, source, true);
        var installed = fixture.Copy(source, "Phoenix.addon64");
        using (var stream = new FileStream(installed, FileMode.Append)) stream.WriteByte(42);
        var report = await ComponentInspectionService.InspectAsync(fixture.Game, fixture.Payload);
        var row = Assert.Single(report.Rows, r => r.IsAddon);
        Assert.Equal(FileVersionInfo.GetVersionInfo(source).FileVersion, row.PayloadVersion);
        Assert.Equal(row.PayloadVersion, row.InstalledVersion);
        Assert.NotEqual(row.PayloadSha256, row.InstalledSha256);
        Assert.Equal(ComponentInspectionState.Different, row.State);
        Assert.True(row.MatchesFilter(ComponentInspectionFilter.Outdated));
        Assert.True(report.MatchesFilter(ComponentInspectionFilter.Outdated));
    }

    [Fact]
    public async Task VersionlessFilesUseHashesAndDoNotInventVersions()
    {
        using var fixture = new Fixture();
        fixture.Write("game/Phoenix.addon64", "different bytes");
        var report = await ComponentInspectionService.InspectAsync(fixture.Game, fixture.Payload);
        var row = Assert.Single(report.Rows, r => r.IsAddon);
        Assert.Null(row.InstalledVersion);
        Assert.Null(row.PayloadVersion);
        Assert.Equal(ComponentInspectionState.Different, row.State);
    }

    [Fact]
    public async Task NumericallyOlderVersionIsOutdatedButNewerCopyIsOnlyDifferent()
    {
        using var fixture = new Fixture();
        var payload = fixture.Payload.PhoenixAddon!;
        File.Copy(typeof(ComponentInspectionService).Assembly.Location, payload, true);
        var installed = fixture.Copy(typeof(ComponentInspectionTests).Assembly.Location, "Phoenix.addon64");
        var report = await ComponentInspectionService.InspectAsync(fixture.Game, fixture.Payload);
        var row = Assert.Single(report.Rows, r => r.IsAddon);
        Assert.Equal(ComponentInspectionState.Outdated, row.State);
        File.Copy(typeof(ComponentInspectionService).Assembly.Location, installed, true);
        File.Copy(typeof(ComponentInspectionTests).Assembly.Location, payload, true);
        var newer = await ComponentInspectionService.InspectAsync(fixture.Game, fixture.Payload);
        Assert.Equal(ComponentInspectionState.Different, Assert.Single(newer.Rows, r => r.IsAddon).State);
    }

    [Fact]
    public async Task UsesExeDirectoryIgnoresDeepAndBackupFilesAndIncludesExplicitAddons()
    {
        using var fixture = new Fixture();
        fixture.Write("game/Binaries/Win64/game.exe", "fixture");
        fixture.Write("game/Phoenix.addon64", "unrelated root copy");
        fixture.Write("game/Binaries/Win64/deep/nvngx_dlss.dll", "ignored");
        fixture.Write("game/Binaries/Win64/_DLSS5_Compat_Backup/Phoenix.addon64", "ignored");
        var addon = fixture.Write("payload/CustomAddons/test.addon64", "addon");
        var disabled = fixture.Write("payload/CustomAddons/disabled.addon64.disabled", "ignored");
        fixture.Write("game/Binaries/Win64/test.addon64", "addon");
        var payload = fixture.Payload with { ExtraAddons = [addon, disabled, addon] };
        var game = fixture.Game with { ExePath = fixture.Workspace.FilePath("game/Binaries/Win64/game.exe") };
        var report = await ComponentInspectionService.InspectAsync(game, payload);
        var row = Assert.Single(report.Rows, r => r.Name == "test.addon64");
        Assert.Equal(ComponentInspectionState.Current, row.State);
        Assert.Equal(1, report.Summary.Addons);
        Assert.DoesNotContain(report.Rows, r => r.InstalledPath.Contains("deep") || r.InstalledPath.Contains("Backup") || r.Name.Contains("disabled"));
        Assert.Equal(ComponentInspectionState.Missing, report.Rows[0].State);
    }

    [Fact]
    public async Task X86GameReShadeUsesX86PayloadAndHostUsesX64()
    {
        using var fixture = new Fixture();
        fixture.Copy(fixture.Payload.ReShade32Dll!, "d3d9.dll");
        fixture.Copy(fixture.Payload.ReShade64Dll!, "host64/dxgi.dll");
        var report = await ComponentInspectionService.InspectAsync(fixture.Game with { Arch = CpuArch.X86, Api = GraphicsApi.DirectX9 }, fixture.Payload);
        var rows = report.Rows.Where(r => r.Name.StartsWith("ReShade")).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, r => Assert.Equal(ComponentInspectionState.Current, r.State));
        Assert.Contains(rows, r => r.PayloadPath == fixture.Payload.ReShade32Dll);
        Assert.Contains(rows, r => r.PayloadPath == fixture.Payload.ReShade64Dll);
    }

    [Fact]
    public async Task FileAndTotalByteBudgetsAndRowLimitsAreReported()
    {
        using var fixture = new Fixture();
        fixture.Copy(fixture.Payload.PhoenixAddon!, "Phoenix.addon64");
        var fileLimit = await ComponentInspectionService.InspectAsync(fixture.Game, fixture.Payload, new ComponentInspectionLimits(MaxFileBytes: 1));
        Assert.True(fileLimit.Summary.Limited);
        Assert.Equal(ComponentInspectionState.Limited, fileLimit.Rows[0].State);
        Assert.Null(fileLimit.Rows[0].InstalledSha256);
        var totalLimit = await ComponentInspectionService.InspectAsync(fixture.Game, fixture.Payload, new ComponentInspectionLimits(MaxTotalBytes: 1));
        Assert.True(totalLimit.Summary.Limited);
        var rowLimit = await ComponentInspectionService.InspectAsync(fixture.Game, fixture.Payload, new ComponentInspectionLimits(MaxRows: 1));
        Assert.Single(rowLimit.Rows);
        Assert.True(rowLimit.Summary.Limited);
    }

    [Fact]
    public async Task RepeatedPayloadModelHashDoesNotConsumeTheBudgetTwice()
    {
        using var fixture = new Fixture();
        fixture.Copy(fixture.Payload.PhoenixAddon!, "host64/Phoenix.addon64");
        fixture.Copy(fixture.Payload.NvidiaDlls[1], "host64/PhoenixModel.dll");
        fixture.Copy(fixture.Payload.NvidiaDlls[1], "host64/nvngx_dlssnr.dll");
        fixture.Copy(fixture.Payload.NvidiaDlls[0], "host64/nvngx_dlss.dll");
        fixture.Copy(fixture.Payload.ReShade64Dll!, "host64/dxgi.dll");
        var bytes = 2 * (new FileInfo(fixture.Payload.PhoenixAddon!).Length + new FileInfo(fixture.Payload.ReShade64Dll!).Length +
            fixture.Payload.NvidiaDlls.Sum(p => new FileInfo(p).Length)) + new FileInfo(fixture.Payload.NvidiaDlls[1]).Length;
        var report = await ComponentInspectionService.InspectAsync(fixture.Game with { Api = GraphicsApi.DirectX11, PreferredRoute = GameRoutePreference.Feeder },
            fixture.Payload, new ComponentInspectionLimits(MaxTotalBytes: bytes));
        Assert.False(report.Summary.Limited);
        Assert.Equal(5, report.Rows.Count(r => r.State == ComponentInspectionState.Current));
    }

    [Fact]
    public async Task LimitedPayloadIsReportedEvenWhenInstalledCopyIsMissing()
    {
        using var fixture = new Fixture();
        var report = await ComponentInspectionService.InspectAsync(fixture.Game, fixture.Payload,
            new ComponentInspectionLimits(MaxFileBytes: 1));
        Assert.True(report.Summary.Limited);
        Assert.All(report.Rows, r => Assert.Equal(ComponentInspectionState.Missing, r.State));
        Assert.All(report.Rows, r => Assert.Null(r.PayloadSha256));
    }

    [Fact]
    public async Task DeadlineReturnsAnExplicitlyLimitedPartialReport()
    {
        using var fixture = new Fixture();
        var installed = fixture.Copy(fixture.Payload.PhoenixAddon!, "Phoenix.addon64");
        using (var stream = new FileStream(installed, FileMode.Open, FileAccess.Write)) stream.SetLength(64L * 1024 * 1024);
        var report = await ComponentInspectionService.InspectAsync(fixture.Game, fixture.Payload,
            new ComponentInspectionLimits(Timeout: TimeSpan.FromMilliseconds(1)));
        Assert.True(report.Summary.Limited);
        Assert.DoesNotContain(report.Rows, r => r.State == ComponentInspectionState.Current);
    }

    [Fact]
    public async Task LockedFilesAreUnreadableRatherThanMissingOrCurrent()
    {
        using var fixture = new Fixture();
        var installed = fixture.Copy(fixture.Payload.PhoenixAddon!, "Phoenix.addon64");
        using var locked = new FileStream(installed, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var report = await ComponentInspectionService.InspectAsync(fixture.Game, fixture.Payload);
        Assert.Equal(ComponentInspectionState.Unreadable, report.Rows[0].State);
        Assert.False(report.Rows[0].InstalledAvailable);
    }

    [Fact]
    public async Task CancellationAndInvalidPathsDoNotStartInspection()
    {
        using var fixture = new Fixture();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ComponentInspectionService.InspectAsync(fixture.Game, fixture.Payload, cancelled.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => ComponentInspectionService.InspectAsync(fixture.Game with { ExePath = fixture.Workspace.FilePath("outside.exe") }, fixture.Payload));
        await Assert.ThrowsAsync<ArgumentException>(() => ComponentInspectionService.InspectAsync(fixture.Game with { Root = @"\\server\game" }, fixture.Payload));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ComponentInspectionService.InspectAsync(fixture.Game, fixture.Payload, new ComponentInspectionLimits(MaxRows: 0)));
    }

    [Fact]
    public async Task PayloadReparseAncestorsAreRejected()
    {
        using var fixture = new Fixture();
        fixture.Copy(fixture.Payload.PhoenixAddon!, "Phoenix.addon64");
        var link = fixture.Workspace.FilePath("linked-payload");
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{link}\" \"{fixture.Payload.Root}\"",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        try
        {
            var report = await ComponentInspectionService.InspectAsync(fixture.Game,
                fixture.Payload with { PhoenixAddon = Path.Combine(link, "phoenix.addon64") });
            Assert.Equal(ComponentInspectionState.Unreadable, report.Rows[0].State);
            Assert.Null(report.Rows[0].PayloadSha256);
            Assert.Contains("Reparse", report.Rows[0].Detail!);
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task HostReparseAncestorsAreRejectedWithoutReadingLinkedFiles()
    {
        using var fixture = new Fixture();
        var link = Path.Combine(fixture.Game.Root, "host64");
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{link}\" \"{fixture.Payload.Root}\"",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        try
        {
            var report = await ComponentInspectionService.InspectAsync(fixture.Game with { Api = GraphicsApi.DirectX11 }, fixture.Payload);
            Assert.All(report.Rows.Where(r => r.InstalledPath.StartsWith(link + Path.DirectorySeparatorChar)), r =>
            {
                Assert.Equal(ComponentInspectionState.Unreadable, r.State);
                Assert.Null(r.InstalledSha256);
                Assert.False(r.InstalledAvailable);
            });
        }
        finally { Directory.Delete(link); }
    }

    sealed class Fixture : IDisposable
    {
        public TestWorkspace Workspace { get; } = new();
        public GameCandidate Game { get; }
        public PayloadInfo Payload { get; }
        public Fixture()
        {
            var exe = Write("game/game.exe", "CPU fixture only");
            Game = new(Path.GetDirectoryName(exe)!, exe, "Fixture", "game.exe", CpuArch.X64,
                GraphicsApi.DirectX12, "fixture", 1, HasNativeDlssSignal: true);
            var reshade32 = Write("payload/ReShade32.dll", "reshade32");
            var reshade64 = Write("payload/ReShade64.dll", "reshade64");
            var phoenix = Write("payload/phoenix.addon64", "phoenix");
            var sr = Write("payload/nvngx_dlss.dll", "sr");
            var nr = Write("payload/nvngx_dlssnr.dll", "nr");
            Payload = new(Workspace.FilePath("payload"), null, reshade32, reshade64, phoenix,
                null, null, null, null, null, null, null, null, null, null, null, null, [sr, nr], [], []);
        }
        public string Write(string relative, string contents)
        {
            var path = Workspace.FilePath(relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
            return path;
        }
        public string Copy(string source, string relative)
        {
            var path = Write("game/" + relative, "");
            File.Copy(source, path, true);
            return path;
        }
        public void Dispose() => Workspace.Dispose();
    }
}
