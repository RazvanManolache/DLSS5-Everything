using System.Reflection;
using System.Text.Json;

namespace Dlss5CompatApp.Tests;

public sealed class GameInstallDefaultsTests
{
    [Fact]
    public void OlderSettingsKeepExistingCopyAndRendererPolicy()
    {
        var state = JsonSerializer.Deserialize<LibraryState>("{}")!;
        Assert.True(state.InstallationDefaults.CopyNvidiaDllToGame);
        Assert.Equal("native", state.InstallationDefaults.Renderer);
        Assert.Equal("small-fp8", state.InstallationDefaults.ModelId);
        Assert.Equal("auto", state.InstallationDefaults.ExecutionMode);
        var controls = PhoenixConfiguration.Controls(state.InstallationDefaults.Settings());
        Assert.Equal("small-fp8", controls["ModelId"]);
        Assert.Equal("0", controls["ComparisonLabels"]);
    }

    [Theory]
    [InlineData("native", "original", false)]
    [InlineData("nvidia", "small-fp8", true)]
    public void DefaultsPersistAndGenerateMatchingStartupControls(string renderer, string model, bool copy)
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath("library.json");
        var state = new LibraryState { InstallationDefaults = new() { Renderer = renderer,
            ModelId = model, CopyNvidiaDllToGame = copy } };
        state.Save(path);
        var loaded = LibraryState.Load(path);
        Assert.Equal(state.InstallationDefaults, loaded.InstallationDefaults);
        var settings = loaded.InstallationDefaults.Settings(7);
        Assert.Equal(copy, settings.CopyNvidiaDllToGame);
        Assert.Equal(7, settings.Iterations);
        var controls = PhoenixConfiguration.Controls(settings);
        Assert.Equal(renderer == "nvidia" ? "1" : "3", controls["Backend"]);
        Assert.Equal(model, controls["ModelId"]);
        Assert.Equal("0", controls["ComparisonLabels"]);
        Assert.Equal("-1", controls["NvidiaModel"]);
    }

    [Theory]
    [InlineData("unknown", "original")]
    [InlineData("native", "unknown")]
    public void InvalidDefaultsAreRejectedBeforeSaving(string renderer, string model)
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath("library.json");
        var state = new LibraryState { InstallationDefaults = new() { Renderer = renderer, ModelId = model } };
        Assert.Throws<InvalidDataException>(() => state.Save(path));
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SettingsWidgetsAreBoundAndFitSmallAndLargeWindows(bool small)
    {
        Exception? failure = null;
        var thread = new Thread(() => {
            try {
                using var form = new MainForm(preview: true, previewSmall: small, previewPage: "Settings");
                form.Show(); Application.DoEvents();
                T Field<T>(string name) => (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                var copy = Field<CheckBox>("_copyNvidiaDll");
                var renderer = Field<ComboBox>("_defaultRenderer");
                var model = Field<ComboBox>("_defaultNeuralModel");
                var execution = Field<ComboBox>("_defaultExecutionMode");
                Assert.Equal(1, model.SelectedIndex); // Fresh UI selects Lightweight.
                copy.Checked = false; renderer.SelectedIndex = 1; model.SelectedIndex = 1; execution.SelectedIndex = 4;
                var defaults = Field<LibraryState>("_libraryState").InstallationDefaults;
                Assert.False(defaults.CopyNvidiaDllToGame);
                Assert.Equal("nvidia", defaults.Renderer); Assert.Equal("small-fp8", defaults.ModelId);
                Assert.Equal("wow64_library", defaults.ExecutionMode);
                Assert.Equal("wow64_library", defaults.Settings().CompatibilityExecutionMode);
                foreach (var control in new Control[] { copy, renderer, model, execution }) {
                    Assert.True(control.Visible);
                    Assert.True(form.ClientRectangle.Contains(form.RectangleToClient(control.RectangleToScreen(control.ClientRectangle))));
                }
                var captureRoot = Environment.GetEnvironmentVariable("DLSS5_UI_CAPTURE_DIR");
                if (!string.IsNullOrWhiteSpace(captureRoot)) {
                    Directory.CreateDirectory(captureRoot);
                    foreach (var theme in new[] { "Dark", "Light" }) {
                        Field<ComboBox>("_themeChoice").SelectedItem = theme;
                        Application.DoEvents();
                        using var bitmap = new Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                        bitmap.Save(Path.Combine(captureRoot, $"startup-defaults-{theme.ToLowerInvariant()}-{(small ? "small" : "large")}.png"));
                    }
                }
                form.Close();
            } catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
