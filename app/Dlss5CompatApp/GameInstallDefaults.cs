namespace Dlss5CompatApp;

/// <summary>Defaults for generated game startup settings, not live GPU tuning.</summary>
sealed record GameInstallDefaults
{
    // Preserve the existing deployment policy for older saved libraries.
    public bool CopyNvidiaDllToGame { get; init; } = true;
    public string Renderer { get; init; } = "native";
    public string ModelId { get; init; } = "original";
    public string ExecutionMode { get; init; } = "auto";

    public void Validate()
    {
        if (Renderer is not ("native" or "nvidia"))
            throw new InvalidDataException("Default renderer must be Phoenix DLSS5 or NVIDIA DLSS5.");
        if (ModelId is not ("original" or "small-fp8"))
            throw new InvalidDataException("Unknown default neural model.");
        if (!PhoenixConfiguration.ExecutionModes.Contains(ExecutionMode))
            throw new InvalidDataException("Unknown default execution mechanism.");
    }

    public PhoenixSettings Settings(int iterations = 1)
    {
        Validate();
        return new() { Runner = Renderer, ModelId = ModelId,
            CompatibilityExecutionMode = ExecutionMode,
            CopyNvidiaDllToGame = CopyNvidiaDllToGame, Iterations = Math.Clamp(iterations, 1, 10) };
    }
}
