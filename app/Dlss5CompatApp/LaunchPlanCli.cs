using System.Text.Json;

namespace Dlss5CompatApp;

/// <summary>Read-only launch settings shared with the GUI, without starting a process.</summary>
static class LaunchPlanCli
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length < 2) throw new ArgumentException("Use --launch-plan <game> --output <json> [--arguments <text>] [--api <GraphicsApi>] [--renderer <local-remix-backend>].");
            var executable=LocalPaths.Normalize(Path.GetFullPath(args[1]));
            string? output=null;string arguments="";string? renderer=null;
            GraphicsApi? apiOverride=null;
            for(var index=2;index<args.Length;index+=2)
            {
                if(index+1>=args.Length)throw new ArgumentException("Missing launch-plan option value.");
                if(args[index]=="--output")output=Path.GetFullPath(args[index+1]);
                else if(args[index]=="--arguments")arguments=args[index+1];
                else if(args[index]=="--renderer")renderer=Path.GetFullPath(args[index+1]);
                else if(args[index]=="--api")
                {
                    if(!Enum.TryParse<GraphicsApi>(args[index+1],true,out var api) || !Enum.IsDefined(api) || api==GraphicsApi.Unknown)
                        throw new ArgumentException("--api needs a supported graphics API name.");
                    apiOverride=api;
                }
                else throw new ArgumentException("Unknown launch-plan option: "+args[index]);
            }
            if(output is null)throw new ArgumentException("--output is required.");
            if(renderer is not null && apiOverride is not null && apiOverride!=GraphicsApi.Vulkan)
                throw new ArgumentException("The verified Remix renderer uses Vulkan.");
            if(renderer is not null) ValidateRemixRenderer(executable,renderer);
            var detected=GameScanner.ScanSingleExe(renderer ?? executable,renderer is null ? apiOverride : GraphicsApi.Vulkan) ?? throw new InvalidOperationException("Renderer executable was not identified.");
            var game=InstallerEngine.InstalledGame(detected);
            var start=GameLauncher.Create(game,arguments);
            if(renderer is not null)
            {
                var frontend=GameScanner.ScanSingleExe(executable) ?? throw new InvalidOperationException("Frontend executable was not identified.");
                var frontendStart=GameLauncher.Create(frontend,arguments);
                foreach(var pair in EnvironmentChanges(start.Environment)) frontendStart.Environment[pair.Key]=pair.Value;
                start=frontendStart;
            }
            var changes=EnvironmentChanges(start.Environment);
            LocalPaths.RequireNoLinks(output);
            File.WriteAllText(output,JsonSerializer.Serialize(new { Schema="dlss5-launch-plan/1",Executable=start.FileName,
                WorkingDirectory=start.WorkingDirectory,Arguments=start.Arguments,Environment=changes,RendererExecutable=renderer }));
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error.Message);return 1;}
    }

    internal static Dictionary<string,string> EnvironmentChanges(IDictionary<string,string?> environment)
        => environment.Where(pair=>pair.Value is not null && pair.Value!=System.Environment.GetEnvironmentVariable(pair.Key))
            .ToDictionary(pair=>pair.Key,pair=>pair.Value!,StringComparer.OrdinalIgnoreCase);

    internal static void ValidateRemixRenderer(string frontend,string renderer)
    {
        var root=Path.GetDirectoryName(frontend)!;
        var expected=Path.Combine(root,"bin",".trex","NvRemixBridge.exe");
        var bridge=Path.Combine(root,"bin","d3d9.dll");
        LocalPaths.RequireNoLinks(frontend);LocalPaths.RequireNoLinks(renderer);LocalPaths.RequireNoLinks(bridge);
        if(!LocalPaths.Same(renderer,expected) || PeReader.GetArchitecture(frontend)!=CpuArch.X86 ||
            PeReader.GetArchitecture(renderer)!=CpuArch.X64 || PeReader.GetArchitecture(bridge)!=CpuArch.X86 ||
            !PayloadScanner.HasExports(bridge,"Direct3DCreate9","remixapi_InitializeLibrary","remixapi_RegisterCallbacks"))
            throw new InvalidOperationException("--renderer requires the selected frontend's verified local RTX Remix backend.");
    }
}
