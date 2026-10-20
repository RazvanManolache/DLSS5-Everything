namespace Dlss5CompatApp.Tests;

public sealed class CliSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitFlagParsesBeforeOrAfterExecutableWithoutChangingExistingOptions(bool beforeExecutable)
    {
        var flag = CliInstaller.AntiCheatAcknowledgementFlag.ToUpperInvariant();
        var args = beforeExecutable
            ? new[] { "--install", flag, "game.exe", "--payload", "components", "--force-vr-eye-split", "--frame-iterations", "3" }
            : new[] { "--install", "game.exe", "--payload", "components", "--force-vr-eye-split", "--frame-iterations", "3", flag };
        var options = CliInstaller.Parse(args);
        Assert.True(options.AcknowledgeAntiCheatRisk);
        Assert.Equal("game.exe", options.ExePath);
        Assert.Equal("components", options.PayloadRoot);
        Assert.True(options.ForceVrEyeSplit);
        Assert.Equal(3, options.FrameIterations);
    }

    [Fact]
    public void AcknowledgementDefaultsOffAndOptionValuesDoNotAcknowledge()
    {
        var defaults = CliInstaller.Parse(["--install", "game.exe"]);
        Assert.False(defaults.AcknowledgeAntiCheatRisk);
        Assert.False(defaults.ForceVrEyeSplit);
        Assert.Equal(1, defaults.FrameIterations);
        var value = CliInstaller.Parse(["--install", "game.exe", "--payload", CliInstaller.AntiCheatAcknowledgementFlag]);
        Assert.False(value.AcknowledgeAntiCheatRisk);
        Assert.Equal(CliInstaller.AntiCheatAcknowledgementFlag, value.PayloadRoot);
        Assert.Throws<ArgumentException>(() => CliInstaller.Parse(["--install", "game.exe", CliInstaller.AntiCheatAcknowledgementFlag + "=true"]));
    }

    [Theory]
    [InlineData("EasyAntiCheat_EOS_Setup.exe")]
    [InlineData("BattlEye/BEService.exe")]
    public void DetectedAntiCheatWithoutFlagStopsBeforeAnyInstallAction(string marker)
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game.exe", CpuArch.X64, "d3d12.dll");
        var markerPath = workspace.FilePath(marker);
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        File.WriteAllText(markerPath, "CPU-only marker");
        var originals = Directory.GetFiles(workspace.Root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        var checks = InstallPreflight.Check(workspace.Root, exe, filesToWrite: ["dxgi.dll"], processProvider: () => []);
        Assert.True(checks.CanInstall);
        Assert.True(checks.RequiresAntiCheatAcknowledgement);
        var output = new List<string>();
        var reachedInstall = false;
        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            CliInstaller.RequirePreflightAcknowledgement(checks, CliInstaller.Parse(["--install", exe]).AcknowledgeAntiCheatRisk, output.Add);
            reachedInstall = true;
        });
        Assert.False(reachedInstall);
        Assert.Contains(CliInstaller.AntiCheatAcknowledgementFlag, error.Message);
        Assert.Contains(checks.DisplayText, error.Message);
        Assert.Contains(output, text => text.Contains("[Warning]"));
        Assert.Equal(originals.Keys.OrderBy(path => path), Directory.GetFiles(workspace.Root, "*", SearchOption.AllDirectories).OrderBy(path => path));
        foreach (var (path, contents) in originals) Assert.Equal(contents, File.ReadAllBytes(path));
    }

    [Fact]
    public void ExplicitAcknowledgementPermitsAntiCheatWarningButStillReportsIt()
    {
        var checks = new InstallPreflightResult([new("anti-cheat", PreflightSeverity.Warning, "Anti-cheat marker")]);
        var options = CliInstaller.Parse(["--install", "game.exe", CliInstaller.AntiCheatAcknowledgementFlag]);
        var output = new List<string>();
        CliInstaller.RequirePreflightAcknowledgement(checks, options.AcknowledgeAntiCheatRisk, output.Add);
        Assert.Single(output);
        Assert.Contains("Anti-cheat marker", output[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcknowledgementCannotOverrideBlockingErrors(bool acknowledge)
    {
        var checks = new InstallPreflightResult([
            new("anti-cheat", PreflightSeverity.Warning, "Anti-cheat marker"),
            new("mod-conflict", PreflightSeverity.Error, "Unmanaged proxy")]);
        var error = Assert.Throws<InvalidOperationException>(() => CliInstaller.RequirePreflightAcknowledgement(checks, acknowledge, _ => { }));
        Assert.Contains("Unmanaged proxy", error.Message);
    }

    [Fact]
    public void CleanChecksDoNotNeedAcknowledgementAndOtherWarningsRemainVisible()
    {
        var output = new List<string>();
        CliInstaller.RequirePreflightAcknowledgement(new([]), false, output.Add);
        Assert.Empty(output);
        CliInstaller.RequirePreflightAcknowledgement(new([new("scan-incomplete", PreflightSeverity.Warning, "Inspection limited")]), false, output.Add);
        Assert.Single(output);
        Assert.Contains("Inspection limited", output[0]);
    }
}
