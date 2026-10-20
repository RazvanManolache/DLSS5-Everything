using System.Text;
using System.Text.Json.Nodes;

namespace Dlss5CompatApp.Tests;

public sealed class BackendProfileStoreTests
{
    const string Route = "X64DirectPhoenix";
    static BackendProfileStore Store(TestWorkspace w, string exe = "game.exe")
    {
        if (!File.Exists(w.FilePath(exe))) w.WritePe(exe, CpuArch.X64);
        return new(w.Root, w.FilePath(exe));
    }
    static void Write(TestWorkspace w, string relative, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(w.FilePath(relative))!);
        File.WriteAllBytes(w.FilePath(relative), bytes);
    }
    static void Mutate(BackendProfileStore store, Action<JsonObject> mutate)
    {
        var path = store.ProfilePath(Route);
        var obj = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        mutate(obj);
        File.WriteAllText(path, obj.ToJsonString());
    }

    [Fact]
    public void EveryAllowedConfigurationRoundTripsExactBytesIncludingNestedHostFolders()
    {
        using var w = new TestWorkspace(); var store = Store(w, "Binaries/Win64/game.exe");
        var expected = new Dictionary<string, byte[]>();
        var index = 0;
        foreach (var relative in store.ConfigurationPaths)
        {
            var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("[settings]\r\nvalue=" + index++)).Concat(new byte[] { 0, 255 }).ToArray();
            Write(w, relative, bytes); expected.Add(relative, bytes);
        }
        var originalBackup = w.FilePath("_DLSS5_Compat_Backup/ReShade.ini");
        Write(w, "_DLSS5_Compat_Backup/ReShade.ini", Encoding.UTF8.GetBytes("game original"));
        var info = store.Snapshot(Route);
        Assert.Equal(15, info.FileCount);
        Assert.Equal(expected.Values.Sum(b => (long)b.Length), info.ConfigurationBytes);
        Assert.StartsWith(Path.Combine(w.Root, "_DLSS5_Compat_Backup", "backend-profiles"), info.Path);
        foreach (var relative in expected.Keys) Write(w, relative, Encoding.UTF8.GetBytes("new install default"));
        Assert.Equal(15, store.Restore(Route, expected.Keys).Count);
        foreach (var (relative, bytes) in expected) Assert.Equal(bytes, File.ReadAllBytes(w.FilePath(relative)));
        Assert.Equal("game original", File.ReadAllText(originalBackup));
        Assert.Single(Directory.GetFiles(store.DirectoryPath));
        Assert.Empty(Directory.GetFiles(w.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void BackendAndExecutableIdentitiesRemainSeparateAndMissingProfileIsHarmless()
    {
        using var w = new TestWorkspace(); var store = Store(w);
        Assert.Empty(store.Load(Route)); Assert.Empty(store.Restore(Route, []));
        File.WriteAllText(w.FilePath("ReShade.ini"), "direct tuning"); store.Snapshot(Route);
        File.WriteAllText(w.FilePath("ReShade.ini"), "feeder tuning"); store.Snapshot("X64DxgiFeeder");
        Assert.Equal("direct tuning", Encoding.UTF8.GetString(store.Load(Route)["ReShade.ini"]));
        Assert.Equal("feeder tuning", Encoding.UTF8.GetString(store.Load("X64DxgiFeeder")["ReShade.ini"]));
        Assert.Equal(store.ProfilePath(Route), store.ProfilePath(Route.ToLowerInvariant()));
        Assert.Empty(Store(w, "other.exe").Load(Route));
        File.WriteAllText(w.FilePath("ReShadeVR.ini"), "new file");
        store.Restore(Route, ["ReShade.ini"]);
        Assert.Equal("new file", File.ReadAllText(w.FilePath("ReShadeVR.ini")));
    }

    [Fact]
    public void WholeProfileRequiresOwnershipBeforeAnyDestinationWrite()
    {
        using var w = new TestWorkspace(); var store = Store(w);
        File.WriteAllText(w.FilePath("ReShade.ini"), "saved first");
        File.WriteAllText(w.FilePath("Phoenix.json"), "saved second"); store.Snapshot(Route);
        File.WriteAllText(w.FilePath("ReShade.ini"), "keep first");
        File.WriteAllText(w.FilePath("Phoenix.json"), "keep second");
        Assert.Throws<InvalidDataException>(() => store.Restore(Route, ["ReShade.ini"]));
        Assert.Equal("keep first", File.ReadAllText(w.FilePath("ReShade.ini")));
        Assert.Equal("keep second", File.ReadAllText(w.FilePath("Phoenix.json")));
        Assert.ThrowsAny<Exception>(() => store.Restore(Route, ["ReShade.ini", "Phoenix.json", "../bad"]));
        Assert.Equal("keep first", File.ReadAllText(w.FilePath("ReShade.ini")));
    }

    [Theory]
    [InlineData("../outside.ini")] [InlineData("C:\\outside.ini")] [InlineData("ReShade.ini:stream")]
    [InlineData("host64/../ReShade.ini")] [InlineData("_DLSS5_Compat_Backup/manifest.json")]
    [InlineData("other/ReShade.ini")] [InlineData("Phoenix.addon64")] [InlineData("ReShade.ini.")]
    [InlineData("host64//ReShade.ini")] [InlineData("\\\\?\\C:\\outside.ini")]
    [InlineData("CON.ini")] [InlineData("host64 /ReShade.ini")]
    public void InvalidLaterEntryNeverWritesEarlierEntry(string invalid)
    {
        using var w = new TestWorkspace(); var store = Store(w);
        File.WriteAllText(w.FilePath("ReShade.ini"), "saved first");
        File.WriteAllText(w.FilePath("Phoenix.json"), "saved second"); store.Snapshot(Route);
        File.WriteAllText(w.FilePath("ReShade.ini"), "keep first");
        Mutate(store, p => p["Files"]![1]!["RelativePath"] = invalid);
        Assert.ThrowsAny<Exception>(() => store.Restore(Route, ["ReShade.ini", "Phoenix.json"]));
        Assert.Equal("keep first", File.ReadAllText(w.FilePath("ReShade.ini")));
        Assert.Equal("saved second", File.ReadAllText(w.FilePath("Phoenix.json")));
    }

    [Theory]
    [InlineData("Version")] [InlineData("GameRoot")] [InlineData("Executable")] [InlineData("Route")]
    [InlineData("Files")] [InlineData("RelativePath")] [InlineData("Data")] [InlineData("Sha256")]
    public void MissingRequiredFieldsAreRefused(string field)
    {
        using var w = new TestWorkspace(); var store = Store(w);
        File.WriteAllText(w.FilePath("ReShade.ini"), "saved"); store.Snapshot(Route);
        Mutate(store, p => { if (!p.Remove(field)) p["Files"]![0]!.AsObject().Remove(field); });
        Assert.ThrowsAny<Exception>(() => store.Load(Route));
    }

    [Fact]
    public void IdentityChecksumDuplicatesAndFutureVersionAreRejected()
    {
        using var w = new TestWorkspace(); var store = Store(w);
        File.WriteAllText(w.FilePath("ReShade.ini"), "saved"); store.Snapshot(Route);
        var good = File.ReadAllText(store.ProfilePath(Route));
        foreach (var mutate in new Action<JsonObject>[] {
            p => p["Version"] = 2, p => p["GameRoot"] = w.Root + "other", p => p["Executable"] = "other.exe",
            p => p["Route"] = "DifferentRoute", p => p["Files"]![0]!["Sha256"] = "bad",
            p => p["Files"]![0]!["Data"] = "not base64!", p => p["Files"] = null,
            p => p["Files"]![0] = null, p => p["UnknownProperty"] = true,
            p => p["Files"]!.AsArray().Add(p["Files"]![0]!.DeepClone()) })
        {
            File.WriteAllText(store.ProfilePath(Route), good); Mutate(store, mutate);
            Assert.ThrowsAny<Exception>(() => store.Load(Route));
        }
        File.WriteAllText(store.ProfilePath(Route), good.Replace("\"Version\": 1", "\"Version\": 1, \"Version\": 1"));
        Assert.Throws<InvalidDataException>(() => store.Load(Route));
        File.WriteAllText(store.ProfilePath(Route), "null");
        Assert.ThrowsAny<Exception>(() => store.Load(Route));
        File.WriteAllText(store.ProfilePath(Route), "{broken");
        Assert.ThrowsAny<Exception>(() => store.Load(Route));
    }

    [Fact]
    public void FileAndProfileLimitsPreserveLastCompleteSnapshot()
    {
        using var w = new TestWorkspace(); var store = Store(w);
        File.WriteAllBytes(w.FilePath("ReShade.ini"), new byte[BackendProfileStore.MaxFileBytes]); store.Snapshot(Route);
        var good = File.ReadAllBytes(store.ProfilePath(Route));
        File.WriteAllBytes(w.FilePath("ReShade.ini"), new byte[BackendProfileStore.MaxFileBytes + 1]);
        Assert.Throws<InvalidDataException>(() => store.Snapshot(Route));
        Assert.Equal(good, File.ReadAllBytes(store.ProfilePath(Route)));
        Mutate(store, p => p["Files"]![0]!["Data"] = Convert.ToBase64String(new byte[BackendProfileStore.MaxFileBytes + 1]));
        Assert.Throws<InvalidDataException>(() => store.Load(Route));
        File.WriteAllBytes(store.ProfilePath(Route), new byte[BackendProfileStore.MaxProfileBytes + 1]);
        Assert.Throws<InvalidDataException>(() => store.Load(Route));
    }

    [Fact]
    public void AggregateSizeLimitAndLockedSnapshotSourcePreservePreviousSnapshot()
    {
        using var w = new TestWorkspace(); var store = Store(w);
        File.WriteAllText(w.FilePath("ReShade.ini"), "last good snapshot"); store.Snapshot(Route);
        var prior = File.ReadAllBytes(store.ProfilePath(Route));
        using (var locked = new FileStream(w.FilePath("ReShade.ini"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => store.Snapshot(Route));
        Assert.Equal(prior, File.ReadAllBytes(store.ProfilePath(Route)));
        foreach (var relative in store.ConfigurationPaths) Write(w, relative, new byte[BackendProfileStore.MaxFileBytes]);
        Assert.Throws<InvalidDataException>(() => store.Snapshot(Route));
        Assert.Equal(prior, File.ReadAllBytes(store.ProfilePath(Route)));
    }

    [Fact]
    public void StoreCountAndByteLimitsDoNotEvictExistingProfiles()
    {
        using var w = new TestWorkspace(); var store = Store(w);
        for (var i = 0; i < BackendProfileStore.MaxProfiles; i++) store.Snapshot("Route" + i);
        store.Snapshot("Route0");
        Assert.Throws<InvalidDataException>(() => store.Snapshot("Overflow"));
        Assert.Equal(BackendProfileStore.MaxProfiles, Directory.GetFiles(store.DirectoryPath).Length);
        using var other = new TestWorkspace(); var otherStore = Store(other);
        Directory.CreateDirectory(otherStore.DirectoryPath);
        using (var sparse = new FileStream(Path.Combine(otherStore.DirectoryPath, "existing.json"), FileMode.CreateNew))
            sparse.SetLength(BackendProfileStore.MaxStoreBytes);
        Assert.Throws<InvalidDataException>(() => otherStore.Snapshot(Route));
        Assert.False(File.Exists(otherStore.ProfilePath(Route)));
    }

    [Fact]
    public async Task ConcurrentStoreInstancesSerializeAtomicSnapshotAndLoad()
    {
        using var w = new TestWorkspace(); var store = Store(w);
        File.WriteAllBytes(w.FilePath("ReShade.ini"), Enumerable.Repeat((byte)0xff, 200 * 1024).ToArray());
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            var instance = new BackendProfileStore(w.Root, w.FilePath("game.exe"));
            instance.Snapshot(Route);
            Assert.Equal(200 * 1024, instance.Load(Route)["ReShade.ini"].Length);
        })));
        Assert.Single(Directory.GetFiles(store.DirectoryPath));
        Assert.Empty(Directory.GetFiles(store.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public void LockedLaterDestinationAndDirectoryConflictLeaveEarlierDestinationUntouched()
    {
        using var w = new TestWorkspace(); var store = Store(w);
        File.WriteAllText(w.FilePath("ReShade.ini"), "saved first");
        File.WriteAllText(w.FilePath("Phoenix.json"), "saved last"); store.Snapshot(Route);
        File.WriteAllText(w.FilePath("ReShade.ini"), "keep first");
        using (var locked = new FileStream(w.FilePath("Phoenix.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => store.Restore(Route, ["ReShade.ini", "Phoenix.json"]));
        Assert.Equal("keep first", File.ReadAllText(w.FilePath("ReShade.ini")));
        File.Delete(w.FilePath("Phoenix.json")); Directory.CreateDirectory(w.FilePath("Phoenix.json"));
        Assert.Throws<InvalidDataException>(() => store.Restore(Route, ["ReShade.ini", "Phoenix.json"]));
        Assert.Equal("keep first", File.ReadAllText(w.FilePath("ReShade.ini")));
        Assert.Empty(Directory.GetFiles(w.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void LinkedDestinationAndProfileStoreAreRejectedWithoutTouchingOutsideFiles()
    {
        using var w = new TestWorkspace(); var store = Store(w);
        Write(w, "host64/ReShade.ini", Encoding.UTF8.GetBytes("saved")); store.Snapshot(Route);
        Directory.Delete(w.FilePath("host64"), true);
        using var outside = new TestWorkspace(); File.WriteAllText(outside.FilePath("ReShade.ini"), "outside");
        SwapperTestLinks.CreateJunction(w.FilePath("host64"), outside.Root);
        try
        {
            Assert.Throws<IOException>(() => store.Restore(Route, ["host64/ReShade.ini"]));
            Assert.Equal("outside", File.ReadAllText(outside.FilePath("ReShade.ini")));
            Assert.Throws<IOException>(() => store.Snapshot(Route));
        }
        finally { Directory.Delete(w.FilePath("host64")); }
        Directory.Delete(store.DirectoryPath, true);
        SwapperTestLinks.CreateJunction(store.DirectoryPath, outside.Root);
        try { Assert.Throws<IOException>(() => store.Load(Route)); }
        finally { Directory.Delete(store.DirectoryPath); }
        Assert.Equal("outside", File.ReadAllText(outside.FilePath("ReShade.ini")));
    }

    [Theory]
    [InlineData("../route")] [InlineData("C:\\route")] [InlineData("")] [InlineData("route:stream")]
    public void UnsafeRouteNamesAreRejected(string route)
    {
        using var w = new TestWorkspace(); var store = Store(w);
        Assert.Throws<ArgumentException>(() => store.Snapshot(route));
        Assert.False(Directory.Exists(store.DirectoryPath));
    }
}
