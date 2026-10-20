using System.Net;
using System.Text.Json;

namespace Dlss5CompatApp.Tests;

public sealed class OptionalModelLibraryTests
{
    [Fact]
    public void CatalogContainsOnlyNativeFullFrameExports()
    {
        Assert.Equal(new[] { "original", "small-fp8" }, OptionalModelLibrary.Catalog.Select(p => p.Model));
        Assert.All(OptionalModelLibrary.Catalog, p => Assert.All(p.Files, f =>
        {
            Assert.Matches("^[a-f0-9]{64}$", f.Sha256);
            Assert.DoesNotContain("onnx", f.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".py", f.Name, StringComparison.OrdinalIgnoreCase);
        }));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("models/../../escape")]
    [InlineData("C:/escape")]
    [InlineData("models/a:stream")]
    public void LibraryPathsCannotEscapeTheCentralRoot(string path)
    {
        using var workspace = new TestWorkspace();
        Assert.Throws<InvalidDataException>(() => OptionalModelLibrary.Contained(workspace.Root, path));
    }

    internal static string PublishFixture(string payloadRoot)
    {
        var root = OptionalModelLibrary.Root(payloadRoot);
        var folder = Path.Combine(root, "models/fixture"); Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "weights.bin"), [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(folder, "model.json"), JsonSerializer.Serialize(new
        {
            schema = "dlss5-phoenix-model/1", model = "original", architecture = "phoenix-network37/1",
            resource = new { path = "weights.bin", bytes = 4, sha256 = OptionalModelLibrary.HashFile(Path.Combine(folder, "weights.bin")) }
        }));
        File.WriteAllText(Path.Combine(root, OptionalModelLibrary.IndexName), JsonSerializer.Serialize(new
        {
            schema = "phoenix-model-library/1", models = new[] { new InstalledModelPackage("canonical-original", "Fixture Original", "original", "models/fixture/model.json", OptionalModelLibrary.HashFile(Path.Combine(folder, "model.json"))) }
        }));
        return root;
    }

    [Fact]
    public void MissingOrChangedModelDescriptorsAreNotAvailable()
    {
        using var workspace = new TestWorkspace(); var root = PublishFixture(workspace.Root);
        Assert.Single(OptionalModelLibrary.Installed(root, verifyWeights: true));
        File.AppendAllText(Path.Combine(root, "models/fixture/model.json"), " ");
        Assert.Empty(OptionalModelLibrary.Installed(root));
        root = PublishFixture(workspace.Root); File.Delete(Path.Combine(root, "models/fixture/weights.bin"));
        Assert.Empty(OptionalModelLibrary.Installed(root));
    }

    [Fact]
    public void OneUnavailablePackageDoesNotHideAnotherInstalledModel()
    {
        using var workspace = new TestWorkspace(); var root = PublishFixture(workspace.Root);
        var path = Path.Combine(root, OptionalModelLibrary.IndexName);
        var valid = OptionalModelLibrary.Installed(root).Single();
        var invalid = valid with { id = "canonical-lightweight", model = "small-fp8", manifest = "../escape" };
        File.WriteAllText(path, JsonSerializer.Serialize(new { schema = "phoenix-model-library/1", models = new[] { invalid, valid } }));
        Assert.Equal(valid, OptionalModelLibrary.Installed(root, verifyWeights: true).Single());
    }

    [Fact]
    public async Task BadDownloadIsNotPublishedAndKeepsExistingModels()
    {
        using var workspace = new TestWorkspace(); var root = PublishFixture(workspace.Root);
        var index = File.ReadAllBytes(Path.Combine(root, OptionalModelLibrary.IndexName));
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([4, 3, 2, 1]) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => OptionalModelLibrary.EnsureSelectedAsync(workspace.Root,
            ["canonical-lightweight"], new Progress<BootstrapProgress>(), CancellationToken.None, http));
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(root, OptionalModelLibrary.IndexName)));
        Assert.Single(OptionalModelLibrary.Installed(root, verifyWeights: true));
        Assert.Empty(Directory.GetDirectories(root, ".install-*"));
    }

    [Fact]
    public async Task VerifiedModelsAreReusedWithoutNetworkAndCancellationDoesNotReplaceThem()
    {
        using var workspace = new TestWorkspace(); var root = PublishFixture(workspace.Root);
        using var http = new HttpClient(new Handler(_ => throw new Exception("No download expected")));
        await OptionalModelLibrary.EnsureSelectedAsync(workspace.Root, ["canonical-original"], new Progress<BootstrapProgress>(), CancellationToken.None, http);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OptionalModelLibrary.EnsureSelectedAsync(workspace.Root,
            ["canonical-lightweight"], new Progress<BootstrapProgress>(), cancellation.Token, http));
        Assert.Single(OptionalModelLibrary.Installed(root, verifyWeights: true));
    }

    [Fact]
    public void InstallerCarriesTheSharedModelPathInConfigurationAndControls()
    {
        using var workspace = new TestWorkspace(); var model = workspace.WritePe("model.dll", CpuArch.X64);
        var library = Path.Combine(workspace.Root, "shared Models"); var settings = new PhoenixSettings { ModelLibraryDirectory = library };
        Assert.Equal(library, PhoenixConfiguration.Create(workspace.Root, model, settings)["model_library_directory"]);
        Assert.Equal(library, PhoenixConfiguration.Controls(settings)["ModelLibraryDirectory"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ComponentsShowsBoundOptionalDownloadCheckboxes(bool small)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var workspace = new TestWorkspace();
                using var form = new MainForm(preview: true, previewPage: "Components", previewSmall: small, payloadRoot: workspace.Root)
                { StartPosition = FormStartPosition.Manual, Location = new(-20000, -20000), ShowInTaskbar = false };
                form.Show(); Application.DoEvents();
                static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
                var checks = Descendants(form).OfType<CheckBox>().Where(c => c.AccessibleName?.StartsWith("Download ") == true).ToArray();
                Assert.Equal(2, checks.Length);
                Assert.All(checks, check => { Assert.True(check.Visible); Assert.False(check.Checked); Assert.True(check.Width > 200); });
                checks[0].Checked = true;
                var method = typeof(MainForm).GetMethod("SelectedOptionalModels", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                Assert.Equal(new[] { "canonical-original" }, (string[])method.Invoke(form, null)!);
                if (Environment.GetEnvironmentVariable("DLSS5_OPTIONAL_MODEL_CAPTURE_DIR") is { Length: > 0 } output)
                {
                    Directory.CreateDirectory(output);
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    bitmap.Save(Path.Combine(output, "components-model-library-" + (small ? "small" : "large") + ".png"));
                }
                form.Close();
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
