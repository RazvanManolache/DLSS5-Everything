using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Dlss5CompatApp;

/// <summary>Provision the pinned vendor runtime directly; no shell or script policy changes.</summary>
static class DosGraphicsDownload
{
    const long MaxArchiveBytes = 512L * 1024 * 1024;
    static readonly string[] Notices = ["license.txt", "3rd-party-licenses.md", "nvngx_dlss.license.txt", "reflex.license.txt"];

    internal static async Task ProvisionAsync(string work, string model, CancellationToken token,
        HttpClient? client = null, Action<string>? verifySignature = null)
    {
        verifySignature ??= VerifySignedFile;
        verifySignature(model);
        using var manifest = JsonDocument.Parse(LocalData.ReadText(Path.Combine(work, "runtime.lock.json"), 65536));
        var pin = manifest.RootElement.GetProperty("nvidia");
        var url = new Uri(pin.GetProperty("url").GetString()!);
        var expectedHash = pin.GetProperty("archiveSha256").GetString()!;
        if (url.Scheme != "https" || url.Host != "github.com" || url.Port != 443 || url.UserInfo.Length != 0 ||
            !url.AbsolutePath.StartsWith("/NVIDIA-RTX/Streamline/releases/download/", StringComparison.Ordinal) ||
            expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid pinned NVIDIA SDK location or checksum.");
        var files = DosGraphicsSetup.ExpectedFiles(work);
        if (files.Length is < 1 or > 64 || files.Select(file => file.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length)
            throw new InvalidDataException("Invalid NVIDIA runtime dependency list.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(4));
        using var ownedClient = client is null ? new HttpClient() : null;
        client ??= ownedClient!;
        var archivePath = Path.Combine(work, "nvidia-sdk.zip");
        using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxArchiveBytes)
                throw new InvalidDataException("NVIDIA SDK download exceeds its size limit.");
            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
            await using var destination = File.Create(archivePath);
            await CopyBoundedAsync(source, destination, MaxArchiveBytes, timeout.Token);
        }
        await using (var archive = File.OpenRead(archivePath))
            if (!Convert.ToHexString(await SHA256.HashDataAsync(archive, timeout.Token)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("NVIDIA SDK archive checksum mismatch.");
        var vendor = Path.Combine(work, "runtime", "nvidia");
        Directory.CreateDirectory(vendor);
        using var zip = ZipFile.OpenRead(archivePath);
        foreach (var file in files)
        {
            var target = Path.Combine(vendor, file.Name);
            await ExtractAsync("bin/x64/" + file.Name, target, 128L * 1024 * 1024);
            await using (var input = File.OpenRead(target))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(input, timeout.Token)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("NVIDIA runtime checksum mismatch: " + file.Name);
            verifySignature(target);
        }
        foreach (var name in Notices)
            await ExtractAsync(name is "license.txt" or "3rd-party-licenses.md" ? name : "bin/x64/" + name,
                Path.Combine(vendor, name), 8L * 1024 * 1024);

        async Task ExtractAsync(string name, string target, long limit)
        {
            var matches = zip.Entries.Where(entry => entry.FullName == name).ToArray();
            if (matches.Length != 1 || matches[0].Length > limit)
                throw new InvalidDataException("Missing, duplicate or oversized NVIDIA SDK member: " + name);
            await using var input = matches[0].Open();
            await using var output = File.Create(target);
            await CopyBoundedAsync(input, output, limit, timeout.Token);
        }
    }

    static async Task CopyBoundedAsync(Stream input, Stream output, long limit, CancellationToken token)
    {
        var buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            if ((total += count) > limit) throw new InvalidDataException("Graphics dependency exceeds its size limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
    }

    // WINTRUST_ACTION_GENERIC_VERIFY_V2, WTD_CHOICE_FILE and VERIFY/CLOSE follow
    // https://learn.microsoft.com/windows/win32/seccrypto/example-c-program--verifying-the-signature-of-a-pe-file
    internal static void VerifySignedFile(string path)
    {
        LocalPaths.RequireNoLinks(path);
        var fileName = Marshal.StringToCoTaskMemUni(Path.GetFullPath(path));
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        var trust = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1,
            FileInfo = filePointer, StateAction = 1 };
        try
        {
            Marshal.StructureToPtr(new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = fileName }, filePointer, false);
            var status = WinVerifyTrust(new IntPtr(-1), ref action, ref trust);
            if (status != 0) throw new InvalidDataException($"Graphics file signature could not be verified (0x{status:X8}): {Path.GetFileName(path)}");
        }
        finally
        {
            trust.StateAction = 2;
            WinVerifyTrust(new IntPtr(-1), ref action, ref trust);
            Marshal.FreeHGlobal(filePointer);
            Marshal.FreeCoTaskMem(fileName);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TrustFile { public uint Size; public IntPtr Path, File, KnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    struct TrustData
    {
        public uint Size;
        public IntPtr PolicyCallback, SipClient;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr State, UrlReference;
        public uint ProviderFlags, UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}
