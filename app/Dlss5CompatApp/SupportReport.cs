using System.Text.RegularExpressions;

namespace Dlss5CompatApp;

static class SupportReport
{
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(1);

    public static string Redact(string text, string gameRoot)
    {
        if (text.Length > 8 * 1024 * 1024) throw new InvalidDataException("Report exceeds export limit.");
        foreach (var (path, alias) in new[] {
            (gameRoot, "<GAME>"),
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<USER>"),
            (Environment.MachineName, "<COMPUTER>"),
            (Environment.UserName, "<USER>") }.OrderByDescending(p => p.Item1.Length))
            if (!string.IsNullOrWhiteSpace(path)) text = text.Replace(path, alias, StringComparison.OrdinalIgnoreCase);
        text = Regex.Replace(text, @"(?i)\b[A-Z]:[\\/](?:[^\r\n<>""|]*[\\/])[^\r\n<>""|]*", "<LOCAL_PATH>", RegexOptions.None, Limit);
        text = Regex.Replace(text, @"(?i)(?:https?://)[^\s<>""\[\]]+", "<URL>", RegexOptions.None, Limit);
        text = Regex.Replace(text, @"(?i)[a-z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-z0-9.-]+\.[a-z]{2,}", "<EMAIL>", RegexOptions.None, Limit);
        text = Regex.Replace(text, @"(?im)(\b(?:password|token|secret|api[_-]?key|authorization)\b\s*[:=]\s*)[^\r\n]+", "$1<REDACTED>", RegexOptions.None, Limit);
        text = Regex.Replace(text, @"\b(?:\d{1,3}\.){3}\d{1,3}\b", "<IP>", RegexOptions.None, Limit);
        return "Redacted diagnostic report. Review for remaining private information before sharing.\r\n\r\n" + text;
    }
}

sealed class SupportReportForm : Form
{
    public SupportReportForm(string report, UiPalette palette)
    {
        Text = "Review report before sharing";
        Size = new Size(900, 600);
        MinimumSize = new Size(600, 420);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10);
        var text = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, MaxLength = 8 * 1024 * 1024, Text = report };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4) };
        var save = new Button { Text = "Save report", Width = 130, Height = 32 };
        save.Click += (_, _) =>
        {
            using var dialog = new SaveFileDialog { Filter = "Text report|*.txt", FileName = "dlss5-diagnostics.txt" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try { File.WriteAllText(dialog.FileName, text.Text); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
        buttons.Controls.Add(save);
        Controls.Add(text);
        Controls.Add(buttons);
        UiTheme.Apply(this, palette);
    }
}
