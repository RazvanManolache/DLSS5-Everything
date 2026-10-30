using System.Reflection;

namespace Dlss5CompatApp.Tests;

public sealed class WorkspacePopupTests
{
    [Theory]
    [InlineData(1060, true)]
    [InlineData(1400, false)]
    public void LibraryStaysAvailableWhileSharedComponentAndGameOptionsOpenInWindows(int width, bool light) => OnUiThread(() =>
    {
        using var workspace = new TestWorkspace();
        using var form = new MainForm(preview: true, previewPage: "My Games", previewLight: light, payloadRoot: workspace.Root);
        form.InstallEvidence = _ => true;
        form.PhoenixUpdateCheckOverride = (_, _, _) => Task.FromResult(new PhoenixUpdateResult(PhoenixUpdateState.Current,
            "Phoenix 1.0-beta is current.", "1.0-beta", "1.0-beta:Windows.zip"));
        form.CompanionUpdateCheckOverride = (project, _, _, _) => Task.FromResult(new CompanionUpdateResult(project,
            CompanionUpdateState.Current, "Published files verified.", "1.0-beta", "1.0-beta", workspace.FilePath("app.exe")));
        form.Size = new Size(width, 850); form.Show(); Application.DoEvents();
        var check = (Task)Call(form, "RefreshPhoenixUpdateAsync", true)!;
        PumpUntil(() => check.IsCompleted); check.GetAwaiter().GetResult();
        Call(form, "ShowPage", "My Games"); Application.DoEvents();
        var versions = Field<LinkLabel>(form, "_componentVersions");
        var bounds = form.RectangleToClient(versions.RectangleToScreen(versions.ClientRectangle));
        Assert.True(versions.Visible); Assert.True(form.ClientRectangle.Contains(bounds));
        Assert.True(bounds.Left < form.ClientSize.Width / 3 && bounds.Bottom > form.ClientSize.Height / 2);
        Assert.Contains("1.0-beta", versions.Text);
        Assert.False(Field<Label>(form, "_phoenixUpdateNotice").Visible);
        Assert.False(Field<Button>(form, "_setupComponents").Visible);
        Assert.False(Field<Dictionary<string, Button>>(form, "_navigation").ContainsKey("Components"));

        var sort = Descendants(form).OfType<Button>().Single(button => button.Name == "MyGamesSort");
        var toolbar = (TableLayoutPanel)sort.Parent!;
        var refresh = toolbar.GetControlFromPosition(4, 0)!;
        Assert.Equal(refresh.Top, sort.Top); Assert.Equal(refresh.Height, sort.Height);
        Capture(form, $"library-{width}-{(light ? "light" : "dark")}");

        var active = Field<string>(form, "_activePage");
        Call(form, "ShowComponentManager"); Application.DoEvents();
        var manager = Field<Form>(form, "_componentManager");
        Assert.True(manager.Visible); Assert.True(form.Enabled); Assert.Equal(active, Field<string>(form, "_activePage"));
        var shared = Field<Button>(form, "_setupComponents");
        Assert.True(shared.Visible); Assert.True(manager.Contains(shared.Parent!.Parent!.Parent));
        var channel = Field<ComboBox>(form, "_releaseChannel");
        Assert.True(channel.Visible); Assert.True(channel.Parent!.ClientSize.Height >= channel.Height);
        Assert.True(manager.ClientRectangle.Contains(manager.RectangleToClient(channel.RectangleToScreen(channel.ClientRectangle))));
        Capture(manager, $"components-{width}-{(light ? "light" : "dark")}");
        manager.Close(); Assert.False(manager.Visible); Assert.False(manager.IsDisposed);
        Call(form, "ShowComponentManager");
        Assert.Same(shared, Field<Button>(form, "_setupComponents")); manager.Close();

        Field<NumericUpDown>(form, "_frameIterations").Value = 3;
        Field<CheckBox>(form, "_forceVrEyeSplit").Checked = true;
        var summary = Field<Label>(form, "_gameCustomSummary");
        Assert.True(summary.Visible); Assert.Contains("3", summary.Text); Assert.Contains("VR eye split", summary.Text);
        Capture(form, $"library-custom-{width}-{(light ? "light" : "dark")}");
        Call(form, "ShowAdvancedGameOptions"); Application.DoEvents();
        var advanced = Field<Form>(form, "_gameOptionsDialog");
        Assert.True(advanced.Visible); Assert.True(form.Enabled);
        foreach (var name in new[] { "_engineChoice", "_launchArguments", "_frameIterations", "_forceVrEyeSplit", "_routeChoice", "_proxyChoice", "_saveProfile" })
            Assert.Contains(Field<Control>(form, name), Descendants(advanced));
        Capture(advanced, $"advanced-game-{width}-{(light ? "light" : "dark")}");
        advanced.Close(); Assert.False(advanced.IsDisposed); Assert.False(advanced.Visible);
        Assert.Contains("3", summary.Text);
        Call(form, "ShowPage", "Settings"); Application.DoEvents();
        Assert.False(Field<CheckBox>(form, "_defaultComparisonLabels").Checked);
        Capture(form, $"settings-defaults-{width}-{(light ? "light" : "dark")}");
    });

    [Fact]
    public void InitialMissingComponentsRequestCompleteSetupAndPopupDisposalReleasesSharedControls() => OnUiThread(() =>
    {
        using var workspace = new TestWorkspace(); var form = new MainForm(preview: true, payloadRoot: workspace.Root);
        form.Show(); Application.DoEvents();
        Assert.True((bool)Call(form, "NeedsInitialComponentSetup")!);
        Call(form, "ShowComponentManager");
        var popup = Field<Form>(form, "_componentManager"); var button = Field<Button>(form, "_setupComponents");
        form.Dispose(); Assert.True(popup.IsDisposed); Assert.True(button.IsDisposed);
    });

    static void Capture(Control control, string name)
    {
        var directory = Environment.GetEnvironmentVariable("DLSS5_WORKSPACE_CAPTURE"); if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory); using var bitmap = new Bitmap(control.ClientSize.Width, control.ClientSize.Height);
        control.DrawToBitmap(bitmap, control.ClientRectangle); bitmap.Save(Path.Combine(directory, name + ".png"));
    }
    static IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);
    static void PumpUntil(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(12);
        while (!ready() && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(10); }
        Assert.True(ready(), "UI operation did not complete.");
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
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
