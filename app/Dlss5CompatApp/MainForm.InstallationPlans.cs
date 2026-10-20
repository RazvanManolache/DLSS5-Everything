namespace Dlss5CompatApp;

partial class MainForm
{
    string DisplayInstallRoute(GameCandidate game) =>
        PackagedPhoenixPlan.For(game, _payload)?.Description ?? game.DisplayRoute;

    // Injectable at the decision boundary so cancel/selection can be tested without installing or launching games.
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<IReadOnlyList<GameCandidate>, GameCandidate?>? ChooseInstallation { get; set; }

    GameCandidate? ResolveInstallChoice(GameCandidate selected)
    {
        var detected = SelectedBaseGame() ?? selected;
        if (!InstallationChoice.NeedsSelection(detected, selected, _engineChoice.SelectedIndex > 0)) return selected;
        var choices = InstallationChoice.Options(selected);
        return ChooseInstallPlan(selected, choices, false);
    }

    GameCandidate? ChooseInstallPlan(GameCandidate selected, IReadOnlyList<GameCandidate> choices, bool repair, string? failure = null)
    {
        var detected = SelectedBaseGame() ?? selected;
        if (choices.Count == 0)
        {
            SetOutcome("No supported installation route was detected for this executable.");
            return null;
        }
        GameCandidate? chosen;
        if (ChooseInstallation is { } choose) chosen = choose(choices);
        else
        {
            using var dialog = new Form
            {
                Text = repair ? "Verification failed — choose another route" : "Choose installation", StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(660, 400), MinimumSize = new Size(660, 400),
                ShowInTaskbar = false, MaximizeBox = false, MinimizeBox = false, Font = Font,
                AutoScaleMode = AutoScaleMode.Dpi
            };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 4 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None,
                ScrollBars = ScrollBars.Vertical, TabStop = false, Text = repair
                    ? "Phoenix output was not verified.\r\n" + failure + "\r\n\r\nChoose another route to install and verify. Cancel keeps the current installation. Menus or launchers may need your input; a failed check does not always mean the route is wrong."
                    : "The renderer is uncertain or more than one route is possible. Choose the renderer used in the game's settings.\r\n\r\nThis installs files only. You can run Verify & fix afterwards." }, 0, 0);
            var picker = new ThemedComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Installation renderer" };
            picker.DropDownWidth = 760;
            foreach (var choice in choices) picker.Items.Add(choice.DisplayApi +
                (choice.Route == InstallRoute.X64DirectPhoenix ? " — native input" : " — capture bridge") +
                (SoftwarePresentation.RequiresHook(choice) ? " — software devices" : "") +
                (choice.ProxyName is { } proxy ? " / " + proxy : "") +
                (detected.AllApis.Contains(choice.Api) ? " (detected)" : " (manual choice)"));
            var detail = new Label { Dock = DockStyle.Fill };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var install = new Button { Text = repair ? "Install and verify" : "Install this", AutoSize = true, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            buttons.Controls.AddRange([install, cancel]);
            picker.SelectedIndexChanged += (_, _) =>
            {
                var choice = choices[picker.SelectedIndex];
                var ready = _payload?.IsReadyFor(choice) == true;
                var confidence = detected.HasVerifiedRenderer && choice.Api == detected.Api ? "Previously verified for this executable."
                    : detected.DetectedInputApis.Contains(choice.Api) ? "Detected from executable imports; runtime output has not been verified."
                    : "A possible route, not a verified working choice. Check the game's video settings if unsure.";
                install.Enabled = ready;
                detail.Text = confidence + Environment.NewLine + DisplayInstallRoute(choice) + Environment.NewLine + Environment.NewLine +
                    (ready ? "Components ready. Runtime not yet verified." : "Missing components: " + _payload?.MissingFor(choice)) +
                    (choice.SuggestedArguments.Length > 0 ? Environment.NewLine + "Launch arguments: " + choice.SuggestedArguments : "");
            };
            picker.SelectedIndex = Math.Max(0, choices.ToList().FindIndex(choice => choice.Api == selected.Api));
            layout.Controls.Add(picker, 0, 1);
            layout.Controls.Add(detail, 0, 2);
            layout.Controls.Add(buttons, 0, 3);
            var help = new Button { Text = "Help me choose", AutoSize = true };
            help.Click += (_, _) => MessageBox.Show(dialog,
                "No files are changed while inspecting detection.\n\n" + detected.Detection +
                "\n\nDetected possibilities: " + detected.DisplayPossibleApis +
                "\n\nChoose the graphics API selected in the game's video settings or launcher. For an unknown API there is no reliable automatic recommendation.\n\nInstall adds the files; Verify & fix later checks output and asks before trying another route.",
                "Choosing a game graphics API", MessageBoxButtons.OK, MessageBoxIcon.Information);
            buttons.Controls.Add(help);
            dialog.Controls.Add(layout);
            dialog.AcceptButton = install;
            dialog.CancelButton = cancel;
            UiTheme.Apply(dialog, _palette);
            chosen = dialog.ShowDialog(this) == DialogResult.OK ? choices[picker.SelectedIndex] : null;
        }
        if (chosen is null) { SetOutcome(repair ? "No replacement installed. The last installation is kept." : "Installation cancelled. No game files changed."); return null; }
        if (!choices.Contains(chosen)) throw new InvalidOperationException("Installation choice is not in the offered routes.");
        // Reflect the accepted choice in Advanced and persist it only after installation succeeds.
        _engineChoice.SelectedIndex = _engineChoice.Items.Cast<EngineChoice>().ToList().FindIndex(choice => choice.Api == chosen.Api && choice != _engineChoice.Items[0]);
        _routeChoice.SelectedIndex = chosen.PreferredRoute switch { GameRoutePreference.Native => 1, GameRoutePreference.Feeder => 2, _ => 0 };
        _proxyChoice.SelectedIndex = chosen.ProxyName == "dxgi.dll" ? 1 : chosen.ProxyName == "d3d11.dll" ? 2 : 0;
        _launchArguments.Text = chosen.SuggestedArguments;
        return chosen;
    }
}
