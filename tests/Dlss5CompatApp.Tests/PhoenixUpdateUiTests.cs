using System.Reflection;

namespace Dlss5CompatApp.Tests;

public sealed class PhoenixUpdateUiTests
{
    [Theory]
    [InlineData((int)PhoenixUpdateState.Current, "Phoenix 0.18 is current.")]
    [InlineData((int)PhoenixUpdateState.LocalBuild, "Imported local build; latest published: 0.18.")]
    [InlineData((int)PhoenixUpdateState.UpdateAvailable, "Phoenix update available: 0.18.")]
    [InlineData((int)PhoenixUpdateState.Unavailable, "Could not check Phoenix updates.")]
    public void NoticeStaysVisibleAcrossPagesAndActivityAndCanBeRechecked(int state, string message) => OnUiThread(() =>
    {
        using var workspace = new TestWorkspace();
        using var form = new MainForm(preview: true, payloadRoot: workspace.Root);
        var calls = 0;
        form.PhoenixUpdateCheckOverride = (_, _, _) => { Interlocked.Increment(ref calls); return Task.FromResult(new PhoenixUpdateResult((PhoenixUpdateState)state, message, "0.18")); };
        form.Show(); Application.DoEvents();
        var task = (Task)Call(form, "RefreshPhoenixUpdateAsync", true)!;
        PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult();
        var notice = Field<Label>(form, "_phoenixUpdateNotice");
        Assert.Equal(message, notice.Text); Assert.True(notice.Visible);
        Assert.True(Field<Button>(form, "_applyPhoenixUpdate").Visible);
        Call(form, "SetOutcome", "Unrelated installation completed");
        foreach (var page in new[] { "Library", "My Games", "Components", "Settings" })
        {
            Call(form, "ShowPage", page); Application.DoEvents();
            Assert.True(notice.Visible); Assert.Equal(message, notice.Text);
        }
        var previous = calls; Field<Button>(form, "_checkPhoenixUpdate").PerformClick();
        PumpUntil(() => calls > previous && Field<Button>(form, "_checkPhoenixUpdate").Enabled);
        var parent = notice.Parent!;
        Assert.True(parent.ClientRectangle.Contains(notice.Bounds), $"Notice {notice.Bounds} must fit update bar {parent.ClientRectangle}.");
        var capture = Environment.GetEnvironmentVariable("DLSS5_PHOENIX_UPDATE_UI_CAPTURE");
        if (!string.IsNullOrWhiteSpace(capture))
        {
            Directory.CreateDirectory(capture); Call(form, "ShowPage", "Library"); Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, form.ClientRectangle);
            bitmap.Save(Path.Combine(capture, ((PhoenixUpdateState)state) + ".png"));
        }
    });

    [Fact]
    public void OlderCheckCannotOverwriteANewerComponentLocation() => OnUiThread(() =>
    {
        using var workspace = new TestWorkspace(); using var form = new MainForm(preview: true, payloadRoot: workspace.Root);
        var first = new TaskCompletionSource<PhoenixUpdateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        form.PhoenixUpdateCheckOverride = (_, _, _) => Interlocked.Increment(ref calls) == 1 ? first.Task :
            Task.FromResult(new PhoenixUpdateResult(PhoenixUpdateState.Current, "New component location is current"));
        form.Show(); Application.DoEvents();
        var oldTask = (Task)Call(form, "RefreshPhoenixUpdateAsync", true)!; PumpUntil(() => calls == 1);
        Field<TextBox>(form, "_payloadRoot").Text = workspace.FilePath("other");
        var newTask = (Task)Call(form, "RefreshPhoenixUpdateAsync", true)!;
        PumpUntil(() => newTask.IsCompleted); newTask.GetAwaiter().GetResult();
        first.SetResult(new(PhoenixUpdateState.UpdateAvailable, "Stale response"));
        PumpUntil(() => oldTask.IsCompleted); oldTask.GetAwaiter().GetResult();
        Assert.Equal("New component location is current", Field<Label>(form, "_phoenixUpdateNotice").Text);
    });

    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    static object? Call(object target, string name, params object[] arguments) => target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, arguments);
    static void PumpUntil(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!ready() && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(10); }
        Assert.True(ready(), "Phoenix update notice did not settle.");
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
