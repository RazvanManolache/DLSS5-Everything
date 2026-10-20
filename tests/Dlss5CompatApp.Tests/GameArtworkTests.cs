using System.Drawing.Imaging;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Dlss5CompatApp.Tests;

public sealed class GameArtworkTests
{
    [Theory]
    [InlineData("Control", "Star Control", null)]
    [InlineData("Control", "Control", 42u)]
    [InlineData("DOOM Eternal", "DOOM Eternal", 42u)]
    [InlineData("Spider-Man 3", "Spider Man 3", 42u)]
    [InlineData("Call of Duty 4: Modern Warfare", "Call of Duty 4 Modern Warfare", 42u)]
    [InlineData("Game Two", "Game Three", null)]
    public void TitleMatchingRejectsWeakMatches(string query, string title, uint? expected)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { items = new[] { new { id = 42, name = title, type = "app" } } }));
        Assert.Equal(expected, GameArtwork.PickAppId(document.RootElement, query));
    }

    [Theory]
    [InlineData("{\"items\":[{\"id\":\"42\",\"name\":\"Control\"}]}")]
    [InlineData("{\"items\":[{\"id\":42,\"name\":\"Control\",\"type\":\"dlc\"}]}")]
    [InlineData("{\"items\":[{\"id\":42,\"name\":\"Control\",\"type\":42}]}")]
    [InlineData("{\"items\":null}")]
    public void UnusableSearchRowsDoNotBecomeArtwork(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Null(GameArtwork.PickAppId(document.RootElement, "Control"));
    }

    [Fact]
    public void OriginalTitleWinsOverAReorderedSequelWithTheSameWords()
    {
        using var document = JsonDocument.Parse("{\"items\":[{\"id\":4435490,\"name\":\"Call of Duty®: Modern Warfare® 4\",\"type\":\"app\"},{\"id\":7940,\"name\":\"Call of Duty® 4: Modern Warfare® (2007)\",\"type\":\"app\"}]}");
        Assert.Equal(7940u, GameArtwork.PickAppId(document.RootElement, "Call of Duty 4: Modern Warfare"));
    }

    [Fact]
    public async Task SearchUsesOnlyTitleAndCachesAValidatedImage()
    {
        using var workspace = new TestWorkspace();
        var requests = new List<Uri>();
        using var client = Client(request =>
        {
            requests.Add(request.RequestUri!);
            return request.RequestUri!.Host == "store.steampowered.com" ? Json("{\"items\":[{\"id\":7940,\"name\":\"Call of Duty 4: Modern Warfare\",\"type\":\"app\"}]}") : ImageResponse();
        });
        using var service = Service(workspace, client);
        var game = Game(workspace);
        var result = await service.GetAsync(game);
        Assert.NotNull(result);
        Assert.True(GameArtwork.IsImage(result.Path));
        Assert.Equal(7940u, result.SteamAppId);
        Assert.Equal(2, requests.Count);
        Assert.Contains("term=Call%20of%20Duty", requests[0].AbsoluteUri);
        Assert.DoesNotContain("exe", requests[0].AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(workspace.Root, string.Join(" ", requests));
        Assert.Equal("shared.cloudflare.steamstatic.com", requests[1].Host);
        Assert.Equal(result.Path, (await service.GetAsync(game))!.Path);
        Assert.Equal(2, requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SteamManifestAndLocalArtworkAvoidAllNetworkRequests(bool flat)
    {
        using var workspace = new TestWorkspace();
        var root = workspace.FilePath("Steam/steamapps/common/SomeFolder");
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        File.WriteAllText(workspace.FilePath("Steam/steamapps/appmanifest_7940.acf"), "\"AppState\" { \"appid\" \"7940\" \"installdir\" \"SomeFolder\" }");
        var art = workspace.FilePath(flat ? "Steam/appcache/librarycache/7940_library_600x900.jpg" : "Steam/appcache/librarycache/7940/library_600x900.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(art)!);
        File.WriteAllBytes(art, ImageBytes());
        using var client = Client(_ => throw new InvalidOperationException("No network expected"));
        using var service = Service(workspace, client);
        var result = await service.GetAsync(Game(workspace) with { Root = root, ExePath = Path.Combine(root, "bin", "game.exe"), DisplayName = "Incorrect executable metadata" });
        Assert.NotNull(result);
        Assert.Equal(7940u, result.SteamAppId);
        Assert.Equal("Steam local library", result.Source);
        Assert.True(File.Exists(art));
        Assert.True(GameArtwork.IsImage(result.Path));
    }

    [Fact]
    public async Task MissingPosterFallsBackToHeaderAndNeverTrustsSearchSuppliedUrls()
    {
        using var workspace = new TestWorkspace();
        var requests = new List<string>();
        using var client = Client(request =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            if (request.RequestUri.Host == "store.steampowered.com") return Json("{\"items\":[{\"id\":7940,\"name\":\"Call of Duty 4: Modern Warfare\",\"image\":\"https://untrusted.example/image.jpg\"}]}");
            return request.RequestUri.AbsolutePath.EndsWith("header.jpg") ? ImageResponse() : new(HttpStatusCode.NotFound);
        });
        using var service = Service(workspace, client);
        Assert.NotNull(await service.GetAsync(Game(workspace)));
        Assert.Equal(3, requests.Count);
        Assert.DoesNotContain(requests, uri => uri.Contains("untrusted"));
    }

    [Fact]
    public async Task MissingMatchesAreCachedWithoutDownloadingTheWrongCover()
    {
        using var workspace = new TestWorkspace();
        var count = 0;
        using var client = Client(_ => { count++; return Json("{\"items\":[{\"id\":1,\"name\":\"Unrelated game\"}]}"); });
        using var service = Service(workspace, client);
        Assert.Null(await service.GetAsync(Game(workspace)));
        Assert.Null(await service.GetAsync(Game(workspace)));
        Assert.Equal(1, count);
        Assert.Null(await service.GetAsync(Game(workspace), retryMissing: true));
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task OversizedDownloadIsRejectedBeforeCachePublication()
    {
        using var workspace = new TestWorkspace();
        using var client = Client(request =>
        {
            if (request.RequestUri!.Host == "store.steampowered.com") return Json("{\"items\":[{\"id\":7940,\"name\":\"Call of Duty 4: Modern Warfare\"}]}");
            var response = ImageResponse();
            response.Content.Headers.ContentLength = GameArtwork.MaximumImageBytes + 1;
            return response;
        });
        using var service = Service(workspace, client);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(Game(workspace)));
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.tmp", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.jpg", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task InvalidImageAndCancellationNeverPublishCacheEntries()
    {
        using var workspace = new TestWorkspace();
        using var client = Client(request => request.RequestUri!.Host == "store.steampowered.com" ?
            Json("{\"items\":[{\"id\":7940,\"name\":\"Call of Duty 4: Modern Warfare\"}]}") : Json("Not an image"));
        using var service = Service(workspace, client);
        Assert.Null(await service.GetAsync(Game(workspace)));
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.jpg", SearchOption.AllDirectories));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetAsync(Game(workspace), cancelled.Token));
    }

    [Fact]
    public void AssetNamesCannotEscapeTheSteamCdnAndPreferencePersists()
    {
        Assert.Throws<ArgumentException>(() => GameArtwork.AssetUri(42, "../../private"));
        Assert.Throws<ArgumentException>(() => GameArtwork.AssetUri(0, "header.jpg"));
        using var workspace = new TestWorkspace();
        var file = workspace.FilePath("preferences.json");
        var preferences = AppPreferences.Load(file);
        Assert.True(preferences.AutomaticArtwork);
        preferences.AutomaticArtwork = false;
        preferences.Save(file);
        Assert.False(AppPreferences.Load(file).AutomaticArtwork);
    }

    static GameCandidate Game(TestWorkspace workspace) => new(workspace.Root, workspace.FilePath("Private/game.exe"), "Call of Duty 4: Modern Warfare", "game.exe", CpuArch.X86, GraphicsApi.DirectX9, "test", 1000);
    static GameArtwork Service(TestWorkspace workspace, HttpClient http) => new(workspace.FilePath("cache"), http, [], TimeSpan.Zero);
    static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> handler) => new(new Handler(handler));
    static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    static HttpResponseMessage ImageResponse() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(ImageBytes()) };
    static byte[] ImageBytes()
    {
        using var bitmap = new Bitmap(100, 150);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.ForestGreen);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Jpeg);
        return stream.ToArray();
    }
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
}
