namespace Dlss5CompatApp;

sealed class ModLoadingPlanForm : Form
{
    internal string? SelectedProxy { get; private set; }
    internal ModLoadingPlanForm(ModLoadingPlan plan)
    {
        Text = "Existing mods and loading plan"; Width = 820; Height = 620;
        MinimumSize = new Size(640, 460); StartPosition = FormStartPosition.CenterParent;
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(16) };
        body.RowStyles.Add(new(SizeType.Absolute, 46)); body.RowStyles.Add(new(SizeType.Percent, 100)); body.RowStyles.Add(new(SizeType.Absolute, 48));
        body.Controls.Add(new Label { Text = Path.GetFileName(plan.ExePath) + " — " + (plan.ManualReview ? "existing components found" : "no known loader files found"),
            Dock = DockStyle.Fill, Font = new Font("Segoe UI", 12, FontStyle.Bold) }, 0, 0);
        body.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical,
            Text = string.Join(Environment.NewLine + Environment.NewLine, plan.Steps) + Environment.NewLine + Environment.NewLine +
                string.Join(Environment.NewLine, plan.Components.Select(c => $"{c.Name}  |  {c.Kind}  |  {c.Architecture}\r\n{c.Detail}\r\nSHA-256: {c.Sha256}")) }, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel }; buttons.Controls.Add(close); CancelButton = close;
        if (plan.SuggestedProxy is { } proxy)
        {
            var use = new Button { Text = "Save route using " + proxy, AutoSize = true };
            use.Click += (_, _) => { SelectedProxy = proxy; DialogResult = DialogResult.OK; Close(); }; buttons.Controls.Add(use);
        }
        body.Controls.Add(buttons, 0, 2); Controls.Add(body);
    }
}
