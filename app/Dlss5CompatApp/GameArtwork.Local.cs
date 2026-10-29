using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;

namespace Dlss5CompatApp;

sealed partial class GameArtwork
{
    // Local fallbacks are independent of Steam/network preferences. Probe only
    // nearby artwork folders, rather than walking a game's texture collection.
    internal Task<GameArtworkResult?> GetLocalFallbackAsync(GameCandidate game, CancellationToken token = default) =>
        Task.Run(() => FindLocalFallback(game, token), token);

    GameArtworkResult? FindLocalFallback(GameCandidate game, CancellationToken token)
    {
        var executableDirectory = System.IO.Path.GetDirectoryName(game.ExePath)!;
        var roots = new[] { executableDirectory, game.Root }.Distinct(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();
        foreach (var root in roots)
        foreach (var folder in new[] { root, System.IO.Path.Combine(root, "artwork"), System.IO.Path.Combine(root, "images") })
        {
            token.ThrowIfCancellationRequested();
            try
            {
                LocalPaths.RequireNoLinks(folder);
                if (!Directory.Exists(folder)) continue;
                candidates.AddRange(Directory.EnumerateFiles(folder).Where(path =>
                {
                    token.ThrowIfCancellationRequested();
                    return new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif" }.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());
                }).Take(256));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        }

        var executableName = System.IO.Path.GetFileNameWithoutExtension(game.ExePath);
        int Priority(string path) => System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant() switch
        {
            var name when name.Equals(executableName, StringComparison.OrdinalIgnoreCase) => 0,
            "cover" or "poster" or "boxart" or "folder" or "library_600x900" => 1,
            "header" or "banner" or "splash" or "logo" or "background" => 2,
            _ => 3
        };
        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(Priority).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).Take(64))
        {
            token.ThrowIfCancellationRequested();
            if (IsImage(path)) return new(path, "Game folder image", null);
        }

        token.ThrowIfCancellationRequested();
        try
        {
            LocalPaths.RequireNoLinks(game.ExePath);
            if (!File.Exists(game.ExePath)) return null;
            var info = new FileInfo(game.ExePath);
            // Include path and file identity: unrelated games can share a title,
            // and replacing an executable must invalidate its previous icon.
            var identity = string.Join('|', System.IO.Path.GetFullPath(game.ExePath).ToUpperInvariant(), info.Length, info.LastWriteTimeUtc.Ticks);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
            var destination = System.IO.Path.Combine(_cache, "icons", key + ".png");
            if (IsImage(destination)) return new(destination, "Game executable icon", null);
            using var icon = Icon.ExtractAssociatedIcon(game.ExePath);
            if (icon is null) return null;
            using var bitmap = icon.ToBitmap();
            token.ThrowIfCancellationRequested();
            LocalPaths.RequireNoLinks(destination);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                bitmap.Save(temporary, ImageFormat.Png);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, destination, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return new(destination, "Game executable icon", null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            OutOfMemoryException or System.Runtime.InteropServices.ExternalException) { return null; }
    }
}
