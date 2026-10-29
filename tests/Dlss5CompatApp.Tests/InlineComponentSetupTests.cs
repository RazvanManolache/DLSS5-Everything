using System.Reflection;

namespace Dlss5CompatApp.Tests;

public sealed class InlineComponentSetupTests
{
    [Theory]
    [InlineData("Library", "success")]
    [InlineData("Library", "failure")]
    [InlineData("Library", "cancel")]
    [InlineData("My Games", "success")]
    [InlineData("Streamer", "success")]
    public void QuickSetupStaysOnItsPageShowsProgressAndHandlesCompletion(string page, string outcome) => OnUiThread(() =>
    {
        using var workspace = new TestWorkspace();
        using var form = new MainForm(preview: true, payloadRoot: workspace.Root);
        var calls = 0;
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<BootstrapProgress>? report = null;
        form.ComponentSetupOverride = async (progress, token) =>
        {
            calls++; report = progress;
            progress.Report(new("Downloading model bytes", 42));
            await complete.Task.WaitAsync(token);
            if (outcome == "failure") throw new IOException("Fixture download failed");
        };
        form.Show(); Application.DoEvents();
        Call(form, "ShowPage", page);
        if (page == "My Games") Field<TextBox>(form, "_myGamesSearch").Text = "No fixture should match this search";
        Application.DoEvents();
        Call(form, "UpdateButtons");
        var panels = Field<List<ComponentSetupPanel>>(form, "_componentSetupPanels");
        var panel = page switch
        {
            "Library" => panels.Single(p => ReferenceEquals(p.DownloadButton, Field<Button>(form, "_setupComponents"))),
            "Streamer" => panels.Single(p => Ancestor(p, Field<LanFrameView>(form, "_streamer"))),
            _ => panels.Single(p => Ancestor(p, Field<FlowLayoutPanel>(form, "_myGamesEmpty")))
        };
        Assert.True(panel.DownloadButton.Visible);
        panel.DownloadButton.PerformClick();
        PumpUntil(() => panel.ProgressBar.Value == 42);
        Assert.Equal(page, Field<string>(form, "_activePage"));
        Assert.True(panel.ProgressBar.Visible);
        Assert.True(panel.CancelButton.Visible);
        Assert.True(panel.ProgressBar.Width >= 40);
        Assert.True(panel.ClientRectangle.Contains(panel.RectangleToClient(panel.ProgressBar.RectangleToScreen(panel.ProgressBar.ClientRectangle))), "Progress bar must fit beside its setup button.");
        Assert.Contains("Downloading model bytes", panel.Status.Text);
        Assert.False(panel.DownloadButton.Enabled);
        Capture(panel, page + "-" + outcome + "-downloading");
        panel.DownloadButton.PerformClick(); Assert.Equal(1, calls);
        if (outcome == "cancel") panel.CancelButton.PerformClick(); else complete.SetResult();
        PumpUntil(() => Field<CancellationTokenSource?>(form, "_downloadCts") is null);
        Assert.Equal(page, Field<string>(form, "_activePage"));
        Assert.True(panel.DownloadButton.Enabled, $"Status={panel.Status.Text}, running={Field<bool>(panel, "running")}, busy={Field<bool>(form, "_busy")}, previewOverride={form.ComponentSetupOverride is not null}, disposed={form.IsDisposed}, parentEnabled={panel.Enabled}");
        Assert.False(panel.CancelButton.Visible);
        if (outcome == "success")
        {
            Assert.Equal(100, panel.ProgressBar.Value);
            Assert.Contains("All components and models", panel.Status.Text);
            Assert.All(Field<Dictionary<string, CheckBox>>(form, "_optionalModelChoices").Values, item => Assert.True(item.Checked));
            Assert.True(Field<CheckBox>(form, "_tinyModelDownload").Checked);
        }
        else
        {
            Assert.True(panel.ProgressBar.Value < 100);
            Assert.Equal("Retry download", panel.DownloadButton.Text);
            Assert.Contains(outcome == "cancel" ? "cancelled" : "Fixture download failed", panel.Status.Text);
        }
        var finalStatus = panel.Status.Text;
        report!.Report(new("Late progress must not overwrite the outcome", 99));
        Application.DoEvents(); Assert.Equal(finalStatus, panel.Status.Text);
        Capture(panel, page + "-" + outcome + "-finished");
        form.Close();
    });

    static void Capture(Control control, string name)
    {
        if (Environment.GetEnvironmentVariable("DLSS5_SETUP_CAPTURE_ROOT") is not { Length: > 0 } root) return;
        Directory.CreateDirectory(root);
        using var bitmap = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(bitmap, control.ClientRectangle);
        bitmap.Save(Path.Combine(root, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
    }

    static bool Ancestor(Control child, Control parent)
    {
        for (var current = child.Parent; current is not null; current = current.Parent) if (ReferenceEquals(current, parent)) return true;
        return false;
    }

    static void PumpUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(10); }
        Assert.True(condition(), "Inline download state did not settle.");
    }

    static void OnUiThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            using var dispatcher = new Form { ShowInTaskbar = false, Opacity = 0 };
            dispatcher.Shown += (_, _) => dispatcher.BeginInvoke(() =>
            {
                try { action(); } catch (Exception error) { failure = error; }
                finally { dispatcher.Close(); }
            });
            Application.Run(dispatcher);
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(25)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    static void Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
}
