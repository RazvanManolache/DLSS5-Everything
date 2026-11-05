namespace Dlss5CompatApp;

/// <summary>One install-time selection shared by every API/architecture route.</summary>
static class PhoenixModelSelection
{
    public static string Resolve(PayloadInfo payload, PhoenixSettings settings, string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var selected = Path.GetFullPath(explicitPath);
            if (Directory.Exists(selected))
            {
                if (PhoenixConfiguration.RequiresNativeNr(settings))
                    throw new InvalidOperationException("NVIDIA DLSS5 requires a model DLL; the selected model folder is for Phoenix DLSS5.");
                return selected; // PhoenixConfiguration validates its selected family before any installation changes.
            }
            RequireDll(selected, settings.ModelId);
            return selected;
        }
        if (!PhoenixConfiguration.RequiresNativeNr(settings))
        {
            // Reject corrupted exports, rather than silently installing bad weights.
            // Keep the existing no-links/containment policy intact.
            var library = settings.ModelLibraryDirectory ?? OptionalModelLibrary.Root(payload.Root);
            var package = OptionalModelLibrary.Installed(library, verifyWeights: true)
                .FirstOrDefault(entry => entry.model == settings.ModelId);
            if (package is not null)
                return Path.GetDirectoryName(OptionalModelLibrary.Contained(library, package.manifest))!;
        }
        // The default per-filename DLL list deliberately prefers local imports.
        // Retain that order, but consider the managed alternatives when that
        // preferred DLL lacks the requested model. Never change the model ID.
        var candidates = payload.NvidiaDlls.Concat(payload.NrModelCandidates)
            .Where(path => Path.GetFileName(path).Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var path in candidates)
            if (NvidiaModelResources.Contains(path, settings.ModelId)) return path;
        throw new InvalidOperationException($"No compatible source for {settings.ModelId}: install its native export " +
            $"or import an x64 NVIDIA DLSS5 DLL containing {NvidiaModelResources.ResourceName(settings.ModelId)}. " +
            "The selected model has not been changed.");
    }

    static void RequireDll(string path, string model)
    {
        if (!NvidiaModelResources.Contains(path, model))
            throw new InvalidOperationException($"The selected DLL does not contain {model} " +
                $"({NvidiaModelResources.ResourceName(model)}) as a valid x64 model resource. " +
                "Choose a compatible DLL or a matching Phoenix DLSS5 model export.");
    }
}
