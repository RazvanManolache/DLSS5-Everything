using System.Diagnostics;

namespace Dlss5CompatApp;

sealed partial class MainForm
{
    readonly bool _preview;
    readonly Panel _pageHost = new() { Dock = DockStyle.Fill };
    readonly Dictionary<string, Panel> _pages = [];
    readonly Dictionary<string, Button> _navigation = [];
    readonly Label _pageTitle = new() { Text = "Game library", AutoSize = true, Font = new Font("Segoe UI Semibold", 20) };
    readonly Label _gameCount = new() { AutoSize = true, Text = "Your games", Margin = new Padding(0, 6, 0, 0) };
    readonly Label _detailName = new() { Dock = DockStyle.Fill, Text = "Select a game", Font = new Font("Segoe UI Semibold", 14), AutoEllipsis = true };
    readonly TextBox _detailPath = new() { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, TabStop = false };
    readonly Label _detailRoute = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    readonly Label _detailStatus = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    Button _setupComponents = null!;
    readonly TextBox _launchArguments = new() { Dock = DockStyle.Fill, PlaceholderText = "Launch arguments" };
    readonly Button _favorite = new() { Text = "Favorite", AutoSize = true };
    readonly Button _hide = new() { Text = "Hide", AutoSize = true };
    readonly Button _saveProfile = new() { Text = "Save profile", Dock = DockStyle.Fill };
    readonly Button _diagnostics = new() { Text = "Check runtime", Dock = DockStyle.Fill };
    readonly Button _cancelBackground = new() { Text = "Cancel", Dock = DockStyle.Fill, Enabled = false };
    readonly ComboBox _libraryFilter = new ThemedComboBox() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 135 };
    readonly ComboBox _apiFilter = new ThemedComboBox() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    readonly ComboBox _storeFilter = new ThemedComboBox() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 125 };
    readonly CheckBox _groupByStore = new() { Text = "Group by store", AutoSize = true };
    readonly CheckBox _detailedColumns = new() { Text = "Details", AutoSize = true };
    readonly ListView _historyView = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, ShowItemToolTips = true };
    readonly ListBox _rootsList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly ListBox _hiddenList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly ComboBox _themeChoice = new ThemedComboBox() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    readonly ComboBox _releaseChannel = new ThemedComboBox() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    readonly Label _updateStatus = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Text = "" };
    readonly Label _componentMessage = new() { Dock = DockStyle.Fill, Text = "", AutoEllipsis = true };
    readonly TextBox _componentDetails = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    readonly ListView _addonList = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false };
    readonly PictureBox _cover = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Visible = false };
    readonly ToolTip _tips = new();
    readonly FlowLayoutPanel _libraryViewTools = new() { Name = "LibraryViewTools", Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
    readonly CancellationTokenSource _lifetimeCts = new();
    readonly List<Button> _busyButtons = [];
    UiPalette _palette = UiPalette.Light;
    bool _busy;
    bool _loadingProfile;
    string _activePage = "My Games";
    Control _gameInspector = null!;
    TableLayoutPanel _discoveryBody = null!;
    ScreenStudioView _screenStudio = null!;
    ScreenStudioView _cameraStudio = null!;
    LanFrameView _streamer = null!;

    void BuildShellUi()
    {
        SuspendLayout();
        var frame = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = Padding.Empty };
        frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _shellFrame = frame;
        frame.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        _dropZone.Visible = false;
        frame.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        frame.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        _dropZoneText.Text = "Temporary play";
        _tips.SetToolTip(_dropZoneText, "Drop a Windows or DOS game. Files are restored when the game exits.");
        _dropZone.Controls.Add(_dropZoneText);
        frame.Controls.Add(_dropZone, 0, 0);
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 182));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        frame.Controls.Add(shell, 0, 1);
        var sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 14, Padding = new Padding(14, 22, 14, 12) };
        sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        for (var i = 0; i < 11; i++) sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        sidebar.Controls.Add(new Label { Text = "DLSS5\nEverything", Font = new Font("Segoe UI Semibold", 17), Dock = DockStyle.Fill }, 0, 0);
        var sections = new[] { ("My Games", "\uE8F1"), ("Library", "\uE721"), ("Screen Studio", "\uE7F4"), ("Camera", "\uE722"), ("Media", "\uE714"), ("Streamer", "\uE968"), ("Components", "\uE896"), ("Add-ons", "\uE710"), ("History", "\uE81C"), ("Settings", "\uE713"), ("Resources", "\uE774") };
        for (var i = 0; i < sections.Length; i++)
        {
            var (name, icon) = sections[i];
            var button = Command(name, icon, () => ShowPage(name));
            button.TextAlign = ContentAlignment.MiddleLeft;
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(0, 2, 0, 2);
            _navigation[name] = button;
            sidebar.Controls.Add(button, 0, i + 1);
        }
        sidebar.Controls.Add(new Label { Text = "Phoenix DLSS5 edition", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft }, 0, 13);
        shell.Controls.Add(sidebar, 0, 0);
        var workspace = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(24, 18, 24, 12) };
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        workspace.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        workspace.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        heading.Controls.Add(_pageTitle);
        heading.Controls.Add(_gameCount);
        workspace.Controls.Add(heading, 0, 0);
        workspace.Controls.Add(BuildPhoenixUpdateBar(), 0, 1);
        workspace.Controls.Add(_pageHost, 0, 2);
        shell.Controls.Add(workspace, 1, 0);
        AddPage("Library", BuildLibraryPage());
        AddPage("My Games", BuildMyGamesPage());
        var screenPage = Page();
        _screenStudio = new ScreenStudioView(() => _payload,
            () => !string.IsNullOrWhiteSpace(_modelPath) ? _modelPath : _payload?.NvidiaDlls.FirstOrDefault(path => Path.GetFileName(path).Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase)) ?? "",
            () => _palette, _preview);
        screenPage.Controls.Add(_screenStudio);
        AddPage("Screen Studio", screenPage);
        var mediaPage = Page();
        mediaPage.Controls.Add(new PhoenixMediaView(() => _payload, _preview));
        AddPage("Media", mediaPage);
        var cameraPage = Page();
        _cameraStudio = new ScreenStudioView(() => _payload,
            () => !string.IsNullOrWhiteSpace(_modelPath) ? _modelPath : _payload?.NvidiaDlls.FirstOrDefault(path => Path.GetFileName(path).Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase)) ?? "",
            () => _palette, _preview, cameraOnly: true);
        cameraPage.Controls.Add(_cameraStudio);
        AddPage("Camera", cameraPage);
        var lanPage = Page();
        _streamer = new LanFrameView(() => _payload,
            () => !string.IsNullOrWhiteSpace(_modelPath) ? _modelPath : _payload?.NvidiaDlls.FirstOrDefault(path => Path.GetFileName(path).Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase)) ?? "", _preview, componentSetup: BuildInlineComponentSetup());
        lanPage.Controls.Add(_streamer);
        AddPage("Streamer", lanPage);
        AddPage("Components", BuildComponentsPage());
        AddPage("Add-ons", BuildAddonsPage());
        AddPage("History", BuildHistoryPage());
        AddPage("Settings", BuildSettingsPage());
        AddPage("Resources", BuildResourcesPage());
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(12, 0, 12, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        footer.Controls.Add(_componentMessage, 0, 0);
        footer.Controls.Add(Command("Activity", "\uE9D9", ShowActivity), 1, 0);
        _cancelBackground.Click += (_, _) => { if (_verificationCts is not null) StopVerification(); else { _scanCts?.Cancel(); _downloadCts?.Cancel(); } };
        _tips.SetToolTip(_cancelBackground, "Stop the current verification, scan or download. Verification closes only its owned test game.");
        _tips.SetToolTip(_hideIncompatible, "Static route support only. Visible games are not automatically runtime-verified.");
        footer.Controls.Add(_cancelBackground, 2, 0);
        frame.Controls.Add(footer, 0, 2);
        Controls.Add(frame);
        _games.HideSelection = false;
        _games.BorderStyle = BorderStyle.None;
        _games.Font = new Font("Segoe UI", 10);
        _games.Columns.Add(GameColumnTitles[0], 195);
        _games.Columns.Add(GameColumnTitles[1], 115);
        _games.Columns.Add(GameColumnTitles[2], 0);
        _games.Columns.Add(GameColumnTitles[3], 50);
        _games.Columns.Add(GameColumnTitles[4], 110);
        _games.Columns.Add(GameColumnTitles[5], 0);
        _games.Columns.Add(GameColumnTitles[6], 0);
        _games.Columns.Add("Status", 100);
        _games.SelectedIndexChanged += (_, _) => { if (!_usingMyGames) RefreshGameDetails(); };
        _games.DoubleClick += (_, _) => RefreshGameDetails();
        _libraryFilter.Items.AddRange(["All games", "Installed", "Favorites", "Hidden"]);
        _libraryFilter.SelectedIndex = 0;
        _apiFilter.Items.Add("All APIs");
        foreach (var api in Enum.GetValues<GraphicsApi>()) _apiFilter.Items.Add(api);
        _apiFilter.SelectedIndex = 0;
        _storeFilter.Items.AddRange(["All stores", "Steam", "Epic", "GOG", "Xbox", "Ubisoft", "Manual"]);
        _storeFilter.SelectedIndex = 0;
        _libraryFilter.SelectedIndexChanged += (_, _) => RenderGameRows();
        _apiFilter.SelectedIndexChanged += (_, _) => RenderGameRows();
        _storeFilter.SelectedIndexChanged += (_, _) => RenderGameRows();
        _groupByStore.CheckedChanged += (_, _) => RenderGameRows();
        _detailedColumns.CheckedChanged += (_, _) => ResizeGameColumns();
        _engineChoice.SelectedIndexChanged += (_, _) => RefreshGameDetails(false);
        InitializeRouteChoices();
        _favorite.Click += (_, _) => ToggleFavorite();
        _hide.Click += (_, _) => ToggleHidden();
        _saveProfile.Click += (_, _) => SaveSelectedProfile();
        _diagnostics.Click += (_, _) => CheckSelectedRuntime();
        _themeChoice.SelectedIndexChanged += (_, _) => ChangeTheme();
        FormClosed += (_, _) => { _lifetimeCts.Cancel(); _scanCts?.Cancel(); _tips.Dispose(); _cover.Image?.Dispose(); };
        WireGameMenu();
        ApplyTheme();
        ShowPage("My Games");
        ResumeLayout(true);
    }

    Panel BuildLibraryPage()
    {
        var page = Page();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var roots = Row(100, 48, 85, 145, 150);
        _scanRoot.Dock = DockStyle.Fill;
        roots.Controls.Add(_scanRoot, 0, 0);
        var browse = Command("Browse game folder", "\uE8B7", () => { if (!_busy && PickFolder(_scanRoot)) SaveRoot(); });
        browse.Text = "";
        browse.AutoSize = false;
        browse.MinimumSize = new Size(32, 32);
        browse.Dock = DockStyle.Fill;
        browse.ImageAlign = ContentAlignment.MiddleCenter;
        roots.Controls.Add(browse, 1, 0);
        var scan = Command("Scan", "\uE721", async () => await ScanAsync());
        _busyButtons.Add(scan);
        roots.Controls.Add(scan, 2, 0);
        roots.Controls.Add(Command("Discover", "\uE774", async () => await DiscoverLibraryAsync()), 3, 0);
        roots.Controls.Add(Command("Add EXE", "\uE710", AddExe), 4, 0);
        foreach (var button in roots.Controls.OfType<Button>())
        {
            button.AutoSize = false;
            button.Dock = DockStyle.Fill;
        }
        layout.Controls.Add(roots, 0, 0);
        var search = Row(100, 125, 118, 112, 160, 40);
        _search.Dock = DockStyle.Fill;
        search.Controls.Add(_search, 0, 0);
        search.Controls.Add(_libraryFilter, 1, 0);
        search.Controls.Add(_apiFilter, 2, 0);
        search.Controls.Add(_storeFilter, 3, 0);
        InitializeInspectionFilter(search);
        layout.Controls.Add(search, 0, 1);
        var filters = Flow();
        filters.Controls.AddRange([_hideIncompatible, _groupByStore, _detailedColumns]);
        var filterRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 264));
        filterRow.Controls.Add(filters, 0, 0);
        filterRow.Controls.Add(_libraryViewTools, 1, 0);
        layout.Controls.Add(filterRow, 0, 2);
        var library = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        library.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        library.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 360));
        _games.Dock = DockStyle.Fill;
        var gameHost = new Panel { Dock = DockStyle.Fill };
        gameHost.Controls.Add(_games);
        BuildEmptyState(gameHost);
        library.Controls.Add(gameHost, 0, 0);
        _discoveryBody = library;
        _gameInspector = BuildGameInspector();
        library.Controls.Add(_gameInspector, 1, 0);
        layout.Controls.Add(library, 0, 3);
        page.Controls.Add(layout);
        _games.SizeChanged += (_, _) => ResizeGameColumns();
        return page;
    }

    Control BuildGameInspector()
    {
        var container = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(18, 0, 0, 0) };
        container.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        container.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        container.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        container.RowStyles.Add(new RowStyle(SizeType.Absolute, 156));
        _inspectorScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Name = "GameDetailsScroll" };
        var inspector = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = Padding.Empty };
        inspector.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Detail(Control control)
        {
            var row = inspector.RowCount++;
            inspector.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            control.Margin = new Padding(3, 5, 3, 7);
            if (control is Label label) { label.AutoSize = true; label.MaximumSize = new Size(320, 0); }
            inspector.Controls.Add(control, 0, row);
        }
        _detailName.Font = new Font("Segoe UI Semibold", 12);
        Detail(_detailName);
        var metaActions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        var advancedToggle = new CheckBox { Text = "Advanced", AutoSize = true, AccessibleName = "Show advanced game options" };
        metaActions.Controls.AddRange([_favorite, _hide, advancedToggle]); Detail(metaActions);
        Detail(_detailStatus);
        Detail(_installedPhoenixNotice);
        var componentSetup = BuildInlineComponentSetup();
        _setupComponents = componentSetup.DownloadButton;
        Detail(componentSetup);
        Detail(_verificationProgress); Detail(_stopVerification); Detail(_verificationReport);
        var advanced = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Visible = false, Name = "AdvancedGameOptions" };
        advanced.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Advanced(Control control, int height)
        {
            var row = advanced.RowCount++;
            advanced.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            control.Dock = DockStyle.Fill;
            advanced.Controls.Add(control, 0, row);
        }
        _detailPath.ScrollBars = ScrollBars.Vertical;
        Advanced(_detailPath, 72); Advanced(Caption("Game graphics API"), 26); Advanced(_engineChoice, 36);
        _engineChoice.DropDownWidth = 540;
        Advanced(_detailRoute, 64); Advanced(Caption("Launch arguments"), 26); Advanced(_launchArguments, 36);
        var iterations = Row(100, 66);
        iterations.Controls.Add(Caption("Frame iterations"), 0, 0); iterations.Controls.Add(_frameIterations, 1, 0);
        Advanced(iterations, 36); _forceVrEyeSplit.Text = "VR eye split"; Advanced(_forceVrEyeSplit, 30);
        Advanced(_saveProfile, 36); Advanced(Command("Change artwork", "\uEB9F", ChangeArtwork), 38); Advanced(_cover, 80);
        Advanced(Caption("Phoenix input route"), 26); Advanced(_routeChoice, 36);
        Advanced(Caption("ReShade proxy"), 26); Advanced(_proxyChoice, 36);
        Detail(advanced); _inspectorScroll.Controls.Add(inspector);
        advancedToggle.CheckedChanged += (_, _) => { advanced.Visible = advancedToggle.Checked; if (advanced.Visible) _inspectorScroll.ScrollControlIntoView(advanced); };
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(0, 8, 0, 0) };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (var i = 0; i < 4; i++) actions.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        _install.Text = "Install";
        _install.Tag = "primary";
        _install.Dock = DockStyle.Fill;
        actions.Controls.Add(_install, 0, 0);
        _tips.SetToolTip(_install, "Install Phoenix DLSS5 files without launching the game. Uncertain renderer choices are shown before installation.");
        _verify.Dock = DockStyle.Fill;
        actions.Controls.Add(_verify, 1, 0);
        _tips.SetToolTip(_verify, "Launch and close the game to check Phoenix output. If it fails, choose another route to install and test. Image quality is not certified.");
        _runExe.Text = "Play";
        _runExe.Dock = DockStyle.Fill;
        actions.Controls.Add(_runExe, 0, 1);
        _restore.Text = "Remove Phoenix DLSS5";
        _restore.Dock = DockStyle.Fill;
        actions.Controls.Add(_restore, 0, 2);
        _openFolder.Dock = DockStyle.Fill;
        actions.Controls.Add(_openFolder, 1, 1);
        _diagnostics.Text = "Diagnostics";
        actions.Controls.Add(_diagnostics, 0, 3);
        var controls = Command("Controls", "\uE9E9", () => EditPhoenixSettings());
        _tips.SetToolTip(controls, "Phoenix DLSS5 live and offline settings");
        actions.Controls.Add(controls, 1, 2);
        actions.Controls.Add(Command("Compare", "\uE8FF", () => EditPhoenixSettings("Comparison")), 1, 3);
        container.Controls.Add(_inspectorScroll, 0, 0);
        _installSummary.Margin = new Padding(3, 4, 3, 4);
        container.Controls.Add(_installSummary, 0, 1);
        container.Controls.Add(actions, 0, 2);
        return container;
    }

    Panel BuildComponentsPage()
    {
        var page = Page(); page.AutoScroll = true;
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(0, 0, 12, 12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(Control control)
        {
            var row = layout.RowCount++;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); control.Dock = DockStyle.Top;
            control.Margin = new Padding(3, 6, 3, 10); layout.Controls.Add(control, 0, row);
        }
        Label Heading(string text) => new() { Text = text, Font = new Font(Font, FontStyle.Bold), AutoSize = true };
        Row(new Label { Text = "Prepare components once, then install a game. Your model is supplied separately; nothing is downloaded just by opening this page.", AutoSize = true, MaximumSize = new Size(900, 0) });
        Row(Heading("1. Phoenix runtime")); Row(_runtimeRequirement);
        var runtimeActions = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        runtimeActions.Controls.Add(Command("Download Phoenix DLSS5", "\uE896", async () => await UsePublishedPhoenixAsync()));
        runtimeActions.Controls.Add(Command("Import Phoenix DLSS5 build", "\uE8E5", ImportPhoenix));
        _updatePayload.Text = "Check for updates"; _updatePayload.AutoSize = true; runtimeActions.Controls.Add(_updatePayload);
        Row(runtimeActions); _payloadProgress.Height = 14; Row(_payloadProgress);
        Row(Heading("2. Model source")); Row(_modelRequirement);
        var modelActions = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        modelActions.Controls.Add(Command("Import model / NGX DLL", "\uE8B7", ImportModel));
        modelActions.Controls.Add(Command("Model compatibility", "\uE774", () => OpenResource("https://github.com/RazvanManolache/dlss5-phoenix-addon#what-you-can-play-with")));
        Row(modelActions);
        Row(BuildOptionalModelChoices());
        Row(Heading("3. Find a game"));
        Row(Command("Choose a game to install", "\uE721", () => ShowPage("Library")));
        Row(_optionalRequirement);
        var advancedToggle = new CheckBox { Text = "Advanced component tools and diagnostics", AutoSize = true };
        Row(advancedToggle);
        var advanced = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Visible = false };
        advanced.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Advanced(Control control, int? height = null)
        {
            var row = advanced.RowCount++;
            advanced.RowStyles.Add(height is { } h ? new RowStyle(SizeType.Absolute, h) : new RowStyle(SizeType.AutoSize));
            control.Dock = DockStyle.Fill; advanced.Controls.Add(control, 0, row);
        }
        Advanced(Caption("Component library folder"), 26);
        var folder = RowLayout();
        TableLayoutPanel RowLayout()
        {
            var host = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top };
            host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); host.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
            host.Controls.Add(_payloadRoot, 0, 0); _payloadRoot.Dock = DockStyle.Fill;
            var browse = Command("Browse component folder", "\uE8B7", () => { if (_busy || _downloadCts is not null) return; PickPayloadFolder(); RefreshPayload(); });
            browse.Text = ""; browse.AutoSize = false; browse.MinimumSize = new Size(32, 32); browse.Dock = DockStyle.Fill; browse.ImageAlign = ContentAlignment.MiddleCenter;
            host.Controls.Add(browse, 1, 0); return host;
        }
        Advanced(folder, 40); Advanced(_payloadStatus, 64);
        Advanced(Caption("Release channel"), 26);
        _releaseChannel.Items.AddRange(["Latest (includes previews)", "Stable releases only"]); _releaseChannel.SelectedIndex = 0;
        Advanced(_releaseChannel, 36);
        var tools = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        tools.Controls.Add(Command("Open components", "\uE8B7", () => OpenDirectory(ResolvePayloadPath())));
        tools.Controls.Add(Command("Cancel download", "\uE711", CancelDownloads));
        tools.Controls.Add(Command("NVIDIA runtimes", "\uE774", () => OpenResource("https://github.com/NVIDIAGameWorks/Streamline")));
        tools.Controls.Add(Command("Set up Vulkan", "\uE713", async () => await SetUpVulkanAsync()));
        tools.Controls.Add(Command("Remove Vulkan reference", "\uE74D", async () => await SetUpVulkanAsync(restore: true)));
        Advanced(tools); Advanced(_componentDetails, 200); Row(advanced);
        advancedToggle.CheckedChanged += (_, _) => { advanced.Visible = advancedToggle.Checked; if (advanced.Visible) page.ScrollControlIntoView(advanced); };
        page.Controls.Add(layout); return page;
    }

    Panel BuildAddonsPage()
    {
        var page = Page();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var actions = Flow();
        actions.Controls.Add(Command("Import add-on", "\uE710", ImportAddon));
        actions.Controls.Add(Command("Enable / disable", "\uE8FB", ToggleAddon));
        actions.Controls.Add(Command("Remove from library", "\uE74D", RemoveAddon));
        actions.Controls.Add(Command("Refresh", "\uE72C", RefreshAddons));
        layout.Controls.Add(actions, 0, 0);
        _addonList.Columns.Add("Add-on", 320);
        _addonList.Columns.Add("Architecture", 130);
        _addonList.Columns.Add("State", 120);
        layout.Controls.Add(_addonList, 0, 1);
        page.Controls.Add(layout);
        return page;
    }

    Panel BuildHistoryPage()
    {
        var page = Page();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var actions = Flow();
        actions.Controls.Add(Command("Refresh", "\uE72C", RefreshHistory));
        actions.Controls.Add(Command("Copy selected", "\uE8C8", CopyHistory));
        actions.Controls.Add(Command("Copy activity", "\uE8C8", () => CopyText(string.IsNullOrEmpty(_log.Text) ? "No activity." : _log.Text)));
        layout.Controls.Add(actions, 0, 0);
        foreach (var (label, width) in new[] { ("When", 160), ("Game", 230), ("Action", 100), ("Result", 100), ("Details", 450) }) _historyView.Columns.Add(label, width);
        layout.Controls.Add(_historyView, 0, 1);
        page.Controls.Add(layout);
        return page;
    }

    Panel BuildSettingsPage()
    {
        var page = Page();
        page.AutoScroll = true;
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 15 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var h in new[] { 26, 36, 40, 40, 40, 26, 40, 30, 140, 43, 30, 130, 43, 43, 1 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
        layout.Controls.Add(Caption("Game installation defaults"), 0, 0);
        layout.Controls.Add(_copyNvidiaDll, 0, 1);
        _defaultRenderer.Items.AddRange(["Phoenix DLSS5", "NVIDIA DLSS5"]);
        _defaultNeuralModel.Items.AddRange(["Original", "Lightweight FP8"]);
        layout.Controls.Add(DefaultChoiceRow("Enhancement engine", _defaultRenderer), 0, 2);
        layout.Controls.Add(DefaultChoiceRow("Default neural model", _defaultNeuralModel), 0, 3);
        _defaultExecutionMode.Items.AddRange(["Automatic (recommended)", "Internal native DX12 (x64)", "External x64 helper",
            "Internal portable (experimental)", "x64 library inside x86 (experimental)", "x86 host + native driver (experimental)"]);
        layout.Controls.Add(DefaultChoiceRow("Execution mechanism", _defaultExecutionMode), 0, 4);
        layout.Controls.Add(Caption("Appearance"), 0, 5);
        _themeChoice.Items.AddRange(["Light", "Dark", "System"]);
        _themeChoice.SelectedIndex = 0;
        layout.Controls.Add(_themeChoice, 0, 6);
        layout.Controls.Add(Caption("Scan folders"), 0, 7);
        layout.Controls.Add(_rootsList, 0, 8);
        var roots = Flow();
        roots.Controls.Add(Command("Add folder", "\uE710", AddScanRoot));
        roots.Controls.Add(Command("Remove", "\uE74D", RemoveScanRoot));
        roots.Controls.Add(Command("Scan all folders", "\uE721", async () => await ScanSavedRootsAsync()));
        layout.Controls.Add(roots, 0, 9);
        layout.Controls.Add(Caption("Hidden games"), 0, 10);
        layout.Controls.Add(_hiddenList, 0, 11);
        layout.Controls.Add(Command("Show again", "\uE8FB", UnhideSelected), 0, 12);
        var update = Row(100, 180);
        update.Controls.Add(_updateStatus, 0, 0);
        update.Controls.Add(Command("Check app update", "\uE72C", async () => await CheckAppUpdateAsync()), 1, 0);
        layout.Controls.Add(update, 0, 13);
        page.Controls.Add(layout);
        return page;
    }

    Panel BuildResourcesPage()
    {
        var page = Page();
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        foreach (var (label, url) in new[] {
            ("Phoenix DLSS5", "https://github.com/RazvanManolache/dlss5-phoenix-addon"),
            ("DLSS5 Everything", "https://github.com/RazvanManolache/DLSS5-Everything"),
            ("ReShade", "https://reshade.me/"),
            ("NVIDIA Streamline", "https://github.com/NVIDIAGameWorks/Streamline"),
            ("d3d8to9", "https://github.com/crosire/d3d8to9/releases"),
            ("dgVoodoo2", "https://dege.freeweb.hu/dgVoodoo2/"),
            ("DOSBox Enhanced", "https://github.com/RazvanManolache/dosbox-enhanced"),
            ("DLSS5 Swapper feature reference", "https://github.com/rakanki911/DLSS5-Swapper"),
            ("NeuralScreen feature reference (separate project)", "https://github.com/perseval-BLR/NeuralScreen"),
            ("Community compatibility reports", "https://github.com/RazvanManolache/DLSS5-Everything/issues") })
        {
            var link = Command(label, "\uE774", () => OpenResource(url));
            link.Width = 380;
            layout.Controls.Add(link);
        }
        page.Controls.Add(layout);
        return page;
    }

    void AddPage(string name, Panel page) { _pages[name] = page; _pageHost.Controls.Add(page); }
    void ShowPage(string name)
    {
        if (name == "LAN") name = "Streamer";
        var changingLibrary = name is "My Games" or "Library" && _usingMyGames != (name == "My Games");
        var changingPage = _activePage != name;
        if (name is "My Games" or "Library")
        {
            _usingMyGames = name == "My Games";
            var host = _usingMyGames ? _myGamesBody : _discoveryBody;
            if (_gameInspector.Parent != host) host.Controls.Add(_gameInspector, 1, 0);
            if (!_usingMyGames) _gameInspector.Visible = true;
        }
        _activePage = name;
        if (name == "My Games" && changingPage) RenderMyGames();
        foreach (var (key, page) in _pages) page.Visible = key == name;
        _pageTitle.Text = name == "Library" ? "Discover games" : name;
        _gameCount.Visible = name is "Library" or "My Games";
        _install.Tag = _usingMyGames ? null : "primary";
        _runExe.Tag = _usingMyGames ? "primary" : null;
        UiTheme.Apply(_install, _palette);
        UiTheme.Apply(_runExe, _palette);
        foreach (var (key, button) in _navigation) button.BackColor = key == name ? _palette.Selection : _palette.Sidebar;
        UpdateGameCount();
        if (changingLibrary)
        {
            UpdateEngineChoices();
            RefreshGameDetails();
            if (_usingMyGames && _convenienceInitialized && SelectedBaseGame() is { } selected) RecordConvenienceView(selected);
        }
        UpdateButtons();
        if (name == "History") RefreshHistory();
        if (name == "Add-ons") RefreshAddons();
    }
    void ApplyTheme()
    {
        UiTheme.Apply(this, _palette);
        _detailRoute.ForeColor = _palette.Muted;
        _componentMessage.ForeColor = _palette.Muted;
        _gameCount.ForeColor = _palette.Muted;
        _payloadStatus.ForeColor = _payload?.PhoenixAddon is not null && _payload.HasPhoenixModel && _payload.HasReShade64 ? _palette.Success : _palette.Error;
        _dropZone.BackColor = _palette.Selection;
        _dropZoneText.BackColor = _palette.Selection;
        _dropZoneText.ForeColor = _palette.Accent;
        _tileImageIndexes.Clear();
        _tileImages.Images.Clear();
        _myGameImageIndexes.Clear();
        _myGameImages.Images.Clear();
        InvalidateArtworkTiles();
        ShowPage(_activePage);
        ApplyMyGamesView();
        Invalidate(true);
    }
    void SetDropFeedback(bool active)
    {
        _dropZone.Visible = active;
        if (_shellFrame is not null) _shellFrame.RowStyles[0].Height = active ? 48 : 0;
        _dropZone.BackColor = _dropZoneText.BackColor = active ? _palette.Surface : _palette.Selection;
    }
    Button Command(string label, string glyph, Action action)
    {
        var button = new Button { Text = label, AccessibleName = label, AutoSize = true, MinimumSize = new Size(75, 32), Height = 34, Margin = new Padding(0, 0, 7, 5), TextImageRelation = TextImageRelation.ImageBeforeText, ImageAlign = ContentAlignment.MiddleLeft, Padding = new Padding(3, 0, 3, 0), Image = UiTheme.Icon(glyph, Color.FromArgb(99, 137, 139)), AutoEllipsis = true };
        _commandButtons.Add((button, label));
        button.Click += (_, _) => { if (CanRunCommand(label)) action(); };
        _tips.SetToolTip(button, label);
        button.Disposed += (_, _) => button.Image?.Dispose();
        return button;
    }
    static Panel Page() => new() { Dock = DockStyle.Fill, Visible = false, Margin = Padding.Empty };
    static FlowLayoutPanel Flow() => new() { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, AutoScroll = true };
    static Label Caption(string text) => new() { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    static TableLayoutPanel Row(float percent, params int[] widths)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = widths.Length + 1, RowCount = 1, Margin = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, percent));
        foreach (var width in widths) row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
        return row;
    }
    void ShowActivity()
    {
        using var form = new Form { Text = "Activity", Size = new Size(880, 460), StartPosition = FormStartPosition.CenterParent, Font = Font };
        var activity = new TextBox { Text = _log.Text, Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
        form.Controls.Add(activity);
        UiTheme.Apply(form, _palette);
        form.ShowDialog(this);
    }
    void OpenDirectory(string path) => StartExternalProcess(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
    void OpenResource(string url)
    {
        try { StartExternalProcess(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { SetOutcome("Could not open resource: " + ex.Message); }
    }
}
