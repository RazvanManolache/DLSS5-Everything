namespace Dlss5CompatApp;

static class Program
{
    [STAThread]
    static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && SmokeTestRunner.IsSmokeTestCommand(args))
            return await SmokeTestRunner.RunFromArgsAsync(args);

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
