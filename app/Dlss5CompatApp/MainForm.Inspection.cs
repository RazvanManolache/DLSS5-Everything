namespace Dlss5CompatApp;

sealed partial class MainForm
{
    readonly ComboBox _componentFilter = new ThemedComboBox() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, AccessibleName = "Inspected component filter" };
    readonly Dictionary<string, ComponentInspectionReport> _inspectedComponents = new(StringComparer.OrdinalIgnoreCase);

    void InitializeInspectionFilter(TableLayoutPanel filters)
    {
        _componentFilter.Items.AddRange(["All components", "Has installed DLLs", "Copies differ", "Has add-ons"]);
        _componentFilter.SelectedIndex = 0;
        _componentFilter.SelectedIndexChanged += (_, _) => RenderGameRows();
        _tips.SetToolTip(_componentFilter, "Component filters use files checked in this session. Unchecked games appear under All components.");
        _componentFilter.Dock = DockStyle.Fill;
        filters.Controls.Add(_componentFilter, 4, 0);
        var inspect = Command("Inspect component files", "\uE9D9", async () => await InspectSelectedComponentsAsync());
        inspect.Text = "";
        inspect.AutoSize = false;
        inspect.MinimumSize = new Size(32, 32);
        inspect.Size = new Size(32, 32);
        filters.Controls.Add(inspect, 5, 0);
    }

    bool MatchesInspectedComponents(GameCandidate game)
    {
        if (_componentFilter.SelectedIndex <= 0) return true;
        return _inspectedComponents.TryGetValue(game.ExePath, out var report) && report.MatchesFilter((ComponentInspectionFilter)_componentFilter.SelectedIndex);
    }

    async Task InspectSelectedComponentsAsync()
    {
        if (SelectedGame() is not { } game || _busy) return;
        if (_preview) { SetOutcome("Preview - file inspection is disabled"); return; }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        SetOutcome("Checking local component versions and checksums...");
        try
        {
            var report = await ComponentInspectionService.InspectAsync(game, _payload, cancel.Token);
            if (IsDisposed) return;
            _inspectedComponents[game.ExePath] = report;
            SetOutcome($"Files checked: {report.Summary.Available} present, {report.Summary.Outdated} different from components" + (report.Summary.Limited ? " (limited inspection)" : ""));
            using var form = new Form { Text = "Component versions - " + game.DisplayName, Size = new Size(1050, 590), MinimumSize = new Size(760, 440), StartPosition = FormStartPosition.CenterParent, Font = Font };
            var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, ShowItemToolTips = true };
            foreach (var (name, width) in new[] { ("Component", 300), ("Installed version", 180), ("Selected version", 180), ("File state", 140) }) list.Columns.Add(name, width);
            foreach (var row in report.Rows)
            {
                var item = new ListViewItem(row.Name) { ToolTipText = row.InstalledPath + Environment.NewLine + row.Detail + Environment.NewLine + "Installed SHA-256: " + row.InstalledSha256 + Environment.NewLine + "Selected SHA-256: " + row.PayloadSha256 };
                item.SubItems.Add(row.InstalledVersion ?? (row.InstalledAvailable ? "No version resource" : "Missing"));
                item.SubItems.Add(row.PayloadVersion ?? (row.PayloadSha256 is null ? "Not selected" : "No version resource"));
                item.SubItems.Add(row.State.ToString());
                list.Items.Add(item);
            }
            form.Controls.Add(list);
            form.Controls.Add(new Label { Text = ComponentInspectionReport.EvidenceScope, Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(10) });
            UiTheme.Apply(form, _palette);
            form.ShowDialog(this);
            RenderGameRows(game.ExePath);
        }
        catch (OperationCanceledException) { if (!IsDisposed) SetOutcome("File inspection cancelled"); }
        catch (Exception ex) { if (!IsDisposed) SetOutcome("File inspection failed: " + ex.Message); }
    }
}
