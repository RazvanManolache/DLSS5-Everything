using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Dlss5CompatApp.Tests;

public sealed class PhoenixRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeSourceBlockedDuringCopyContinuesButChecksumCorruptionStops(bool corrupt)
    {
        using var workspace = new TestWorkspace();
        var addon = Addons(workspace);
        Portable(workspace, "selected", CpuArch.X86);
        var payloadRoot = workspace.FilePath("payload");
        await PhoenixReleaseDownloader.ImportLocalAsync(addon, payloadRoot, default);
        Dll(workspace, "payload/ReShade32.dll", CpuArch.X86);
        Dll(workspace, "payload/nvngx_dlssnr.dll", CpuArch.X64);
        var payload = PayloadScanner.Scan(payloadRoot);
        var source = PhoenixRuntimePackage.For(payload, CpuArch.X86).Single(file => file.RelativePath.EndsWith("kernel.spv")).SourcePath;
        var exe = workspace.WritePe("game/game.exe", CpuArch.X86);
        var game = new GameCandidate(Path.GetDirectoryName(exe)!, exe, "Fixture", "game.exe", CpuArch.X86,
            GraphicsApi.DirectX9, "fixture", 160 * 1024);
        FileStream? sourceLock = null;
        var injected = false;
        var installer = new InstallerEngine(message =>
        {
            if (injected || !message.StartsWith("Copied PhoenixRuntime")) return;
            injected = true;
            if (corrupt) File.WriteAllText(source, "changed after verification");
            else sourceLock = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None);
        });
        try
        {
            if (corrupt)
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(game, payload));
                Assert.True(InstallerEngine.HasPending(game.Root));
                Assert.False(File.Exists(InstallerEngine.LauncherPathFor(game)));
            }
            else
            {
                var result = await installer.InstallAsync(game, payload);
                Assert.False(result.Complete);
                Assert.Contains(result.Failures, file => file.RelativePath.Replace('\\', '/').EndsWith("vulkan/kernel.spv"));
                Assert.True(File.Exists(workspace.FilePath("game/ReShade.ini")));
                Assert.True(File.Exists(InstallerEngine.LauncherPathFor(game)));
            }
            Assert.True(injected);
        }
        finally { sourceLock?.Dispose(); }
        if (corrupt) await installer.RecoverAsync(game.Root);
        else await installer.RestoreAsync(game.Root);
    }

    [Theory]
    [InlineData("phoenix_native_broker.exe")]
    [InlineData("vulkan/kernel.spv")]
    [InlineData("NOTICE")]
    public async Task BlockedRuntimeFileDoesNotPreventOtherCompanionsAndConfiguration(string blocked)
    {
        using var workspace = new TestWorkspace();
        var addon = Addons(workspace);
        Portable(workspace, "selected", CpuArch.X86);
        workspace.WritePe("selected/PhoenixRuntime/win-x86/phoenix_native_broker.exe", CpuArch.X86);
        var payloadRoot = workspace.FilePath("payload");
        await PhoenixReleaseDownloader.ImportLocalAsync(addon, payloadRoot, default);
        Dll(workspace, "payload/ReShade32.dll", CpuArch.X86);
        Dll(workspace, "payload/nvngx_dlssnr.dll", CpuArch.X64);
        var payload = PayloadScanner.Scan(payloadRoot);
        var exe = workspace.WritePe("game/game.exe", CpuArch.X86);
        var game = new GameCandidate(Path.GetDirectoryName(exe)!, exe, "Fixture", "game.exe", CpuArch.X86,
            GraphicsApi.DirectX9, "fixture", 160 * 1024);
        var relative = "PhoenixRuntime/win-x86/" + blocked;
        var target = workspace.FilePath("game/" + relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "original blocked file");
        var installer = new InstallerEngine(_ => { });
        InstallResult result;
        using (var locked = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            result = await installer.InstallAsync(game, payload, phoenixSettings: new() { CompatibilityExecutionMode = "portable" });
        Assert.False(result.Complete);
        Assert.Contains(result.Failures, file => file.RelativePath.Replace('\\', '/') == relative);
        Assert.False(RuntimeDiagnostics.HasInstallManifest(game));
        Assert.Equal("original blocked file", File.ReadAllText(target));
        Assert.True(File.Exists(workspace.FilePath("game/PhoenixRuntime/win-x86/phoenix_runtime_cpp.dll")));
        Assert.True(File.Exists(workspace.FilePath("game/Phoenix.addon32")));
        Assert.True(File.Exists(workspace.FilePath("game/ReShade.ini")));
        Assert.True(File.Exists(InstallerEngine.LauncherPathFor(game)));
        await installer.RestoreAsync(game.Root);
        Assert.Equal("original blocked file", File.ReadAllText(target));
        Assert.False(File.Exists(workspace.FilePath("game/Phoenix.addon32")));
    }

    internal static string Dll(TestWorkspace workspace, string name, CpuArch arch, params string[] exports)
    {
        var path = workspace.WritePe(name, arch);
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96), 0x2022);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x98 + (arch == CpuArch.X64 ? 108 : 92)), 16);
        File.WriteAllBytes(path, bytes);
        TestWorkspace.SetPhoenixExports(path, exports);
        return path;
    }

    internal static void Portable(TestWorkspace workspace, string parent, CpuArch arch)
    {
        var section = parent + "/PhoenixRuntime/" + (arch == CpuArch.X86 ? "win-x86" : "win-x64");
        Dll(workspace, section + "/phoenix_runtime_cpp.dll", arch);
        Dll(workspace, section + "/phoenix_cpu_cpp.dll", arch);
        Dll(workspace, section + "/phoenix_vulkan_cpp.dll", arch);
        Dll(workspace, section + "/vulkan/phoenix_vulkan.dll", arch);
        File.WriteAllText(workspace.FilePath(section + "/phoenix_native_cpp.json"), "{}");
        File.WriteAllBytes(workspace.FilePath(section + "/vulkan/kernel.spv"), [3,2,35,7]);
        File.WriteAllText(workspace.FilePath(section + "/NOTICE"), "Fixture notice");
    }

    static void Native(TestWorkspace workspace)
    {
        const string prefix = "selected/PhoenixRuntime/wow64/";
        var hashes = new Dictionary<string, string>();
        foreach (var name in new[] { "phoenix_bootstrap64.dll", "phoenix_driver64.dll", "phoenix-nvngx.dll", "phoenix_native.dll", "wow64ext.dll" })
        {
            var file = Dll(workspace, prefix + name, name == "wow64ext.dll" ? CpuArch.X86 : CpuArch.X64);
            hashes[name] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        }
        File.WriteAllText(workspace.FilePath(prefix + "bootstrap.json"), JsonSerializer.Serialize(new
        { schema = "phoenix-wow64-bootstrap/2", files = hashes, ntdll_sha256 = new string('0', 64),
            cuda_sha256 = new string('0', 64), driver_directory = "fixture-driver" }));
        Directory.CreateDirectory(workspace.FilePath(prefix + "native"));
        File.WriteAllText(workspace.FilePath(prefix + "native/native_graph.json"), "{}");
        var graphHash = Convert.ToHexString(SHA256.HashData("{}"u8));
        var kernels = Enumerable.Range(0, 41).Select(index => {
            var name = $"kernel{index}.cubin";
            byte[] bytes = [(byte)index];
            File.WriteAllBytes(workspace.FilePath(prefix + "native/" + name), bytes);
            return new { binary = name, binary_sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
        }).ToArray();
        File.WriteAllText(workspace.FilePath(prefix + "native/native_manifest.json"), JsonSerializer.Serialize(new
        { schema = "phoenix-native-generated-build/1", passed = true, model_weights_included = false,
            native_graph_sha256 = graphHash, kernels }));
    }

    static string Addons(TestWorkspace workspace)
    {
        var addon = Dll(workspace, "selected/Phoenix.addon64", CpuArch.X64,
            "CnrPHOENIXSummary", "CnrPHOENIXRegister", "CnrPHOENIXFeederVersion", "CnrPHOENIXFeederEvaluate",
            "CnrPHOENIXCompatibilityStatus", "AddonInit", "AddonUninit", "CnrPHOENIXInProcessBackbufferVersion");
        TestWorkspace.SetEmbeddedPhoenixRuntime(addon);
        Dll(workspace, "selected/Phoenix.addon32", CpuArch.X86, "CnrPHOENIXCompatibilityStatus", "AddonInit", "AddonUninit");
        return addon;
    }

    [Theory]
    [InlineData((int)CpuArch.X86)]
    [InlineData((int)CpuArch.X64)]
    public async Task ImportInstallInspectAndRestoreArchitectureMatchedRuntime(int machine)
    {
        using var workspace = new TestWorkspace();
        var addon = Addons(workspace);
        Portable(workspace, "selected", CpuArch.X86);
        Portable(workspace, "selected", CpuArch.X64);
        Native(workspace);
        var payloadRoot = workspace.FilePath("payload");
        var release = await PhoenixReleaseDownloader.ImportLocalAsync(addon, payloadRoot, default);
        Assert.NotNull(release.RuntimePath);
        var reselected = await PhoenixReleaseDownloader.ImportLocalAsync(Path.Combine(payloadRoot, "phoenix.addon64"), payloadRoot, default);
        Assert.Equal(release.RuntimePath, reselected.RuntimePath);
        Assert.Equal(release.Companion32Path, reselected.Companion32Path);
        Dll(workspace, "payload/ReShade32.dll", CpuArch.X86);
        Dll(workspace, "payload/ReShade64.dll", CpuArch.X64);
        Dll(workspace, "payload/nvngx_dlssnr.dll", CpuArch.X64);
        var payload = PayloadScanner.Scan(payloadRoot);
        var arch = (CpuArch)machine;
        var exe = workspace.WritePe("game/game.exe", arch);
        var game = new GameCandidate(Path.GetDirectoryName(exe)!, exe, "Fixture", "game.exe", arch,
            GraphicsApi.DirectX9, "fixture", 160 * 1024);
        var selected = arch == CpuArch.X86 ? "win-x86" : "win-x64";
        Directory.CreateDirectory(workspace.FilePath("game/PhoenixRuntime"));
        File.WriteAllText(workspace.FilePath("game/PhoenixRuntime/user.txt"), "keep me");
        var log = new List<string>();
        var installer = new InstallerEngine(log.Add);
        await installer.InstallAsync(game, payload, phoenixSettings: new() { CompatibilityExecutionMode = "portable" });
        Assert.True(File.Exists(workspace.FilePath("game/PhoenixRuntime/" + selected + "/vulkan/phoenix_vulkan.dll")));
        Assert.True(File.Exists(workspace.FilePath("game/PhoenixRuntime/" + selected + "/vulkan/kernel.spv")));
        Assert.True(File.Exists(workspace.FilePath("game/PhoenixRuntime/" + selected + "/NOTICE")));
        Assert.Equal(arch == CpuArch.X86, Directory.Exists(workspace.FilePath("game/PhoenixRuntime/wow64")));
        Assert.False(Directory.Exists(workspace.FilePath("game/PhoenixRuntime/" + (arch == CpuArch.X86 ? "win-x64" : "win-x86"))));
        var ini = workspace.FilePath("game/ReShade.ini");
        Assert.Equal("portable", IniEditor.GetValue(ini, "Phoenix.Compatibility", "ExecutionMode"));
        Assert.Equal("PhoenixRuntime/" + selected, IniEditor.GetValue(ini, "Phoenix.Compatibility", "PortableRuntime"));
        var inspection = await ComponentInspectionService.InspectAsync(game, payload);
        Assert.Contains(inspection.Rows, row => row.Name.Contains("runtime") && row.State == ComponentInspectionState.Current);
        var provider = workspace.FilePath("game/PhoenixRuntime/" + selected + "/vulkan/phoenix_vulkan.dll");
        File.Delete(provider);
        IniEditor.SetValue(ini, "Phoenix.Compatibility", "NativeRuntime", "old-runtime");
        inspection = await ComponentInspectionService.InspectAsync(game, payload);
        Assert.Contains(inspection.Rows, row => LocalPaths.Same(row.InstalledPath, provider) && row.State == ComponentInspectionState.Missing);
        await installer.InstallAsync(game, payload);
        Assert.True(File.Exists(provider));
        foreach (var name in new[] { "ReShade.ini", "ReShadeVR.ini" })
        {
            Assert.Equal("auto", IniEditor.GetValue(Path.Combine(game.Root, name), "Phoenix.Compatibility", "ExecutionMode"));
            Assert.Equal("PhoenixRuntime/wow64", IniEditor.GetValue(Path.Combine(game.Root, name), "Phoenix.Compatibility", "NativeRuntime"));
        }
        await installer.RestoreAsync(game.Root);
        Assert.False(File.Exists(provider));
        Assert.Equal("keep me", File.ReadAllText(workspace.FilePath("game/PhoenixRuntime/user.txt")));
        Assert.True(Directory.Exists(Path.Combine(payloadRoot, release.RuntimePath!)));
    }

    [Theory]
    [InlineData("wow64_library")]
    [InlineData("wow64_driver")]
    [InlineData("portable")]
    public async Task ExplicitInternalSelectionWithoutRuntimeBlocksBeforeGameMutation(string mode)
    {
        using var workspace = new TestWorkspace();
        var addon = Addons(workspace);
        var root = workspace.FilePath("payload");
        await PhoenixReleaseDownloader.ImportLocalAsync(addon, root, default);
        var exe = workspace.WritePe("game/game.exe", CpuArch.X86);
        var game = new GameCandidate(Path.GetDirectoryName(exe)!, exe, "Fixture", "game.exe", CpuArch.X86,
            GraphicsApi.DirectX9, "fixture", 160 * 1024);
        var settings = new PhoenixSettings { CompatibilityExecutionMode = mode };
        var result = InstallerEngine.CheckPreflight(game, PayloadScanner.Scan(root), phoenixSettings: settings);
        Assert.False(result.CanInstall);
        Assert.Contains(result.Findings, finding => finding.Code == "runtime-unavailable");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new InstallerEngine(_ => {}).InstallAsync(game,
            PayloadScanner.Scan(root), phoenixSettings: settings));
        Assert.Equal(["game.exe"], Directory.GetFiles(game.Root).Select(Path.GetFileName));
        Assert.False(Directory.Exists(Path.Combine(game.Root, "_DLSS5_Compat_Backup")));
    }

    [Fact]
    public async Task ModifiedRuntimeOrWrongAddonPairIsNotInstallable()
    {
        using var workspace = new TestWorkspace();
        var addon = Addons(workspace);
        Portable(workspace, "selected", CpuArch.X86);
        var root = workspace.FilePath("payload");
        var release = await PhoenixReleaseDownloader.ImportLocalAsync(addon, root, default);
        var payload = PayloadScanner.Scan(root);
        var runtime = PhoenixRuntimePackage.For(payload, CpuArch.X86);
        File.AppendAllText(runtime.First(x => x.RelativePath.EndsWith(".spv")).SourcePath, "changed");
        Assert.False(PhoenixRuntimePackage.VerifyCached(root, release));
        Assert.Throws<InvalidDataException>(() => PhoenixRuntimePackage.For(payload, CpuArch.X86));
        var other = Dll(workspace, "other.addon32", CpuArch.X86, "CnrPHOENIXCompatibilityStatus", "AddonInit", "AddonUninit");
        File.AppendAllText(other, "other build");
        Assert.Throws<InvalidDataException>(() => PhoenixRuntimePackage.For(payload with { PhoenixAddon32 = other }, CpuArch.X86));
    }

    [Fact]
    public async Task IncompleteImportPreservesWorkingPublication()
    {
        using var workspace = new TestWorkspace();
        var addon = Addons(workspace);
        Portable(workspace, "selected", CpuArch.X86);
        var root = workspace.FilePath("payload");
        await PhoenixReleaseDownloader.ImportLocalAsync(addon, root, default);
        var before = File.ReadAllBytes(Path.Combine(root, PhoenixReleaseDownloader.SourceManifestFileName));
        File.Delete(workspace.FilePath("selected/PhoenixRuntime/win-x86/phoenix_runtime_cpp.dll"));
        await Assert.ThrowsAsync<InvalidDataException>(() => PhoenixReleaseDownloader.ImportLocalAsync(addon, root, default));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(root, PhoenixReleaseDownloader.SourceManifestFileName)));
        Assert.Empty(Directory.GetFileSystemEntries(root, ".phoenix-*"));
    }

    [Theory]
    [InlineData("PhoenixRuntime/win-x86/../../outside.dll")]
    [InlineData("../outside.dll")]
    [InlineData("PhoenixRuntime/win-x86/nvngx_dlssnr.dll")]
    [InlineData("PhoenixRuntime/wow64/nvcuda64.dll")]
    [InlineData("PhoenixRuntime/win-x86/diagnostic.log")]
    public async Task UnsafeArchiveEntriesAreRejected(string name)
    {
        using var workspace = new TestWorkspace();
        var archive = workspace.FilePath("runtime.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        using (var output = zip.CreateEntry(name).Open()) output.WriteByte(1);
        await Assert.ThrowsAnyAsync<Exception>(() => PhoenixRuntimePackage.ExtractAsync(archive, workspace.FilePath("staging"), default));
        Assert.False(File.Exists(workspace.FilePath("outside.dll")));
    }

    [Fact]
    public void NativeRuntimeProfileIsCheckedWithoutLoadingCode()
    {
        using var workspace = new TestWorkspace();
        Native(workspace);
        var bootstrap = workspace.FilePath("selected/PhoenixRuntime/wow64/bootstrap.json");
        Assert.Contains("different Windows", PhoenixRuntimePackage.NativeProfileError([new("wow64/bootstrap.json", bootstrap, "")])!);
        Assert.Contains("not installed", PhoenixRuntimePackage.NativeProfileError([])!);
    }

    [Fact]
    public async Task PublishedSdkBinLayoutRetainsRuntimeDependenciesAndLicensesNotDevelopmentFiles()
    {
        using var workspace = new TestWorkspace();
        Portable(workspace, "sdk", CpuArch.X64);
        var archive = workspace.FilePath("runtime.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var root = workspace.FilePath("sdk");
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetRelativePath(root, file).Replace('\\', '/').Replace("win-x64/", "win-x64/bin/");
                using var output = zip.CreateEntry(name).Open();
                output.Write(File.ReadAllBytes(file));
            }
            foreach (var name in new[] { "LICENSE", "licenses/provider/NOTICE", "PhoenixRuntime/win-x64/include/phoenix.h", "PhoenixRuntime/win-x64/lib/runtime.lib" })
            {
                using var output = zip.CreateEntry(name).Open();
                output.Write("fixture"u8);
            }
        }
        var stage = workspace.FilePath("stage");
        await PhoenixRuntimePackage.ExtractAsync(archive, stage, default);
        Assert.True(File.Exists(Path.Combine(stage, "win-x64", "phoenix_runtime_cpp.dll")));
        Assert.True(File.Exists(Path.Combine(stage, "win-x64", "vulkan", "kernel.spv")));
        Assert.True(File.Exists(Path.Combine(stage, "win-x64", "LICENSE")));
        Assert.True(File.Exists(Path.Combine(stage, "win-x64", "licenses", "provider", "NOTICE")));
        Assert.False(Directory.Exists(Path.Combine(stage, "win-x64", "bin")));
        Assert.False(Directory.Exists(Path.Combine(stage, "win-x64", "include")));
        Assert.False(Directory.Exists(Path.Combine(stage, "win-x64", "lib")));
        var release = PhoenixRuntimePackage.Publish(stage, workspace.FilePath("payload"),
            new("fixture", "fixture", 0, new string('0',64), false), default);
        Assert.True(PhoenixRuntimePackage.VerifyCached(workspace.FilePath("payload"), release));
    }

    [Fact]
    public async Task LocalSdkBinLayoutIsAlsoNormalised()
    {
        using var workspace = new TestWorkspace();
        var addon = Addons(workspace);
        Portable(workspace, "selected", CpuArch.X86);
        var runtime = workspace.FilePath("selected/PhoenixRuntime/win-x86");
        var temporary = workspace.FilePath("sdk-bin");
        Directory.Move(runtime, temporary);
        Directory.CreateDirectory(runtime);
        Directory.Move(temporary, Path.Combine(runtime, "bin"));
        var root = workspace.FilePath("payload");
        await PhoenixReleaseDownloader.ImportLocalAsync(addon, root, default);
        var files = PhoenixRuntimePackage.For(PayloadScanner.Scan(root), CpuArch.X86);
        Assert.Contains(files, file => file.RelativePath == "win-x86/phoenix_runtime_cpp.dll");
        Assert.DoesNotContain(files, file => file.RelativePath.Contains("/bin/"));
    }
}
