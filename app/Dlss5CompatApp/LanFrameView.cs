using System.Net;
using System.Net.NetworkInformation;

namespace Dlss5CompatApp;

internal sealed class LanFrameView : UserControl
{
    readonly ComboBox address = new ThemedComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly NumericUpDown port = new ThemedNumericUpDown { Minimum = 1024, Maximum = 65535, Value = 47855, Dock = DockStyle.Fill };
    readonly TextBox endpoint = new() { ReadOnly = true, Dock = DockStyle.Fill };
    readonly TextBox pairing = new() { ReadOnly = true, UseSystemPasswordChar = true, Dock = DockStyle.Fill };
    readonly TextBox fingerprint = new() { ReadOnly = true, Dock = DockStyle.Fill };
    readonly Label state = new() { Text = "Stopped", Dock = DockStyle.Fill, AutoSize = true };
    readonly Label frames = new() { Text = "0", Dock = DockStyle.Fill, AutoSize = true };
    readonly Label dimensions = new() { Text = "-", Dock = DockStyle.Fill, AutoSize = true };
    readonly Label latency = new() { Text = "-", Dock = DockStyle.Fill, AutoSize = true };
    readonly Label transport = new() { Text = "-", Dock = DockStyle.Fill, AutoSize = true };
    readonly Label readiness = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    readonly Button start = new() { Text = "Start streamer", AccessibleName = "Start streamer", AutoSize = true, Height = 34, Tag = "primary" };
    readonly Button stop = new() { Text = "Stop", Enabled = false, AutoSize = true, Height = 34 };
    readonly Button export = new() { Text = "Export pairing", AccessibleName = "Export private pairing", Enabled = false, AutoSize = true, Height = 34 };
    readonly Button refresh = new() { Text = "", Width = 36, Height = 32, AccessibleName = "Refresh addresses" };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    readonly ToolTip tips = new();
    readonly Func<PayloadInfo?> payload;
    readonly Func<string> model;
    readonly bool preview;
    readonly Label journey = new() { AutoSize = true, Dock = DockStyle.Top, MaximumSize = new Size(960, 0), Padding = new Padding(0, 0, 0, 12) };
    LanFrameServer? server;
    bool stopping;
    string? notice;
    public LanFrameView(Func<PayloadInfo?> payload, Func<string> model, bool preview = false, Action? openComponents = null)
    {
        this.payload = payload; this.model = model; this.preview = preview;
        Dock = DockStyle.Fill; AutoScroll = true;
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 13,
            Padding = new Padding(0, 10, 0, 0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
        void Row(int row, string caption, Control value, Control? extra = null)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.Controls.Add(new Label { Text = caption, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            value.Margin = new Padding(0, 6, 6, 6); layout.Controls.Add(value, 1, row);
            if (extra is not null) layout.Controls.Add(extra, 2, row);
        }
        refresh.Image = UiTheme.Icon("\uE72C", Color.FromArgb(99, 137, 139)); tips.SetToolTip(refresh, "Refresh addresses");
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        start.Image = UiTheme.Icon("\uE768", Color.FromArgb(99, 137, 139)); start.TextImageRelation = TextImageRelation.ImageBeforeText;
        stop.Image = UiTheme.Icon("\uE71A", Color.FromArgb(99, 137, 139)); stop.TextImageRelation = TextImageRelation.ImageBeforeText;
        export.Image = UiTheme.Icon("\uE74E", Color.FromArgb(99, 137, 139)); export.TextImageRelation = TextImageRelation.ImageBeforeText;
        var guide = new Button { Width = 36, Height = 34, AccessibleName = "Open LAN guide", Image = UiTheme.Icon("\uE897", Color.FromArgb(99, 137, 139)) };
        tips.SetToolTip(guide, "Open LAN guide");
        guide.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Docs", "lan-frame-service.md")) { UseShellExecute = true }); }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
            { state.Text = notice = "Could not open the LAN guide."; }
        };
        guide.Disposed += (_, _) => guide.Image?.Dispose();
        var setup = new Button { Text = "Set up Components", AutoSize = true, AccessibleName = "Open Components for streaming prerequisites" };
        setup.Click += (_, _) => openComponents?.Invoke();
        actions.Controls.AddRange([start, stop, export, setup, guide]); Row(0, "", actions);
        layout.SetColumnSpan(actions,2);
        layout.RowStyles[0].SizeType=SizeType.AutoSize;
        Row(1, "Listen address", address, refresh); Row(2, "Port", port);
        Row(3, "Endpoint", endpoint); Row(4, "Pairing token", pairing, CopyButton(pairing, "Copy pairing token"));
        Row(5, "Certificate SHA-256", fingerprint, CopyButton(fingerprint, "Copy certificate fingerprint"));
        Row(6, "Phoenix DLSS5", readiness);
        Row(7, "Status", state); Row(8, "Active transport", transport); Row(9, "Frames returned", frames);
        Row(10, "Frame size", dimensions); Row(11, "PC processing", latency);
        Row(12, "Available transports", new Label { Text = PyrowaveFrameCodec.Available ? "Pyrowave / Raw RGBA" : "Raw RGBA (Pyrowave runtime missing)", AutoSize = true, Dock = DockStyle.Fill });
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var advanced = new CheckBox { Text = "Connection details and manual network settings", AutoSize = true };
        Row(13, "", advanced); layout.SetColumnSpan(advanced, 2);
        void Details()
        {
            foreach (var index in new[] { 1, 2, 3, 4, 5, 12 })
            {
                layout.RowStyles[index].Height = advanced.Checked ? 44 : 0;
                foreach (Control control in layout.Controls)
                    if (layout.GetRow(control) == index) control.Visible = advanced.Checked;
            }
        }
        advanced.CheckedChanged += (_, _) => Details(); Details();
        var body = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); body.Controls.Add(journey, 0, 0); body.Controls.Add(layout, 0, 1);
        Controls.Add(body);
        RefreshAddresses(); refresh.Click += (_, _) => RefreshAddresses();
        start.Click += (_, _) => StartServer(); stop.Click += async (_, _) => await StopServerAsync();
        export.Click += (_, _) => ExportPairing();
        RefreshStatus();
        timer.Tick += (_, _) => RefreshStatus(); timer.Start();
    }
    Button CopyButton(TextBox source, string label)
    {
        var button = new Button { Width = 36, Height = 32, AccessibleName = label, Image = UiTheme.Icon("\uE8C8", Color.FromArgb(99, 137, 139)) };
        tips.SetToolTip(button, label);
        button.Click += (_, _) =>
        {
            if (string.IsNullOrEmpty(source.Text)) return;
            try { Clipboard.SetText(source.Text); }
            catch (System.Runtime.InteropServices.ExternalException) { state.Text = "Clipboard is busy. Try again."; }
        };
        button.Disposed += (_, _) => button.Image?.Dispose(); return button;
    }
    void RefreshAddresses()
    {
        var selected = address.SelectedItem?.ToString();
        var ips = OrderAddresses(NetworkInterface.GetAllNetworkInterfaces().Where(item => item.OperationalStatus == OperationalStatus.Up)
            .SelectMany(item => item.GetIPProperties().UnicastAddresses.Select(ip => (ip.Address, item.NetworkInterfaceType,
                item.Description.Contains("virtual", StringComparison.OrdinalIgnoreCase)))));
        address.Items.Clear(); address.Items.AddRange(ips.Cast<object>().ToArray());
        address.SelectedIndex = Math.Max(0, Array.FindIndex(ips, item => item.ToString() == selected));
    }
    internal static IPAddress[] OrderAddresses(IEnumerable<(IPAddress Address, NetworkInterfaceType Type, bool Virtual)> addresses) => addresses
        .Where(item => LanFrameProtocol.IsLanAddress(item.Address)).Append((Address: IPAddress.Loopback, Type: NetworkInterfaceType.Loopback, Virtual: false))
        .OrderBy(item => IPAddress.IsLoopback(item.Address)).ThenBy(item => item.Virtual || item.Type == NetworkInterfaceType.Tunnel)
        .ThenBy(item => item.Type == NetworkInterfaceType.Wireless80211 ? 0 : 1)
        .ThenBy(item => item.Address.ToString(), StringComparer.Ordinal).Select(item => item.Address).Distinct().ToArray();
    internal bool StartListening() { StartServer(); return server is not null; }
    internal byte[] PairingData() => server?.Endpoint is { } ep
        ? StreamerPairing.Serialize(ep, server.Fingerprint, server.PairingToken, PyrowaveFrameCodec.Available)
        : throw new InvalidOperationException("Start the streamer before exporting pairing.");
    void ExportPairing()
    {
        if (preview || server is null) return;
        using var dialog = new SaveFileDialog { Title = "Save private pairing file", Filter = "Pairing JSON (*.json)|*.json", FileName = "phoenix-pairing.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { LocalPaths.RequireNoLinks(dialog.FileName); File.WriteAllBytes(dialog.FileName, PairingData()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { state.Text = notice = error.Message; }
    }
    void StartServer()
    {
        if (preview || server is not null || stopping) return;
        notice = null;
        try
        {
            var components = payload() ?? throw new InvalidOperationException("Open Components to download Phoenix DLSS5 and ReShade first.");
            var modelPath = model();
            _ = ScreenStudioSession.RequiredFiles(AppContext.BaseDirectory, components, includeCaptureWorker: false);
            if (!File.Exists(modelPath) && !Directory.Exists(modelPath)) throw new InvalidOperationException("Open Components and import a Phoenix DLSS5 model first.");
            server = new LanFrameServer(async (w, h, settings, token) =>
            {
                var backend = new LanPhoenixBackend();
                try { await backend.StartAsync(components, modelPath, w, h, settings, token).ConfigureAwait(false); return backend; }
                catch { backend.Dispose(); throw; }
            });
            server.Start((IPAddress)(address.SelectedItem ?? IPAddress.Loopback), (int)port.Value);
            endpoint.Text = server.Endpoint?.ToString(); pairing.Text = server.PairingToken; fingerprint.Text = server.Fingerprint;
            start.Enabled = address.Enabled = port.Enabled = refresh.Enabled = false; stop.Enabled = export.Enabled = true;
            RefreshStatus();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.Net.Sockets.SocketException or
            System.Security.Cryptography.CryptographicException or System.ComponentModel.Win32Exception)
        { server?.Dispose(); server = null; state.Text = notice = error.Message; }
    }
    async Task StopServerAsync()
    {
        if (server is null || stopping) return;
        var stoppingServer = server;
        stopping = true; stop.Enabled = false; state.Text = "Stopping";
        try { await stoppingServer.StopAsync(); stoppingServer.Dispose(); if (ReferenceEquals(server, stoppingServer)) server = null; }
        finally
        {
            stopping = false;
            if (!IsDisposed) { endpoint.Clear(); pairing.Clear(); fingerprint.Clear(); export.Enabled = false; start.Enabled = address.Enabled = port.Enabled = refresh.Enabled = true; RefreshStatus(); }
        }
    }
    void RefreshStatus()
    {
        if (stopping) return;
        var current = server?.Status ?? new LanFrameStatus("Stopped");
        state.Text = notice ?? current.State; frames.Text = current.Frames.ToString("N0");
        dimensions.Text = current.Width == 0 ? "-" : $"{current.Width} x {current.Height}";
        latency.Text = current.Frames == 0 ? "-" : $"{current.ProcessingMs:F1} ms";
        transport.Text = current.Codec == "pyrowave" ? "Pyrowave (both directions)" : current.Codec == "raw" ? "Raw RGBA (both directions)" : "-";
        var components = payload(); var modelPath = model();
        readiness.Text = components is null ? "Components not selected" : !components.HasPhoenixFeederApi ? "Compatible Phoenix DLSS5 feeder required" :
            !components.HasReShade64 ? "ReShade x64 required" : !File.Exists(modelPath) && !Directory.Exists(modelPath) ? "Model not selected" : "Ready - " + Path.GetFileName(modelPath.TrimEnd(Path.DirectorySeparatorChar));
        var ready = components?.HasPhoenixFeederApi == true && components.HasReShade64 && (File.Exists(modelPath) || Directory.Exists(modelPath));
        start.Enabled = !preview && !stopping && server is null && ready;
        journey.Text = !ready ? "1. Prepare Components → choose the Phoenix runtime and model. Then start the PC streamer."
            : server is null ? "2. Start the PC streamer. Then export the private pairing file and import it into Phoenix Link on the same local network."
            : current.Frames == 0 ? "3. Export pairing and connect Phoenix Link. Waiting for frames from the client. Pairing details should be shared only with your own device."
            : "4. Client frames are being returned. PC processing time below excludes network, phone decoding and display latency.";
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); server?.Dispose(); server = null; tips.Dispose(); refresh.Image?.Dispose(); start.Image?.Dispose(); stop.Image?.Dispose(); export.Image?.Dispose(); }
        base.Dispose(disposing);
    }
}
