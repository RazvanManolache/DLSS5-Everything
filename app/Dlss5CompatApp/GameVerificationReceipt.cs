using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dlss5CompatApp;

/// <summary>A dated result for this executable and installation, not a general compatibility claim.</summary>
sealed record GameVerificationReceipt(string ExecutableSha256, string InstallationSha256, DateTimeOffset CheckedAt,
    bool Passed, string Message, string ReportPath)
{
    static string DirectoryPath => Path.Combine(LocalData.DirectoryPath, "verification-results");
    static string ReceiptPath(GameCandidate game, string? directory) => Path.Combine(directory ?? DirectoryPath,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(game.ExePath).ToUpperInvariant()))) + ".json");
    static string Hash(string path) { LocalPaths.RequireNoLinks(path); using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    static string Manifest(GameCandidate game) => Path.Combine(game.Root, "_DLSS5_Compat_Backup", "manifest.json");
    public static void Save(GameCandidate game, bool passed, string message, string report, string? directory = null)
    {
        var path = ReceiptPath(game, directory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        LocalPaths.RequireNoLinks(path);
        using var gate = LocalData.Lock(path);
        var receipt = new GameVerificationReceipt(Hash(game.ExePath), Hash(Manifest(game)), DateTimeOffset.UtcNow, passed, message, Path.GetFullPath(report));
        var temporary = path + ".tmp";
        LocalPaths.RequireNoLinks(temporary);
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(receipt)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static GameVerificationReceipt? ReadCurrent(GameCandidate game, string? directory = null)
    {
        try
        {
            var path = ReceiptPath(game, directory);
            if (!File.Exists(path)) return null;
            var receipt = JsonSerializer.Deserialize<GameVerificationReceipt>(LocalData.ReadText(path, 32768));
            return receipt is not null && receipt.CheckedAt != default && !string.IsNullOrWhiteSpace(receipt.Message) &&
                !string.IsNullOrWhiteSpace(receipt.ReportPath) && Path.IsPathFullyQualified(receipt.ReportPath) && receipt.ExecutableSha256 == Hash(game.ExePath) &&
                receipt.InstallationSha256 == Hash(Manifest(game)) ? receipt : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }
}
