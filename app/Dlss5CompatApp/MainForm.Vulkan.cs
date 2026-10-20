namespace Dlss5CompatApp;

sealed partial class MainForm
{
    async Task SetUpVulkanAsync(bool restore = false)
    {
        if (_preview || _busy || _downloadCts is not null) return;
        var path = SelectedGame() is { Api: GraphicsApi.Vulkan } game ? game.ExePath : null;
        if (path is null)
        {
            using var picker = new OpenFileDialog { Title = "Choose Vulkan game executable", Filter = "Game executable|*.exe" };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            path = picker.FileName;
        }
        var x86 = PeReader.GetArchitecture(path) == CpuArch.X86;
        var source = x86 ? _payload?.ReShade32Dll : _payload?.ReShade64Dll;
        if (!restore && source is null)
        {
            SetOutcome("Vulkan setup needs the matching ReShade x86/x64 DLL. Check component updates first.");
            return;
        }
        var message = restore
            ? "Remove this app's Vulkan layer reference for the selected executable? Other games and an existing official ReShade layer are kept. Game-folder files are removed separately with Remove Phoenix DLSS5."
            : "Set up ReShade's Vulkan layer for the selected executable? This changes the current user's Vulkan layer registration and ReShade game list, outside the game folder. Existing official layers are reused; conflicting layers are not disabled. No game will be launched.";
        if (MessageBox.Show(this, message, restore ? "Remove Vulkan reference" : "Set up Vulkan", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        SetBusy(true);
        try
        {
            var layer = new VulkanLayerInstaller(x86);
            if (restore)
            {
                var removed = await layer.RestoreAsync(path, _lifetimeCts.Token);
                SetOutcome(removed ? "Vulkan reference removed. Other games kept." : "No app-managed Vulkan reference found.");
            }
            else
            {
                await layer.InstallAsync(path, source!, _lifetimeCts.Token);
                SetOutcome("Vulkan layer setup completed. Install Phoenix DLSS5 for this executable next.");
            }
        }
        catch (OperationCanceledException) { if (!IsDisposed) SetOutcome("Vulkan operation cancelled"); }
        catch (Exception ex) { if (!IsDisposed) SetOutcome("Vulkan operation failed: " + ex.Message); }
        finally { if (!IsDisposed) SetBusy(false); }
    }
}
