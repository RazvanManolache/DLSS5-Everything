using System.Reflection;

namespace Dlss5CompatApp.Tests;

public sealed class ArtworkFallbackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalArtworkLoadsAndRendersWithSteamDisabledInBothGameViews(bool myGames)
    {
        OnUiThread(() =>
        {
            using var workspace = new TestWorkspace();
            var image = workspace.FilePath("game/cover.png");
            Directory.CreateDirectory(Path.GetDirectoryName(image)!);
            using (var bitmap = new Bitmap(64, 96))
            {
                using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.Orange);
                bitmap.Save(image, System.Drawing.Imaging.ImageFormat.Png);
            }
            using var form = new MainForm(preview: true, previewArtwork: true);
            var game = new GameCandidate(Path.GetDirectoryName(image)!, workspace.FilePath("game/game.exe"),
                "Example Fallback Fixture", "game.exe", CpuArch.X64, GraphicsApi.DirectX12, "fixture", 1000);
            Field<AppPreferences>(form, "_appPreferences").AutomaticArtwork = false;
            Field<AppPreferences>(form, "_appPreferences").UseTiles = true;
            SetField(form, "_usingMyGames", myGames);
            var rows = Field<ListView>(form, myGames ? "_myGames" : "_games");
            rows.Items.Clear();
            rows.Items.Add(new ListViewItem(game.DisplayName) { Tag = game });
            Call(form, "QueueArtwork");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            var fallback = Field<Dictionary<string, string>>(form, "_fallbackArtwork");
            while (!fallback.ContainsKey(game.ExePath) && DateTime.UtcNow < deadline)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
            Assert.True(LocalPaths.Same(image, fallback[game.ExePath]));
            Assert.Empty(Field<HashSet<string>>(form, "_artworkAttempted"));
            Assert.True(rows.Items[0].ImageIndex >= 0);
            using var displayed = new Bitmap(rows.LargeImageList!.Images[rows.Items[0].ImageIndex]);
            Assert.Equal(Color.Orange.ToArgb(), displayed.GetPixel(78, 117).ToArgb());
            using var rendered = (Bitmap)Call(form, "MakeConvenienceTile", image, game, false)!;
            Assert.Equal(Color.Orange.ToArgb(), rendered.GetPixel(78, 117).ToArgb());

            // A real cover arriving later must replace the fallback immediately.
            var remote = workspace.FilePath("downloaded.png");
            File.Copy(image, remote);
            Field<Dictionary<string, string>>(form, "_automaticArtwork")[game.ExePath] = remote;
            Assert.Equal(remote, Call(form, "ArtworkFor", game));
            var profile = Field<LibraryState>(form, "_libraryState");
            profile.Profiles[game.ExePath] = profile.ProfileFor(game.ExePath) with { ArtworkPath = image };
            Assert.Equal(image, Call(form, "ArtworkFor", game));
            File.Delete(image);
            Assert.Equal(remote, Call(form, "ArtworkFor", game));
        });
    }

    static void OnUiThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Artwork UI check timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    static void SetField(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
}
