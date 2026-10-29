namespace Dlss5CompatApp;

sealed record ComponentDownloadStage(string Name, int Weight, Func<IProgress<BootstrapProgress>, CancellationToken, Task> Download);

static class ComponentSetupDownload
{
    public static Task EnsureAllAsync(string root, IProgress<BootstrapProgress> progress, CancellationToken token,
        PhoenixReleaseChannel channel, bool replaceLocalDevelopment = false) => RunAsync(CreateStages(
            (report, cancel) => PayloadBootstrapper.EnsureCurrentAsync(root, report, cancel,
                channel: channel, replaceLocalDevelopment: replaceLocalDevelopment),
            (package, report, cancel) => OptionalModelLibrary.EnsureSelectedAsync(root, [package.Id], report, cancel),
            (report, cancel) => TinyModelLibrary.EnsureAsync(root, report, cancel)), progress, token);

    internal static IReadOnlyList<ComponentDownloadStage> CreateStages(
        Func<IProgress<BootstrapProgress>, CancellationToken, Task> runtimes,
        Func<OptionalModelPackage, IProgress<BootstrapProgress>, CancellationToken, Task> model,
        Func<IProgress<BootstrapProgress>, CancellationToken, Task> tiny)
    {
        var stages = new List<ComponentDownloadStage> { new("Runtimes and NVIDIA model DLL", 70, runtimes) };
        // Use the whole catalog, regardless of selections in the advanced page.
        foreach (var package in OptionalModelLibrary.Catalog)
            stages.Add(new(package.Label, Math.Max(1, (int)(package.Files[0].MaxBytes / 10000000)),
                (report, cancel) => model(package, report, cancel)));
        stages.Add(new("Tiny native 2x upscaler", 1, tiny));
        return stages;
    }

    internal static async Task RunAsync(IReadOnlyList<ComponentDownloadStage> stages,
        IProgress<BootstrapProgress> progress, CancellationToken token)
    {
        if (stages.Count == 0 || stages.Any(stage => stage.Weight <= 0))
            throw new ArgumentException("Download stages must have positive weights.", nameof(stages));
        var total = stages.Sum(stage => stage.Weight);
        var completed = 0;
        var lastPercent = 0;
        var progressLock = new object();
        var errors = new List<string>();
        foreach (var stage in stages)
        {
            token.ThrowIfCancellationRequested();
            var offset = completed;
            // Providers report per-file progress, sometimes restarting at zero.
            // Keep one monotonic overall bar; reserve 100% for verified success.
            var report = new StageProgress(item =>
            {
                var percent = Math.Min(99, (offset * 100 + stage.Weight * Math.Clamp(item.Percent, 0, 100)) / total);
                lock (progressLock)
                {
                    lastPercent = Math.Max(lastPercent, percent);
                    progress.Report(new(stage.Name + ": " + item.Message, lastPercent));
                }
            });
            report.Report(new("Checking downloads…", 0));
            try { await stage.Download(report, token); }
            catch (OperationCanceledException error) when (!token.IsCancellationRequested)
            { errors.Add(stage.Name + ": download interrupted or timed out. " + error.Message); }
            catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
            { errors.Add(stage.Name + ": " + error.Message); }
            completed += stage.Weight;
        }
        token.ThrowIfCancellationRequested();
        if (errors.Count != 0) throw new InvalidOperationException("Downloads incomplete. " + string.Join("; ", errors));
        progress.Report(new("All components and models are ready.", 100));
    }

    sealed class StageProgress(Action<BootstrapProgress> report) : IProgress<BootstrapProgress>
    {
        public void Report(BootstrapProgress value) => report(value);
    }
}
