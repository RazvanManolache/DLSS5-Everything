namespace Dlss5CompatApp;

internal sealed class PhoenixRecoveryForm : Form
{
    readonly PhoenixRecoveryService recovery;
    readonly Label status = new() { AutoSize = true, MaximumSize = new Size(700, 0) };
    internal PhoenixRecoveryForm(string executable, string? componentRoot)
    {
        recovery = new(executable); Text = "Phoenix recovery — " + Path.GetFileName(executable);
        Width = 760; Height = 420; StartPosition = FormStartPosition.CenterParent;
        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(18) };
        body.Controls.Add(new Label { Text = "Restore processing while the application is closed", AutoSize = true, Font = new Font("Segoe UI", 14, FontStyle.Bold) });
        body.Controls.Add(new Label { Text = "Phoenix applies the request before initializing its providers on the next launch. A newly selected model becomes last-working only after sustained successful frames.", MaximumSize = new Size(700, 0), AutoSize = true });
        Add("Restore last working settings", recovery.RestoreWorking); Add("Disable Phoenix processing", recovery.DisableProcessing);
        if (componentRoot is not null)
            foreach (var installed in OptionalModelLibrary.Installed(OptionalModelLibrary.Root(componentRoot)))
                Add("Use installed " + installed.label, () => recovery.SelectInstalledModel(componentRoot, installed.id));
        body.Controls.Add(status); Controls.Add(body); RefreshStatus();
        void Add(string text, Action action)
        {
            var button = new Button { Text = text, AutoSize = true };
            button.Click += (_, _) => { try { action(); RefreshStatus(); status.Text += "\r\nRecovery request saved for the next launch."; } catch (Exception error) { status.Text = error.Message; } };
            body.Controls.Add(button);
        }
    }
    void RefreshStatus()
    {
        try
        {
            var saved = recovery.Read();
            status.Text = $"Failed trials: {saved["failures"]}\r\n{saved["reason"]}";
            if (saved["pending"] is not null) status.Text += "\r\nAn earlier settings trial did not establish health.";
            if (saved["requested"] is not null) status.Text += "\r\nRecovery settings are queued for the next launch.";
        }
        catch (Exception error) { status.Text = error.Message; }
    }
}
