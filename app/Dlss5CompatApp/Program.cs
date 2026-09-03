namespace Dlss5CompatApp;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (InstallerEngine.IsVulkanDisablePatchCommand(args))
            return InstallerEngine.RunVulkanDisablePatchCommand(args);

        if (args.Length > 0 && SmokeTestRunner.IsSmokeTestCommand(args))
            return SmokeTestRunner.RunFromArgsAsync(args).GetAwaiter().GetResult();

        if (DosGameLauncher.IsDosLaunchCommand(args))
            return DosGameLauncher.RunFromArgsAsync(args).GetAwaiter().GetResult();

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
