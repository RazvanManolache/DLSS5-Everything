namespace Dlss5CompatApp;

sealed partial class MainForm
{
    readonly ComboBox _routeChoice = new ThemedComboBox() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "Phoenix DLSS5 input route" };
    readonly ComboBox _proxyChoice = new ThemedComboBox() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "ReShade proxy filename" };

    void InitializeRouteChoices()
    {
        _routeChoice.Items.AddRange(["Automatic route", "Native NGX input", "Feeder capture"]);
        _proxyChoice.Items.AddRange(["Automatic proxy", "dxgi.dll", "d3d11.dll", "dinput8.dll (early loading)", "dinput.dll (legacy early loading)"]);
        _routeChoice.SelectedIndex = _proxyChoice.SelectedIndex = 0;
        _tips.SetToolTip(_routeChoice, "Automatic uses backbuffer input with modern addons and keeps same-device processing available. Native NGX input requires compatible D3D12 engine buffers; older addons may require DLAA. Capture uses an eligible in-process route first, with an external helper as fallback.");
        _tips.SetToolTip(_proxyChoice, "DXGI/D3D11 filenames select the graphics loading point. Early input loading requires a matching startup input import and preserves conflicting input mods. This does not change the renderer or inference transport.");
        _routeChoice.SelectedIndexChanged += (_, _) => RouteChoiceChanged();
        _proxyChoice.SelectedIndexChanged += (_, _) => RouteChoiceChanged();
    }

    void RouteChoiceChanged()
    {
        if (_loadingProfile) return;
        RefreshGameDetails(false);
        UpdateSelectedRouteDisplay();
        UpdateButtons();
    }

    void LoadRouteChoices(GameCandidate? game)
    {
        _loadingProfile = true;
        try
        {
            var profile = game is null ? new GameProfile() : _libraryState.ProfileFor(game.ExePath);
            _routeChoice.SelectedIndex = profile.PreferredRoute switch { GameRoutePreference.Native => 1, GameRoutePreference.Feeder => 2, _ => 0 };
            _proxyChoice.SelectedIndex = profile.ProxyName switch { "dxgi.dll" => 1, "d3d11.dll" => 2, "dinput8.dll" => 3, "dinput.dll" => 4, _ => 0 };
        }
        finally { _loadingProfile = false; }
    }

    GameRoutePreference? ChosenRoute => _routeChoice.SelectedIndex switch { 1 => GameRoutePreference.Native, 2 => GameRoutePreference.Feeder, _ => null };
    string? ChosenProxy => _proxyChoice.SelectedIndex switch { 1 => "dxgi.dll", 2 => "d3d11.dll", 3 => "dinput8.dll", 4 => "dinput.dll", _ => null };
    GameCandidate ApplyUiRouteChoices(GameCandidate effective, GameCandidate detected) => effective with
    {
        PreferredRoute = ChosenRoute ?? detected.VerifiedInputRoute,
        ProxyName = ChosenProxy ?? (effective.Api == detected.Api ? detected.VerifiedProxyName : null)
    };
}
