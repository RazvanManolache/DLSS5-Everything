namespace Dlss5CompatApp;

internal sealed class PhoenixMediaView : UserControl
{
    readonly Func<PayloadInfo?> payload;
    readonly bool preview;
    readonly Label status = new() { AutoSize = true, MaximumSize = new Size(850, 0) };
    readonly ComboBox model = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    readonly ComboBox execution = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 400 };
    string? queriedPackage,queryPending;
    string preferredDevice="native";
    long queryGeneration;
    readonly Func<string,string,Task<IReadOnlyList<PhoenixMediaPlayer.Device>>> discoverDevices;
    readonly ComboBox monitoring = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    readonly ComboBox scaler = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    readonly CheckBox motion = new() { Text = "Estimate motion when needed", Checked = true, AutoSize = true };
    readonly CheckBox scenes = new() { Text = "Detect scene changes", Checked = true, AutoSize = true };
    readonly CheckBox fusion = new() { Text = "Experimental multiscale fusion", AutoSize = true };
    readonly NumericUpDown flowHeight = new() { Minimum = 64, Maximum = 2160, Value = 180, Width = 100 };
    readonly CheckedListBox chain = new() { Width = 340, Height = 100, CheckOnClick = true };
    readonly NumericUpDown passes = Number(1, 10, 1), neuralScale = Number(10, 100, 100), blocks = Number(1, 8, 8), minimumBlocks = Number(1, 8, 1);
    readonly NumericUpDown targetFps = Number(0, 1000, 0), processingMs = Number(0, 1000, 0), age = Number(1, 1000, 50);
    readonly NumericUpDown brightness = Number(-2, 2, 0, 2), contrast = Number(0, 4, 1, 2), saturation = Number(0, 2, 1, 2), gamma = Number(.1m, 4, 1, 2);
    readonly NumericUpDown layers = Number(2, 3, 2), secondScale = Number(.1m, 1, .5m, 2), thirdScale = Number(.1m, 1, .75m, 2);
    readonly NumericUpDown[] repeats = Enumerable.Range(0, 8).Select(_ => Number(0, 4, 1)).ToArray();
    readonly ComboBox priority = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    readonly CheckBox adjustFlow = new() { Text = "Allow motion resolution adjustment", Checked = true, AutoSize = true },
        adjustScale = new() { Text = "Allow neural resolution adjustment", AutoSize = true },
        adjustBlocks = new() { Text = "Allow central block adjustment", AutoSize = true },
        adjustPasses = new() { Text = "Allow network pass adjustment", AutoSize = true };
    sealed record ScalingChoice(string Label, int Value) { public override string ToString() => Label; }
    sealed record StageChoice(string Label, int Value) { public override string ToString() => Label; }
    static NumericUpDown Number(decimal minimum, decimal maximum, decimal value, int decimals = 0) => new()
        { Minimum = minimum, Maximum = maximum, Value = value, DecimalPlaces = decimals, Increment = decimals > 0 ? .05m : 1, Width = 95 };
    static FlowLayoutPanel Row(string label, Control control)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        row.Controls.Add(new Label { Text = label, Width = 270, AutoSize = false, Height = 25 }); row.Controls.Add(control); return row;
    }
    public PhoenixMediaView(Func<PayloadInfo?> payload, bool preview,
        Func<string,string,Task<IReadOnlyList<PhoenixMediaPlayer.Device>>>? discoverDevices=null)
    {
        this.payload = payload; this.preview = preview;this.discoverDevices=discoverDevices??PhoenixMediaPlayer.DevicesAsync; Dock = DockStyle.Fill;
        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, Padding = new Padding(18) };
        body.Controls.Add(new Label { Text = "Phoenix Video Player", AutoSize = true,
            Font = new Font("Segoe UI", 18, FontStyle.Bold) });
        body.Controls.Add(new Label { Text = "Play, compare and export using the shared Phoenix model library.", AutoSize = true });
        model.Items.AddRange(["Original", "Lightweight FP8"]); model.SelectedIndex = 0;
        execution.Items.Add(new PhoenixMediaPlayer.Device("native", "Native RTX · Rendering GPU")); execution.SelectedIndex = 0;
        body.Controls.Add(Row("Processing device", execution));
        var refreshDevices=new Button {Text="Refresh processing devices",AutoSize=true};
        refreshDevices.Click+=(_,_)=>{queriedPackage=null;RefreshStatus();};body.Controls.Add(refreshDevices);
        monitoring.Items.AddRange(["Monitoring off", "Basic counters", "Detailed sampled GPU timings"]); monitoring.SelectedIndex = 0;
        scaler.Items.AddRange([new ScalingChoice("Existing scaler", 0), new ScalingChoice("NVIDIA Image Scaling", 1)]); scaler.SelectedIndex = 0;
        foreach (var control in new Control[] { model, motion, scenes,
            new Label { Text = "Motion analysis height", AutoSize = true }, flowHeight, monitoring, scaler, fusion })
            body.Controls.Add(control);
        body.Controls.Add(new Label { Text = "Processing order (checked stages run from top to bottom)", AutoSize = true });
        foreach (var (label, id) in new[] { ("Neural rendering", 0), ("Multiscale fusion", 3), ("Scaling", 1), ("Color treatment", 2) })
            chain.Items.Add(new StageChoice(label, id), id != 3);
        fusion.CheckedChanged += (_, _) => { for (int i = 0; i < chain.Items.Count; i++) if (((StageChoice)chain.Items[i]).Value == 3 && chain.GetItemChecked(i) != fusion.Checked) chain.SetItemChecked(i, fusion.Checked); };
        chain.ItemCheck += (_, e) => { if (((StageChoice)chain.Items[e.Index]).Value == 3) fusion.Checked = e.NewValue == CheckState.Checked; };
        body.Controls.Add(chain);
        var moves = new FlowLayoutPanel { AutoSize = true };
        foreach (var (text, delta) in new[] { ("Move up", -1), ("Move down", 1) })
        { var button = new Button { Text = text, AutoSize = true }; button.Click += (_, _) => MoveStage(delta); moves.Controls.Add(button); }
        body.Controls.Add(moves);
        foreach (var (label, control) in new (string, Control)[] {
            ("Whole-network passes", passes), ("Neural processing scale (%)", neuralScale), ("Central blocks", blocks),
            ("Fusion layers", layers), ("Second fusion scale", secondScale), ("Third fusion scale", thirdScale),
            ("Brightness", brightness), ("Contrast", contrast), ("Saturation", saturation), ("Gamma", gamma) }) body.Controls.Add(Row(label, control));
        for (int i = 0; i < repeats.Length; i++) body.Controls.Add(Row($"Block {i + 1} repetitions (0 skips)", repeats[i]));
        priority.Items.AddRange(["Keep quality fixed", "Prefer responsiveness", "Prefer quality"]); priority.SelectedIndex = 0;
        body.Controls.Add(Row("Adaptive priority", priority));
        foreach (var (label, control) in new (string, Control)[] { ("Target source FPS (0 = no limit)", targetFps),
            ("Maximum processing ms (0 = no limit)", processingMs), ("Processing age budget (ms)", age), ("Minimum central blocks", minimumBlocks) }) body.Controls.Add(Row(label, control));
        foreach (var control in new[] { adjustFlow, adjustScale, adjustBlocks, adjustPasses }) body.Controls.Add(control);
        body.Controls.Add(new Label { Text = "Pipeline adaptation requires monitoring; neural FPS/time budgets work independently. Offline exports preserve the selected quality and process every frame. Tiny reconstruction, when installed, produces exactly 2x dimensions.", MaximumSize = new Size(650, 0), AutoSize = true });
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        foreach (var (name, action) in new (string, Action)[] {
            ("Open player", () => Launch(null)), ("Open media…", OpenMedia), ("Locate player…", SelectPlayer), ("Player recovery…", RecoverPlayer) })
        {
            var button = new Button { Text = name, AutoSize = true, Enabled = !preview };
            button.Click += (_, _) => action(); buttons.Controls.Add(button);
        }
        body.Controls.Add(buttons); body.Controls.Add(status); Controls.Add(body);
        HandleCreated += (_, _) => RefreshStatus();
        VisibleChanged += (_, _) => { if (Visible) RefreshStatus(); }; RefreshStatus();
        motion.CheckedChanged += (_, _) => RefreshDependencies();
        monitoring.SelectedIndexChanged += (_, _) => RefreshDependencies();
        priority.SelectedIndexChanged += (_, _) => RefreshDependencies();
        fusion.CheckedChanged += (_, _) => RefreshDependencies();
        layers.ValueChanged += (_, _) => RefreshDependencies();
        blocks.ValueChanged += (_, _) => { minimumBlocks.Maximum = blocks.Value; };
        chain.ItemCheck += (_, _) => { if (IsHandleCreated) BeginInvoke((Action)RefreshDependencies); };
        RefreshDependencies();
    }
    void RefreshDependencies()
    {
        flowHeight.Enabled = motion.Checked;
        priority.Enabled = monitoring.SelectedIndex > 0;
        var adaptive = priority.Enabled && priority.SelectedIndex > 0;
        foreach (var control in new Control[] { age, adjustFlow, adjustScale, adjustBlocks, adjustPasses }) control.Enabled = adaptive;
        adjustFlow.Enabled = adaptive && motion.Checked;
        var scaling = IsStageEnabled(1); scaler.Enabled = scaling;
        if (!scaling) scaler.SelectedIndex = 0;
        var multi = fusion.Checked && IsStageEnabled(3);
        foreach (var control in new Control[] { layers, secondScale, thirdScale }) control.Enabled = multi;
        thirdScale.Enabled = multi && layers.Value == 3;
        foreach (var control in new Control[] { brightness, contrast, saturation, gamma }) control.Enabled = IsStageEnabled(2);
        var neural = IsStageEnabled(0) || multi;
        // The established neural GPU budgets work without the optional
        // pipeline governor or its CPU/GPU telemetry. Keep them available for
        // either model whenever a neural stage (including fusion) consumes them.
        foreach (var control in new Control[] { passes, neuralScale, blocks, minimumBlocks, targetFps, processingMs }) control.Enabled = neural;
        adjustScale.Enabled = adjustBlocks.Enabled = adjustPasses.Enabled = adaptive && neural;
        foreach (var repeat in repeats) repeat.Enabled = neural;
    }
    void RefreshStatus()
    {
        var root = payload()?.Root;
        if (root is not null && TinyModelLibrary.Find(root) is not null && !scaler.Items.Cast<ScalingChoice>().Any(x => x.Value == 3))
            scaler.Items.Add(new ScalingChoice("Tiny neural 2x", 3));
        if ((root is null || TinyModelLibrary.Find(root) is null) && scaler.Items.Cast<ScalingChoice>().FirstOrDefault(x => x.Value == 3) is { } unavailable)
        { scaler.SelectedIndex = 0; scaler.Items.Remove(unavailable); }
        status.Text = preview ? "Example media workspace. No playback or processing is running."
            : root is null ? "Open Components to prepare the shared component library."
            : PhoenixMediaPlayer.Find(root) is { } player ? "Player: " + player
            : "Locate a Phoenix Video Player build. Its models remain in Everything's shared component library.";
        if (!preview && IsHandleCreated && root is not null && PhoenixMediaPlayer.Find(root) is { } executable)
            _=RefreshExecutionDevices(executable,root);
        else if(!preview&&IsHandleCreated)InvalidateDevices();
    }
    internal async Task RefreshExecutionDevices(string executable,string root)
    {
        string key;
        try { key=PhoenixMediaPlayer.DeviceDiscoveryKey(executable,root); }
        catch(Exception error) { InvalidateDevices();status.Text+=" Portable devices unavailable: "+error.Message;return; }
        if(key==queriedPackage||key==queryPending)return;
        if(queryPending is null&&execution.Items.Count>1&&execution.SelectedItem is PhoenixMediaPlayer.Device current)preferredDevice=current.Id;
        var selected=preferredDevice;
        var generation=++queryGeneration;queryPending=key;queriedPackage=null;
        execution.Items.Clear();execution.Items.Add(new PhoenixMediaPlayer.Device("native","Native RTX · Rendering GPU"));
        execution.SelectedIndex=0;execution.Enabled=false;
        try
        {
            var devices = await discoverDevices(executable, root);
            if(IsDisposed||generation!=queryGeneration)return;
            // Package replacement while the native helper is running must not
            // commit its old inventory, even when the executable path is unchanged.
            if(PhoenixMediaPlayer.DeviceDiscoveryKey(executable,root)!=key){await RefreshExecutionDevices(executable,root);return;}
            execution.Items.Clear(); execution.Items.Add(new PhoenixMediaPlayer.Device("native", "Native RTX · Rendering GPU"));
            foreach (var device in devices) execution.Items.Add(device);
            execution.SelectedIndex = 0;
            for (int i = 0; i < execution.Items.Count; i++) if ((execution.Items[i] as PhoenixMediaPlayer.Device)?.Id == selected) execution.SelectedIndex = i;
            if (selected == "native" && !devices.Any(x => x.Id.StartsWith("cuda:") || x.Vendor == 0x10de))
            {
                var automatic = devices.FirstOrDefault(x => x.Id.StartsWith("vulkan:")) ?? devices.FirstOrDefault(x => x.Id == "cpu");
                if (automatic is not null) execution.SelectedItem = automatic;
            }
            preferredDevice=((PhoenixMediaPlayer.Device)execution.SelectedItem!).Id;
            queriedPackage=key;
        }
        catch (Exception error) { if (!IsDisposed&&generation==queryGeneration) status.Text += " Portable devices unavailable: " + error.Message; }
        finally {if(!IsDisposed&&generation==queryGeneration){queryPending=null;execution.Enabled=true;}}
    }
    void InvalidateDevices(){++queryGeneration;queriedPackage=queryPending=null;
        execution.Items.Clear();execution.Items.Add(new PhoenixMediaPlayer.Device("native","Native RTX · Rendering GPU"));execution.SelectedIndex=0;execution.Enabled=true;}
    void SelectPlayer()
    {
        using var dialog = new OpenFileDialog { Filter = "Phoenix Video Player|PhoenixVideoPlayer.exe" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { PhoenixMediaPlayer.Select(dialog.FileName); RefreshStatus(); }
        catch (Exception error) { status.Text = error.Message; }
    }
    void RecoverPlayer()
    {
        try
        {
            if (preview) return;
            var root = payload()?.Root ?? throw new InvalidOperationException("Prepare Components first.");
            var executable = PhoenixMediaPlayer.Find(root) ?? throw new FileNotFoundException("Locate Phoenix Video Player first.");
            using var dialog = new PhoenixRecoveryForm(executable, root); dialog.ShowDialog(this);
        }
        catch (Exception error) { status.Text = error.Message; }
    }
    void OpenMedia()
    {
        using var dialog = new OpenFileDialog { Filter = "Media files|*.mp4;*.mkv;*.webm;*.mov;*.avi;*.png;*.jpg;*.jpeg;*.gif;*.bmp|All files|*.*" };
        if (dialog.ShowDialog(this) == DialogResult.OK) Launch(dialog.FileName);
    }
    void Launch(string? input)
    {
        try
        {
            if (preview) return;
            if(queryPending is not null)throw new InvalidOperationException("Wait for native processing-device discovery to finish.");
            var root = payload()?.Root ?? throw new InvalidOperationException("Prepare Components first.");
            var executable = PhoenixMediaPlayer.Find(root) ?? throw new FileNotFoundException("Locate Phoenix Video Player first.");
            var recipe = new PhoenixMediaRecipe(model.SelectedIndex == 0 ? "original" : "small-fp8",
                motion.Checked, scenes.Checked, monitoring.SelectedIndex, (int)flowHeight.Value,
                ((ScalingChoice)scaler.SelectedItem!).Value, fusion.Checked && IsStageEnabled(3))
            {
                ExecutionDevice = ((PhoenixMediaPlayer.Device)execution.SelectedItem!).Id,
                ExecutionDevices = execution.Items.Cast<PhoenixMediaPlayer.Device>().ToArray(),
                Order = chain.Items.Cast<StageChoice>().Select(x => x.Value).ToArray(), Neural = IsStageEnabled(0), Scale = IsStageEnabled(1), Color = IsStageEnabled(2),
                Passes = (int)passes.Value, NeuralScale = (float)neuralScale.Value, Blocks = (int)blocks.Value, MinimumBlocks = (int)minimumBlocks.Value,
                TargetFps = (float)targetFps.Value, MaximumProcessingMs = (float)processingMs.Value,
                Repeats = repeats.Select(x => (int)x.Value).ToArray(), FusionLayers = (int)layers.Value, SecondScale = (float)secondScale.Value, ThirdScale = (float)thirdScale.Value,
                Brightness = (float)brightness.Value, Contrast = (float)contrast.Value, Saturation = (float)saturation.Value, Gamma = (float)gamma.Value,
                Priority = priority.SelectedIndex, MaximumAgeMs = (float)age.Value, AdjustFlow = adjustFlow.Checked, AdjustScale = adjustScale.Checked,
                AdjustBlocks = adjustBlocks.Checked, AdjustPasses = adjustPasses.Checked
            };
            PhoenixMediaPlayer.Launch(executable, root, recipe, input);
            status.Text = "Player opened with the shared model library and selected processing settings.";
        }
        catch (Exception error) { status.Text = error.Message; }
    }
    bool IsStageEnabled(int stage)
    { for (int i = 0; i < chain.Items.Count; i++) if (((StageChoice)chain.Items[i]).Value == stage) return chain.GetItemChecked(i); return false; }
    void MoveStage(int delta)
    {
        int index = chain.SelectedIndex, target = index + delta;
        if (index < 0 || target < 0 || target >= chain.Items.Count) return;
        object item = chain.Items[index]; bool enabled = chain.GetItemChecked(index); chain.Items.RemoveAt(index);
        chain.Items.Insert(target, item); chain.SetItemChecked(target, enabled); chain.SelectedIndex = target;
    }
}
