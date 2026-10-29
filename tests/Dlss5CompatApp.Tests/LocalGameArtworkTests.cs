using System.Drawing.Imaging;

namespace Dlss5CompatApp.Tests;

public sealed class LocalGameArtworkTests
{
    [Fact]
    public async Task LocalCoverWinsOverOtherImagesAndNeverMakesNetworkRequests()
    {
        using var workspace = new TestWorkspace();
        var game = Game(workspace);
        Image(workspace.FilePath("game/texture.png"), Color.Blue);
        var cover = workspace.FilePath("game/cover.jpg");
        Image(cover, Color.Red);
        using var service = Service(workspace);
        var result = await service.GetLocalFallbackAsync(game);
        Assert.True(LocalPaths.Same(cover, result!.Path));
        Assert.Equal("Game folder image", result.Source);
        // Discovery leaves the game folder image available for edits/removal.
        File.Delete(cover);
        Assert.False(File.Exists(cover));
    }

    [Fact]
    public async Task ExecutableNamedImageHasPriorityAndGameRootArtworkWorksForNestedBinaries()
    {
        using var workspace = new TestWorkspace();
        var game = Game(workspace) with { ExePath = workspace.FilePath("game/bin/game.exe") };
        var cover = workspace.FilePath("game/artwork/cover.png");
        Image(cover, Color.Red);
        using var service = Service(workspace);
        Assert.True(LocalPaths.Same(cover, (await service.GetLocalFallbackAsync(game))!.Path));
        var named = workspace.FilePath("game/bin/game.png");
        Image(named, Color.Blue);
        Assert.True(LocalPaths.Same(named, (await service.GetLocalFallbackAsync(game))!.Path));
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpg")]
    [InlineData("bmp")]
    [InlineData("gif")]
    public async Task OtherValidNearbyImagesAreUsedWithoutRequiringSpecialNames(string extension)
    {
        using var workspace = new TestWorkspace();
        var image = workspace.FilePath("game/images/scenery." + extension);
        Image(image, Color.Green);
        using var service = Service(workspace);
        Assert.True(LocalPaths.Same(image, (await service.GetLocalFallbackAsync(Game(workspace)))!.Path));
    }

    [Fact]
    public async Task InvalidAndOversizedImagesDoNotHideAValidAlternative()
    {
        using var workspace = new TestWorkspace();
        var game = Game(workspace);
        File.WriteAllText(workspace.FilePath("game/game.png"), "not an image");
        using (var file = File.Create(workspace.FilePath("game/cover.png"))) file.SetLength(GameArtwork.MaximumImageBytes + 1);
        var valid = workspace.FilePath("game/splash.png");
        Image(valid, Color.Gold);
        using var service = Service(workspace);
        Assert.True(LocalPaths.Same(valid, (await service.GetLocalFallbackAsync(game))!.Path));
    }

    [Fact]
    public async Task DoesNotSearchUnrelatedParentImagesOrDeepTextureDirectories()
    {
        using var workspace = new TestWorkspace();
        Image(workspace.FilePath("cover.png"), Color.Red);
        Image(workspace.FilePath("game/assets/textures/cover.png"), Color.Blue);
        using var service = Service(workspace);
        Assert.Null(await service.GetLocalFallbackAsync(Game(workspace)));
    }

    [Fact]
    public async Task ExecutableIconIsCachedWithoutLockingTheExecutableAndAnAddedImageReplacesIt()
    {
        using var workspace = new TestWorkspace();
        var game = Game(workspace);
        // A real PE host provides an icon resource; no fixture executable is run.
        File.Copy(Environment.ProcessPath!, game.ExePath);
        using var service = Service(workspace);
        var icon = await service.GetLocalFallbackAsync(game);
        Assert.NotNull(icon);
        Assert.Equal("Game executable icon", icon.Source);
        Assert.True(File.Exists(icon.Path));
        Assert.Equal(icon.Path, (await service.GetLocalFallbackAsync(game))!.Path);
        using (File.Open(game.ExePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        var cover = workspace.FilePath("game/cover.png");
        Image(cover, Color.Red);
        Assert.True(LocalPaths.Same(cover, (await service.GetLocalFallbackAsync(game))!.Path));
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MissingFilesAndCancellationDoNotPublishCacheImages()
    {
        using var workspace = new TestWorkspace();
        using var service = Service(workspace);
        Assert.Null(await service.GetLocalFallbackAsync(Game(workspace)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetLocalFallbackAsync(Game(workspace), cancellation.Token));
        Assert.False(Directory.Exists(workspace.FilePath("cache/icons")));
    }

    static GameCandidate Game(TestWorkspace workspace)
    {
        Directory.CreateDirectory(workspace.FilePath("game"));
        return new(workspace.FilePath("game"), workspace.FilePath("game/game.exe"), "Game", "game.exe", CpuArch.X64, GraphicsApi.DirectX12, "fixture", 1000);
    }

    static GameArtwork Service(TestWorkspace workspace) => new(workspace.FilePath("cache"), new HttpClient(new RejectNetwork()), [], TimeSpan.Zero);

    static void Image(string path, Color color)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new Bitmap(64, 96);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(color);
        var format = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" => ImageFormat.Jpeg, ".bmp" => ImageFormat.Bmp, ".gif" => ImageFormat.Gif, _ => ImageFormat.Png
        };
        bitmap.Save(path, format);
    }

    sealed class RejectNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Local artwork must not access the network.");
    }
}
