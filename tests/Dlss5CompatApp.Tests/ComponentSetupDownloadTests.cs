namespace Dlss5CompatApp.Tests;

public sealed class ComponentSetupDownloadTests
{
    [Fact]
    public async Task FullSetupIncludesRuntimesEveryCatalogModelAndTinyWithMonotonicProgress()
    {
        var calls = new List<string>();
        Task Download(string id, IProgress<BootstrapProgress> progress, CancellationToken token)
        {
            calls.Add(id);
            foreach (var percent in new[] { 0, 50, 100, 0, 90, 100 }) progress.Report(new(id, percent));
            return Task.CompletedTask;
        }
        var stages = ComponentSetupDownload.CreateStages((p, t) => Download("runtimes", p, t),
            (package, p, t) => Download(package.Id, p, t), (p, t) => Download("tiny", p, t));
        var sink = new ProgressSink();
        await ComponentSetupDownload.RunAsync(stages, sink, default);
        Assert.Equal(new[] { "runtimes" }.Concat(OptionalModelLibrary.Catalog.Select(p => p.Id)).Append("tiny"), calls);
        Assert.Equal(100, sink.Items[^1].Percent);
        Assert.All(sink.Items.SkipLast(1), item => Assert.InRange(item.Percent, 0, 99));
        Assert.Equal(sink.Items.Select(item => item.Percent).Order(), sink.Items.Select(item => item.Percent));
    }

    [Fact]
    public async Task FailedRuntimeStillAttemptsAllModelsAndReportsIncompleteInsteadOfSuccess()
    {
        var models = new List<string>(); var tiny = false;
        var stages = ComponentSetupDownload.CreateStages((_, _) => throw new IOException("runtime unavailable"),
            (package, _, _) => { models.Add(package.Id); return Task.CompletedTask; },
            (_, _) => { tiny = true; return Task.CompletedTask; });
        var sink = new ProgressSink();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ComponentSetupDownload.RunAsync(stages, sink, default));
        Assert.Contains("runtime unavailable", error.Message);
        Assert.Equal(OptionalModelLibrary.Catalog.Select(p => p.Id), models);
        Assert.True(tiny);
        Assert.DoesNotContain(sink.Items, item => item.Percent == 100);
    }

    [Fact]
    public async Task CancellationStopsBeforeOtherDownloads()
    {
        using var cancellation = new CancellationTokenSource();
        var models = 0;
        var stages = ComponentSetupDownload.CreateStages((_, _) => { cancellation.Cancel(); return Task.CompletedTask; },
            (_, _, _) => { models++; return Task.CompletedTask; }, (_, _) => Task.CompletedTask);
        var sink = new ProgressSink();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ComponentSetupDownload.RunAsync(stages, sink, cancellation.Token));
        Assert.Equal(0, models);
        Assert.DoesNotContain(sink.Items, item => item.Percent == 100);
    }

    [Fact]
    public async Task ProviderTimeoutIsAReportedFailureWithoutCancellingOtherDownloads()
    {
        var tiny = false;
        var stages = ComponentSetupDownload.CreateStages((_, _) => throw new TaskCanceledException("timeout"),
            (_, _, _) => Task.CompletedTask, (_, _) => { tiny = true; return Task.CompletedTask; });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ComponentSetupDownload.RunAsync(stages, new ProgressSink(), default));
        Assert.Contains("timed out", error.Message);
        Assert.True(tiny);
    }

    sealed class ProgressSink : IProgress<BootstrapProgress>
    {
        public List<BootstrapProgress> Items { get; } = [];
        public void Report(BootstrapProgress value) => Items.Add(value);
    }
}
