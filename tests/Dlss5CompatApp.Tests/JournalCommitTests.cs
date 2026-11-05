using System.Reflection;

namespace Dlss5CompatApp.Tests;

public sealed class JournalCommitTests
{
    static void Save(string root) => typeof(InstallerEngine)
        .GetMethod("WritePending", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, [root, new InstallManifest { GameRoot = root }]);

    [Fact]
    public async Task JournalCommitSurvivesBriefReaderWithoutDeleteSharing()
    {
        using var workspace = new TestWorkspace();
        var directory = workspace.FilePath("_DLSS5_Compat_Backup");
        Directory.CreateDirectory(directory);
        var pending = Path.Combine(directory, "pending.json");
        File.WriteAllText(pending, "old durable journal");
        using var holder = new FileStream(pending, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var started = new ManualResetEventSlim();
        var work = Task.Run(() => { started.Set(); Save(workspace.Root); });
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        await Task.Delay(200);
        Assert.False(work.IsCompleted);
        holder.Dispose();
        await work.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(workspace.Root.Replace("\\", "\\\\"), File.ReadAllText(pending));
        Assert.Empty(Directory.EnumerateFiles(directory, "pending.json.tmp-*"));
    }

    [Fact]
    public void PermanentJournalLockStopsAndPreservesPreviousBytes()
    {
        using var workspace = new TestWorkspace();
        var directory = workspace.FilePath("_DLSS5_Compat_Backup");
        Directory.CreateDirectory(directory);
        var pending = Path.Combine(directory, "pending.json");
        File.WriteAllText(pending, "old durable journal");
        using var holder = new FileStream(pending, FileMode.Open, FileAccess.Read, FileShare.Read);
        var failure = Assert.Throws<TargetInvocationException>(() => Save(workspace.Root));
        Assert.True(failure.InnerException is IOException or UnauthorizedAccessException);
        Assert.Equal("old durable journal", File.ReadAllText(pending));
        Assert.Single(Directory.EnumerateFiles(directory, "pending.json.tmp-*"));
    }
}
