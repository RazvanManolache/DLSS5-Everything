using System.Buffers.Binary;

namespace Dlss5CompatApp.Tests;

public sealed class SoftwarePresentationTests
{
    static GameCandidate Fixture(TestWorkspace workspace, GraphicsApi api = GraphicsApi.DirectX11) =>
        new(workspace.Root, workspace.WritePe("unrelated.exe", CpuArch.X64), "Unrelated", "unrelated.exe",
            CpuArch.X64, api, "fixture", 160 * 1024);

    static string Hook(TestWorkspace workspace, string name, CpuArch arch, bool software)
    {
        var path = workspace.WritePe(name, arch);
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96), 0x2022);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x98 + (arch == CpuArch.X64 ? 108 : 92)), 16);
        File.WriteAllBytes(path, bytes);
        TestWorkspace.SetPhoenixExports(path, software
            ? ["ReShadeRegisterAddon", "CnrDLSS5SoftwareDevices"] : ["ReShadeRegisterAddon"]);
        return path;
    }

    [Fact]
    public void SoftwareRejectionMustBelongToThisExecutableAndHaveNoWorkingRuntime()
    {
        using var workspace = new TestWorkspace();
        var game = Fixture(workspace);
        var log = workspace.FilePath("ReShade.log");
        const string rejection = "Skipping device because the driver type is 'D3D_DRIVER_TYPE_WARP' or 'D3D_DRIVER_TYPE_REFERENCE'.";
        File.WriteAllText(log, "into 'other.exe'\n" + rejection);
        Assert.False(SoftwarePresentation.RequiresHook(game));
        File.WriteAllText(log, "into '" + game.ExePath + "'\n" + rejection);
        Assert.True(SoftwarePresentation.RequiresHook(game));
        File.AppendAllText(log, "\nRecreated runtime environment");
        Assert.False(SoftwarePresentation.RequiresHook(game));
        File.WriteAllText(workspace.FilePath("ReShade.ini"), "[APP]\nIncludeSoftwareDevices=1\n");
        Assert.True(SoftwarePresentation.RequiresHook(game));
        Assert.False(SoftwarePresentation.RequiresHook(game.WithApi(GraphicsApi.Vulkan)));
    }

    [Fact]
    public void HookRequiresMatchingArchitectureAndExplicitCapabilityExport()
    {
        using var workspace = new TestWorkspace();
        Assert.False(SoftwarePresentation.Compatible(Hook(workspace, "stock.dll", CpuArch.X64, false), CpuArch.X64));
        Assert.False(SoftwarePresentation.Compatible(Hook(workspace, "wrong.dll", CpuArch.X86, true), CpuArch.X64));
        Assert.True(SoftwarePresentation.Compatible(Hook(workspace, "valid.dll", CpuArch.X64, true), CpuArch.X64));
    }

    [Fact]
    public void GuidedRepairOffersSoftwareVariantOnceInsteadOfRepeatingTheRejectedHook()
    {
        using var workspace = new TestWorkspace();
        var game = Fixture(workspace);
        Hook(workspace, "dxgi.dll", CpuArch.X64, false);
        var attempted = new HashSet<string> { InstallationChoice.InstalledRouteKey(game) };
        File.WriteAllText(workspace.FilePath("ReShade.log"), "into '" + game.ExePath +
            "'\nSkipping device because it uses the Microsoft Basic Render Driver");
        Assert.Contains(InstallationChoice.RepairOptions(game, attempted), choice =>
            InstallationChoice.RouteKey(choice) == InstallationChoice.RouteKey(game));
        Assert.Equal(attempted.Single(), InstallationChoice.InstalledRouteKey(game));
        Hook(workspace, "dxgi.dll", CpuArch.X64, true);
        attempted.Add(InstallationChoice.InstalledRouteKey(game));
        Assert.DoesNotContain(InstallationChoice.RepairOptions(game, attempted), choice =>
            InstallationChoice.RouteKey(choice) == InstallationChoice.RouteKey(game));
    }
}
