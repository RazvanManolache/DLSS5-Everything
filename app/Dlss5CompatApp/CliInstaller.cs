namespace Dlss5CompatApp;

static class CliInstaller
{
    internal const string AntiCheatAcknowledgementFlag = "--acknowledge-anti-cheat-risk";

    public static bool IsInstallCommand(string[] args) =>
        args.Length > 0 &&
        (args[0].Equals("--install", StringComparison.OrdinalIgnoreCase) ||
         args[0].Equals("install", StringComparison.OrdinalIgnoreCase));

    public static async Task<int> RunFromArgsAsync(string[] args)
    {
        try
        {
            var options = Parse(args);
            if (string.IsNullOrWhiteSpace(options.ExePath))
                throw new ArgumentException("Usage: Dlss5CompatApp.exe --install <game.exe> [--payload <folder>] [--api <GraphicsApi>] [--renderer <local-remix-backend>] [--proxy dxgi.dll|d3d11.dll] [--force-vr-eye-split] [--frame-iterations 1-10] [" + AntiCheatAcknowledgementFlag + "]");

            var exePath = Path.GetFullPath(options.ExePath);
            var payloadRoot = Path.GetFullPath(options.PayloadRoot ?? Path.Combine(AppContext.BaseDirectory, "Payload"));
            Directory.CreateDirectory(payloadRoot);

            void Log(string message) => Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message);
            var progress = new Progress<BootstrapProgress>(item =>
            {
                if (!string.IsNullOrWhiteSpace(item.Message))
                    Log(item.Message);
            });

            Log("Checking payload in " + payloadRoot);
            if (!options.Offline) await PayloadBootstrapper.EnsureCurrentAsync(payloadRoot, progress, CancellationToken.None);

            var payload = PayloadScanner.Scan(payloadRoot);
            var game = DetectInstallTarget(exePath, options);
            if (options.ProxyOverride is not null) game = game with { ProxyName = options.ProxyOverride };
            game.ValidateOverrides();

            Log("Detected: " + game.DisplayName + " | " + game.Arch + " | " + game.Api + " | " + (PackagedPhoenixPlan.For(game, payload)?.Description ?? game.DisplayRoute));
            var missing = payload.MissingFor(game);
            if (!string.IsNullOrWhiteSpace(missing))
                throw new InvalidOperationException("Payload is incomplete: " + missing);

            var engine = new InstallerEngine(Log);
            RequirePreflightAcknowledgement(InstallerEngine.CheckPreflight(game, payload), options.AcknowledgeAntiCheatRisk, Log);
            var result = await engine.InstallAsync(game, payload, options.ForceVrEyeSplit, options.FrameIterations);
            if (!result.Complete) { Console.Error.WriteLine(result.Warning); return 2; }
            Log("Install complete.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Install failed: " + ex.Message);
            return 1;
        }
    }

    internal static GameCandidate DetectInstallTarget(string frontend, CliInstallOptions options)
    {
        var target = frontend;
        var api = options.ApiOverride;
        if (options.RendererPath is not null)
        {
            if (api is not null && api != GraphicsApi.Vulkan)
                throw new ArgumentException("The verified Remix renderer uses Vulkan.");
            target = Path.GetFullPath(options.RendererPath);
            LaunchPlanCli.ValidateRemixRenderer(frontend, target);
            api = GraphicsApi.Vulkan;
        }
        return GameScanner.ScanSingleExe(target, api)
            ?? throw new InvalidOperationException("Could not detect a supported game route for: " + target);
    }

    internal static void RequirePreflightAcknowledgement(InstallPreflightResult checks, bool acknowledgeAntiCheatRisk, Action<string> log)
    {
        checks.ThrowIfBlocked();
        if (checks.HasWarnings) log(checks.DisplayText);
        if (checks.RequiresAntiCheatAcknowledgement && !acknowledgeAntiCheatRisk)
            throw new InvalidOperationException("Anti-cheat acknowledgement required. Review the game's policy and use "
                + AntiCheatAcknowledgementFlag + " only if you accept the risk.\n" + checks.DisplayText);
    }

    internal static CliInstallOptions Parse(string[] args)
    {
        var options = new CliInstallOptions();
        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--offline", StringComparison.OrdinalIgnoreCase))
            {
                options.Offline = true;
            }
            else if (arg.Equals("--payload", StringComparison.OrdinalIgnoreCase))
            {
                options.PayloadRoot = RequiredValue(args, ref i, arg);
            }
            else if (arg.Equals("--api", StringComparison.OrdinalIgnoreCase))
            {
                var value=RequiredValue(args,ref i,arg);
                if (!Enum.TryParse<GraphicsApi>(value,true,out var api) || !Enum.IsDefined(api) || api==GraphicsApi.Unknown)
                    throw new ArgumentException("--api needs a supported graphics API name, for example DirectX9 or OpenGl.");
                options.ApiOverride=api;
            }
            else if (arg.Equals("--renderer", StringComparison.OrdinalIgnoreCase))
            {
                options.RendererPath = RequiredValue(args, ref i, arg);
            }
            else if (arg.Equals("--proxy", StringComparison.OrdinalIgnoreCase))
            {
                options.ProxyOverride=RequiredValue(args,ref i,arg);
                if (GameCandidate.OverrideValueValidationError(null, options.ProxyOverride) is { } error)
                    throw new ArgumentException(error);
            }
            else if (arg.Equals("--force-vr-eye-split", StringComparison.OrdinalIgnoreCase))
            {
                options.ForceVrEyeSplit = true;
            }
            else if (arg.Equals(AntiCheatAcknowledgementFlag, StringComparison.OrdinalIgnoreCase))
            {
                options.AcknowledgeAntiCheatRisk = true;
            }
            else if (arg.Equals("--frame-iterations", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(RequiredValue(args, ref i, arg), out var value))
                    throw new ArgumentException("--frame-iterations must be a number from 1 to 10.");
                options.FrameIterations = Math.Clamp(value, 1, 10);
            }
            else if (string.IsNullOrWhiteSpace(options.ExePath))
            {
                options.ExePath = arg;
            }
            else
            {
                throw new ArgumentException("Unexpected argument: " + arg);
            }
        }

        return options;
    }

    static string RequiredValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException(optionName + " needs a value.");
        index++;
        return args[index];
    }

    internal sealed class CliInstallOptions
    {
        public string? ExePath { get; set; }
        public string? PayloadRoot { get; set; }
        public string? RendererPath { get; set; }
        public GraphicsApi? ApiOverride { get; set; }
        public string? ProxyOverride { get; set; }
        public bool Offline { get; set; }
        public bool ForceVrEyeSplit { get; set; }
        public bool AcknowledgeAntiCheatRisk { get; set; }
        public int FrameIterations { get; set; } = 1;
    }
}
