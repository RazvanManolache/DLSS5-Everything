namespace Dlss5CompatApp.Tests;

public sealed class DosEnhancedDiscoveryTests
{
    [Fact]
    public void PreparationCliRejectsMalformedArgumentsWithoutLaunching()
    {
        Assert.Equal(1, DosGameLauncher.PrepareFromArgs(["--prepare-dos"]));
    }

    [Fact]
    public void ScannerChoosesTheEnhancedComponentAndIgnoresStagingAndSessionCopies()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePe("dosbox-staging/dosbox.exe", CpuArch.X64);
        workspace.WritePe(".dos-sessions/old/app/DosboxEnhanced.exe", CpuArch.X64);
        Assert.Null(PayloadScanner.Scan(workspace.Root).DosBoxEnhancedExe);
        var enhanced = workspace.WritePe("dosbox-enhanced/app/DosboxEnhanced.exe", CpuArch.X64);
        Assert.True(LocalPaths.Same(enhanced, PayloadScanner.Scan(workspace.Root).DosBoxEnhancedExe!));
        workspace.WritePe("dosbox-enhanced/app/DosboxEnhanced.exe", CpuArch.X86);
        Assert.Null(PayloadScanner.Scan(workspace.Root).DosBoxEnhancedExe);
    }

    [Fact]
    public void ProfileRejectsDosCommandInjectionBeforeWritingAFile()
    {
        using var workspace = new TestWorkspace();
        var executable = workspace.FilePath("A&B.COM");
        File.WriteAllText(executable, "fixture");
        Assert.Throws<ArgumentException>(() => DosGameLauncher.WriteGameConfig(workspace.Root, executable));
        Assert.False(File.Exists(workspace.FilePath("session-profile.json")));
    }
}
