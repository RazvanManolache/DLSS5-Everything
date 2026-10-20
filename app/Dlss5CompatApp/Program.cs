namespace Dlss5CompatApp;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--phoenix-control")
            return PhoenixControlCli.RunAsync(args).GetAwaiter().GetResult();

        if (args.Length > 0 && args[0] == "--verify-library")
            return LibraryVerification.RunFromArgsAsync(args).GetAwaiter().GetResult();

        if (args.Length > 0 && args[0] == "--inspect")
            return InspectionCli.RunAsync(args).GetAwaiter().GetResult();

        if (args.Length > 0 && args[0] == "--prepare-dos")
            return DosGameLauncher.PrepareFromArgs(args);

        if (args.Length == 2 && args[0] == "--setup-dos-graphics")
        {
            try { DosGraphicsSetup.EnsureAsync(PayloadScanner.Scan(Path.GetFullPath(args[1])),Console.WriteLine,CancellationToken.None).GetAwaiter().GetResult(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        }

        if (args.Length == 2 && args[0] == "--restore")
        {
            try { new InstallerEngine(Console.WriteLine).RestoreAsync(Path.GetFullPath(args[1])).GetAwaiter().GetResult(); return 0; }
            catch (Exception ex) { Console.Error.WriteLine("Restore failed: " + ex.Message); return 1; }
        }

        if (ComponentCli.IsImportCommand(args) || ComponentCli.IsUpdateCommand(args))
            return ComponentCli.RunAsync(args).GetAwaiter().GetResult();

        if (InstallerEngine.IsVulkanDisablePatchCommand(args))
            return InstallerEngine.RunVulkanDisablePatchCommand(args);

        if (args.Length > 0 && CliInstaller.IsInstallCommand(args))
            return CliInstaller.RunFromArgsAsync(args).GetAwaiter().GetResult();

        if (args.Length > 0 && SmokeTestRunner.IsSmokeTestCommand(args))
            return SmokeTestRunner.RunFromArgsAsync(args).GetAwaiter().GetResult();

        if (DosGameLauncher.IsDosLaunchCommand(args))
            return DosGameLauncher.RunFromArgsAsync(args).GetAwaiter().GetResult();

        ApplicationConfiguration.Initialize();
        var preview = args.Contains("--ui-preview", StringComparer.OrdinalIgnoreCase);
        var previewPageIndex = Array.FindIndex(args, a => a.Equals("--preview-page", StringComparison.OrdinalIgnoreCase));
        var previewPage = previewPageIndex >= 0 && previewPageIndex + 1 < args.Length ? args[previewPageIndex + 1] : "Library";
        string? Option(string name) {
            var index=Array.FindIndex(args,a => a.Equals(name,StringComparison.OrdinalIgnoreCase));
            return index>=0 && index+1<args.Length && !args[index+1].StartsWith("--",StringComparison.Ordinal) ? args[index+1] : null;
        }
        Application.Run(new MainForm(preview, previewPage, args.Contains("--preview-light"), args.Contains("--preview-small"),
            payloadRoot: Option("--payload-root"), previewArtwork: args.Contains("--preview-artwork"), initialPage: Option("--page"),
            startStreamer: args.Contains("--start-streamer"), streamerPairingFile: Option("--streamer-pairing-file")));
        return 0;
    }
}
