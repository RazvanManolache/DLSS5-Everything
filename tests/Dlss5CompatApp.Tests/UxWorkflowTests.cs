using System.Reflection;
using System.Text.Json;

namespace Dlss5CompatApp.Tests;

public sealed class UxWorkflowTests
{
    static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Flags)!.GetValue(target)!;
    static object? Call(object target, string name, params object?[] arguments) => target.GetType().GetMethod(name, Flags)!.Invoke(target, arguments);
    static IEnumerable<Control> All(Control parent) => parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(All(child)));
    static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "UX test timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    static MainForm Form(int width = 1060, int height = 740)
    {
        var form = new MainForm(preview: true) { Size = new(width, height), Location = new(-20000, -20000), ShowInTaskbar = false, StartPosition = FormStartPosition.Manual };
        form.Show(); Application.DoEvents(); return form;
    }

    [Theory]
    [InlineData(1060, 740)]
    [InlineData(1400, 900)]
    public void LastAdvancedSelectorsAreActuallyReachableThroughTheScroller(int width, int height) => Sta(() =>
    {
        using var form = Form(width, height);
        All(form).OfType<CheckBox>().Single(control => control.AccessibleName == "Show advanced game options").Checked = true;
        var scroll = Field<Panel>(form, "_inspectorScroll");
        foreach (var name in new[] { "_routeChoice", "_proxyChoice" })
        {
            var target = Field<Control>(form, name);
            scroll.ScrollControlIntoView(target); target.Select(); Application.DoEvents();
            var bounds = scroll.RectangleToClient(target.RectangleToScreen(target.ClientRectangle));
            Assert.True(scroll.ClientRectangle.Contains(bounds), $"{name}: {bounds} outside {scroll.ClientRectangle}");
        }
        Assert.False(Field<Panel>(form, "_dropZone").Visible);
        Assert.True(form.AllowDrop);
    });

    [Fact]
    public void StopCheckCancelsOnlyTheCheckAndDoesNotCloseTheApplication() => Sta(() =>
    {
        using var form = Form(); using var check = new CancellationTokenSource();
        typeof(MainForm).GetField("_verificationCts", Flags)!.SetValue(form, check);
        Call(form, "UpdateVerificationProgress");
        Field<Button>(form, "_stopVerification").PerformClick();
        Assert.True(check.IsCancellationRequested);
        Assert.False(Field<CancellationTokenSource>(form, "_lifetimeCts").IsCancellationRequested);
        Assert.False(form.IsDisposed);
        Assert.False(Field<bool>(form, "_verificationCloseRequested"));
        Assert.False(Field<Button>(form, "_stopVerification").Enabled);
        typeof(MainForm).GetField("_verificationCts", Flags)!.SetValue(form, null);
    });

    [Fact]
    public void VerificationReceiptSurvivesRestartButRejectsChangedExecutableOrInstallation()
    {
        using var workspace = new TestWorkspace();
        var exe = workspace.WritePe("game.exe", CpuArch.X64, "d3d11.dll");
        var journal = workspace.FilePath("_DLSS5_Compat_Backup/manifest.json"); Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        File.WriteAllText(journal, "first installation");
        var game = new GameCandidate(workspace.Root, exe, "Fixture", "game.exe", CpuArch.X64, GraphicsApi.DirectX11, "fixture", 100);
        var receipts = workspace.FilePath("receipts"); var report = workspace.FilePath("report.json"); File.WriteAllText(report, "{}");
        GameVerificationReceipt.Save(game, true, "Output verified", report, receipts);
        Assert.True(GameVerificationReceipt.ReadCurrent(game, receipts)!.Passed);
        File.AppendAllText(exe, "changed"); Assert.Null(GameVerificationReceipt.ReadCurrent(game, receipts));
        GameVerificationReceipt.Save(game, false, "No output", report, receipts);
        Assert.False(GameVerificationReceipt.ReadCurrent(game, receipts)!.Passed);
        File.WriteAllText(journal, "replacement route"); Assert.Null(GameVerificationReceipt.ReadCurrent(game, receipts));
    }

    [Fact]
    public void CurrentResultReplacesCachedUnknownAndReinstallClearsIt() => Sta(() =>
    {
        using var form = Form();
        var game = (GameCandidate)Field<ListView>(form, "_games").SelectedItems[0].Tag!;
        Field<Dictionary<string, bool>>(form, "_installed")[game.ExePath] = true;
        Field<Dictionary<string, GameVerificationReceipt?>>(form, "_verificationReceipts")[game.ExePath] = null;
        Call(form, "RememberVerification", game, true, "Output verified", null);
        Assert.Equal("Output verified", Call(form, "GameInstallStatus", game));
        Call(form, "RecordHistory", game, HistoryAction.Install, true, "Reinstalled");
        Assert.Equal("Installed · unchecked", Call(form, "GameInstallStatus", game));
    });

    [Fact]
    public void DormantStrengthAndResolutionValuesArePreservedUntilEnabled() => Sta(() =>
    {
        using var workspace = new TestWorkspace();
        using var form = new PhoenixLiveForm(workspace.Root, colorOnlyCapture: true);
        var editors = Field<Dictionary<PhoenixSetting, Control>>(form, "editors");
        var tone = (NumericUpDown)editors[PhoenixSetting.Tone]; var saved = tone.Value;
        Assert.False(tone.Enabled);
        ((CheckBox)editors[PhoenixSetting.OverrideStrengths]).Checked = true;
        Assert.True(tone.Enabled); Assert.Equal(saved, tone.Value);
        Assert.False(editors[PhoenixSetting.CpuThreads].Enabled);
        Assert.False(editors[PhoenixSetting.ResolutionQuality].Enabled);
        Assert.Contains("Select Super resolution", Field<Dictionary<PhoenixSetting, Label>>(form, "dependencyLabels")[PhoenixSetting.ResolutionQuality].Text);
        Assert.Equal("Apply changes", Field<Button>(form, "apply").Text);
    });

    [Fact]
    public void BasicControlCaptionsRemainVisibleWhenAdvancedIsInitiallyHidden() => Sta(() =>
    {
        using var workspace = new TestWorkspace();
        using var form = new PhoenixLiveForm(workspace.Root) { Location = new(-20000, -20000), ShowInTaskbar = false };
        form.Show(); Application.DoEvents();
        var rows = Field<Dictionary<PhoenixSetting, Control>>(form, "fieldRows");
        foreach (var key in new[] { PhoenixSetting.Backend, PhoenixSetting.ExecutionDevice, PhoenixSetting.OverrideStrengths, PhoenixSetting.Tone })
            Assert.True(Assert.IsType<Label>(rows[key].Tag).Visible, key.ToString());
        Assert.False(rows[PhoenixSetting.WorkerEngine].Visible);
        Field<CheckBox>(form, "showAdvanced").Checked = true;
        Assert.True(Assert.IsType<Label>(rows[PhoenixSetting.WorkerEngine].Tag).Visible);
    });

    [Fact]
    public void OfflineApplyAndSaveIncludesPendingValues() => Sta(() =>
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(workspace.FilePath("ReShade.ini"), "[Phoenix]\nBackend=3\nOverrideStrengths=0\nTone=1\n");
        using var form = new PhoenixLiveForm(workspace.Root);
        var editors = Field<Dictionary<PhoenixSetting, Control>>(form, "editors");
        ((CheckBox)editors[PhoenixSetting.OverrideStrengths]).Checked = true;
        ((NumericUpDown)editors[PhoenixSetting.Tone]).Value = 1.5m;
        ((Task)Call(form, "ApplyAndSaveAsync")!).GetAwaiter().GetResult();
        var saved = File.ReadAllText(workspace.FilePath("ReShade.ini"));
        Assert.Contains("Tone=1.5", saved); Assert.Contains("OverrideStrengths=1", saved);
        Assert.Empty(Field<HashSet<PhoenixSetting>>(form, "dirty"));
        Assert.Contains("Offline settings saved", Field<Label>(form, "outcome").Text);
    });

    [Fact]
    public void ScreenPresetsDoNotOverwriteDefaultsOrPersistentOnOffStatus() => Sta(() =>
    {
        using var form = Form(); Call(form, "ShowPage", "Screen Studio");
        var view = Field<ScreenStudioView>(form, "_screenStudio");
        var preferences = Field<ScreenStudioPreferences>(view, "preferences"); var original = preferences.Settings;
        var state = Field<Label>(view, "state").Text;
        Assert.Equal("Saved for next capture", Field<Label>(view, "pending").Text);
        Assert.True(Field<Panel>(view, "everyday").Visible);
        Assert.True(Field<Button>(view, "screenshot").Visible);
        view.LoadSettings(original with { Tone = 1.5f }); Field<TextBox>(view, "presetName").Text = "Test preset";
        Call(view, "SavePreset");
        preferences = Field<ScreenStudioPreferences>(view, "preferences");
        Assert.Equal(original, preferences.Settings); Assert.Equal(1.5f, preferences.Presets["Test preset"].Tone);
        Call(view, "DeletePreset"); Assert.Equal(original, preferences.Settings);
        Assert.Equal(state, Field<Label>(view, "state").Text);
    });

    [Fact]
    public void RestoringSavedValuesClearsDirtyStateAndDependenciesUpdateImmediately() => Sta(() =>
    {
        using var form = Form(); Call(form, "ShowPage", "Screen Studio");
        var view = Field<ScreenStudioView>(form, "_screenStudio");
        var saved = Field<ScreenStudioPreferences>(view, "preferences").Settings;
        Field<TrackBar>(view, "tone").Value = 150;
        Assert.Equal("Unapplied changes", Field<Label>(view, "pending").Text);
        Field<TrackBar>(view, "tone").Value = (int)(saved.Tone * 100);
        Assert.Equal("Saved for next capture", Field<Label>(view, "pending").Text);
        Assert.False(Field<TrackBar>(view, "fov").Enabled);
        Field<CheckBox>(view, "lens").Checked = true;
        Assert.True(Field<TrackBar>(view, "fov").Enabled);
    });

    [Fact]
    public void StreamerOffersComponentsRecoveryWithoutStartingAListener() => Sta(() =>
    {
        var opened = false;
        using var form = new System.Windows.Forms.Form();
        using var view = new LanFrameView(() => null, () => "", preview: true, openComponents: () => opened = true);
        form.Controls.Add(view); form.Show(); Application.DoEvents();
        Assert.False(Field<Button>(view, "start").Enabled);
        Assert.Contains("Prepare Components", Field<Label>(view, "journey").Text);
        All(view).OfType<Button>().Single(button => button.AccessibleName == "Open Components for streaming prerequisites").PerformClick();
        Assert.True(opened);
    });

    [Fact]
    public void EssentialControlBoundariesHaveAtLeastThreeToOneContrast()
    {
        static double Luminance(Color color)
        {
            double Linear(byte value) { var channel = value / 255d; return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4); }
            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }
        foreach (var palette in new[] { UiPalette.Light, UiPalette.Dark })
        {
            var one = Luminance(palette.ControlBoundary); var two = Luminance(palette.Surface);
            Assert.True((Math.Max(one, two) + .05) / (Math.Min(one, two) + .05) >= 3);
        }
    }
}
