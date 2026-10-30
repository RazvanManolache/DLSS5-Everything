using System.Reflection;

namespace Dlss5CompatApp.Tests;

public sealed class AppUpdateUiTests
{
    [Fact]
    public void ClosingCancelsBothCompanionChecks() => OnUiThread(() =>
    {
        using var workspace = new TestWorkspace(); using var form = new MainForm(preview: true, payloadRoot: workspace.Root);
        var tokens = new System.Collections.Concurrent.ConcurrentBag<CancellationToken>();
        form.CompanionUpdateCheckOverride = async (_, _, _, token) =>
        {
            tokens.Add(token); await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Closing should cancel the request.");
        };
        form.Show(); PumpUntil(() => tokens.Count == 2);
        form.Dispose(); PumpUntil(() => !Field<bool>(form, "_checkingCompanions"));
        Assert.All(tokens, token => Assert.True(token.IsCancellationRequested));
    });

    [Fact]
    public void AllFourReleaseChecksHaveSeparateStatusesAndSafeLinks() => OnUiThread(() =>
    {
        using var workspace = new TestWorkspace(); using var form = new MainForm(preview: true, payloadRoot: workspace.Root);
        var opened = new List<string>();
        form.StartExternalProcess = start => { opened.Add(start.FileName); return null; };
        form.AppUpdateCheckOverride = (version, _) => Task.FromResult(new AppUpdateResult(AppUpdateStatus.UpdateAvailable,
            version, "v3.1.0-preview.6", AppUpdateService.ReleasesUrl));
        form.PhoenixUpdateCheckOverride = (_, _, _) => Task.FromResult(new PhoenixUpdateResult(PhoenixUpdateState.Current, "Phoenix 1.0-beta files verified."));
        form.CompanionUpdateCheckOverride = (project, _, _, _) => Task.FromResult(new CompanionUpdateResult(project,
            project == CompanionProject.DosBoxEnhanced ? CompanionUpdateState.UpdateAvailable : CompanionUpdateState.Current,
            project == CompanionProject.DosBoxEnhanced ? "DOSBox Enhanced: 0.6.2 → v1.0-beta. Update available." : "Video Player: 1.0-beta files verified.",
            "1.0-beta", "v1.0-beta", workspace.FilePath(project + "/app.exe")));
        form.Size = new Size(1400, 900); form.Show();
        PumpUntil(() => Field<LinkLabel>(form, "_componentUpdateNotice").Visible);
        var task = (Task)Call(form, "CheckAllReleaseUpdatesAsync")!;
        PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult();
        Call(form, "ShowPage", "Components"); Application.DoEvents();
        Assert.Contains("v3.1.0-preview.6", Field<LinkLabel>(form, "_everythingReleaseDetail").Text);
        Assert.Contains("1.0-beta files verified", Field<LinkLabel>(form, "_phoenixReleaseDetail").Text);
        var details = Field<Dictionary<CompanionProject, LinkLabel>>(form, "_companionReleaseDetails");
        Assert.Contains("0.6.2", details[CompanionProject.DosBoxEnhanced].Text);
        Assert.Contains("files verified", details[CompanionProject.VideoPlayer].Text);
        Assert.Contains("DosBoxEnhanced", Field<TextBox>(form, "_componentBinaryPaths").Text);
        Assert.Empty(opened);
        foreach (var project in Enum.GetValues<CompanionProject>())
            typeof(LinkLabel).GetMethod("OnLinkClicked", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(details[project], [new LinkLabelLinkClickedEventArgs(details[project].Links[0])]);
        Assert.Equal(Enum.GetValues<CompanionProject>().Select(project => CompanionUpdateService.ReleasesUrl(project).AbsoluteUri), opened);
        foreach (var name in new[] { "_appUpdateNotice", "_componentUpdateNotice" })
        {
            var notice = Field<LinkLabel>(form, name);
            Assert.True(notice.Visible); Assert.True(notice.Parent!.ClientRectangle.Contains(notice.Bounds));
        }
        var capture = Environment.GetEnvironmentVariable("DLSS5_APP_UPDATE_UI_CAPTURE");
        if (!string.IsNullOrWhiteSpace(capture))
        {
            Directory.CreateDirectory(capture);
            using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
            form.DrawToBitmap(bitmap, form.ClientRectangle); bitmap.Save(Path.Combine(capture, "all-release-checks.png"));
        }
    });

    [Theory]
    [InlineData(1060, false)]
    [InlineData(1400, true)]
    public void StartupNoticeStaysAcrossPagesAndOnlyClickOpensGitHub(int width, bool dark) => OnUiThread(() =>
    {
        using var workspace = new TestWorkspace();
        using var form = new MainForm(preview: true, payloadRoot: workspace.Root);
        int calls = 0, opens = 0;
        form.AppUpdateCheckOverride = (current, _) =>
        {
            Assert.Equal(AppUpdateService.CurrentVersion, current);
            Interlocked.Increment(ref calls);
            return Task.FromResult(new AppUpdateResult(AppUpdateStatus.UpdateAvailable, current,
                "v3.1.0-preview.6", AppUpdateService.ReleasesUrl));
        };
        form.StartExternalProcess = start =>
        {
            Assert.True(start.UseShellExecute);
            Assert.Equal(AppUpdateService.ReleasesUrl.AbsoluteUri, start.FileName);
            opens++; return null;
        };
        FieldInfo(form, "_palette").SetValue(form, dark ? UiPalette.Dark : UiPalette.Light);
        Call(form, "ApplyTheme");
        form.Size = new Size(width, 800); form.Show();
        var notice = Field<LinkLabel>(form, "_appUpdateNotice");
        PumpUntil(() => notice.Visible);
        Assert.Equal(1, calls); Assert.Equal(0, opens);
        Assert.Contains("v3.1.0-preview.6", notice.Text);
        Assert.Equal(dark ? UiPalette.Dark.Accent : UiPalette.Light.Accent, notice.LinkColor);
        Assert.True(notice.TabStop); Assert.NotEmpty(notice.AccessibleName!);
        Call(form, "SetOutcome", "Unrelated installation completed");
        foreach (var page in new[] { "My Games", "Library", "Components", "Settings" })
        {
            Call(form, "ShowPage", page); Application.DoEvents();
            Assert.True(notice.Visible);
            Assert.True(notice.Parent!.ClientRectangle.Contains(notice.Bounds), $"Update link {notice.Bounds} must fit {notice.Parent.ClientRectangle}.");
            var title = Field<Label>(form, "_pageTitle");
            Assert.False(notice.RectangleToScreen(notice.ClientRectangle).IntersectsWith(title.RectangleToScreen(title.ClientRectangle)));
        }
        // Exercise the actual link event, rather than the open method directly.
        typeof(LinkLabel).GetMethod("OnLinkClicked", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(notice, [new LinkLabelLinkClickedEventArgs(notice.Links[0])]);
        Assert.Equal(1, opens); Assert.True(notice.Visible);
        var cached = (Task)Call(form, "RefreshAppUpdateAsync", false)!;
        PumpUntil(() => cached.IsCompleted); cached.GetAwaiter().GetResult();
        Assert.Equal(1, calls);
        var manual = (Task)Call(form, "CheckAppUpdateAsync")!;
        PumpUntil(() => manual.IsCompleted); manual.GetAwaiter().GetResult();
        Assert.Equal(2, calls);
        var capture = Environment.GetEnvironmentVariable("DLSS5_APP_UPDATE_UI_CAPTURE");
        if (!string.IsNullOrWhiteSpace(capture))
        {
            Call(form, "ShowPage", "My Games"); Application.DoEvents();
            Directory.CreateDirectory(capture);
            using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
            form.DrawToBitmap(bitmap, form.ClientRectangle);
            bitmap.Save(Path.Combine(capture, $"update-notice-{width}-{(dark ? "dark" : "light")}.png"));
        }
    });

    [Fact]
    public void OfflineCheckIsQuietAndDoesNotEraseAKnownUpdate() => OnUiThread(() =>
    {
        using var workspace = new TestWorkspace(); using var form = new MainForm(preview: true, payloadRoot: workspace.Root);
        int calls = 0;
        form.AppUpdateCheckOverride = (current, _) => Task.FromResult(++calls == 2
            ? new AppUpdateResult(AppUpdateStatus.UpdateAvailable, current, "v9.0", AppUpdateService.ReleasesUrl)
            : new AppUpdateResult(AppUpdateStatus.Failed, current, null, AppUpdateService.ReleasesUrl, "offline"));
        form.Show(); PumpUntil(() => calls == 1 && !Field<bool>(form, "_checkingAppUpdate"));
        var notice = Field<LinkLabel>(form, "_appUpdateNotice");
        Assert.False(notice.Visible);
        Assert.Contains("Couldn't check", Field<Label>(form, "_updateStatus").Text);
        for (int index = 0; index < 2; index++)
        {
            var task = (Task)Call(form, "CheckAppUpdateAsync")!;
            PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult();
            Assert.True(notice.Visible); Assert.Contains("v9.0", notice.Text);
        }
        Assert.Contains("Couldn't check", Field<Label>(form, "_updateStatus").Text);
    });

    [Fact]
    public void DisposeCancelsPendingCheckAndDuplicateChecksDoNotOverlap() => OnUiThread(() =>
    {
        using var workspace = new TestWorkspace(); using var form = new MainForm(preview: true, payloadRoot: workspace.Root);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        form.AppUpdateCheckOverride = async (_, token) =>
        {
            Interlocked.Increment(ref calls); started.SetResult(token);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Cancellation should end this check.");
        };
        form.Show(); PumpUntil(() => started.Task.IsCompleted);
        var duplicate = (Task)Call(form, "CheckAppUpdateAsync")!;
        Assert.True(duplicate.IsCompletedSuccessfully); Assert.Equal(1, calls);
        form.Dispose();
        PumpUntil(() => !Field<bool>(form, "_checkingAppUpdate"));
        Assert.True(started.Task.Result.IsCancellationRequested);
    });

    [Fact]
    public void PreviewWithoutOverrideDoesNotStartNetworkOrTimer() => OnUiThread(() =>
    {
        using var workspace = new TestWorkspace(); using var form = new MainForm(preview: true, payloadRoot: workspace.Root);
        form.Show(); Application.DoEvents();
        Assert.False(Field<bool>(form, "_checkingAppUpdate"));
        Assert.False(Field<System.Windows.Forms.Timer>(form, "_appUpdateTimer").Enabled);
        Assert.False(Field<LinkLabel>(form, "_appUpdateNotice").Visible);
    });

    static FieldInfo FieldInfo(object target, string name) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
    static T Field<T>(object target, string name) => (T)FieldInfo(target, name).GetValue(target)!;
    static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);
    static void PumpUntil(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!ready() && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(10); }
        Assert.True(ready(), "Everything update check did not settle.");
    }
    static void OnUiThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            using var dispatcher = new Form { ShowInTaskbar = false, Opacity = 0 };
            dispatcher.Shown += (_, _) => dispatcher.BeginInvoke(() =>
            {
                try { action(); } catch (Exception error) { failure = error; } finally { dispatcher.Close(); }
            });
            Application.Run(dispatcher);
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(25)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
