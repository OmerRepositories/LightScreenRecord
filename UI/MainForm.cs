using System.Diagnostics;
using LiteRec.Audio;
using LiteRec.Capture;
using LiteRec.Interop;
using LiteRec.Recording;

namespace LiteRec.UI;

/// <summary>
/// The control panel: capture area, audio, video and output settings plus Start / Pause / Stop.
/// The layout is built from nested TableLayoutPanels so it scales with DPI and mirrors itself
/// automatically when the Hebrew (right-to-left) language is selected.
/// </summary>
internal sealed class MainForm : Form
{
    private const int HotkeyStartPause = 1;
    private const int HotkeyStop = 2;

    private static readonly Color RecordingColor = Color.FromArgb(220, 38, 38);
    private static readonly Color PausedColor = Color.FromArgb(234, 138, 0);
    private static readonly Color BusyColor = Color.FromArgb(37, 99, 235);

    /// <summary>ComboBox entry that pairs display text with a typed value.</summary>
    private sealed record Item<T>(string Text, T Value)
    {
        public override string ToString() => Text;
    }

    private readonly Recorder _recorder = new();
    private readonly AppSettings _settings;
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 250 };
    private readonly ToolTip _toolTip = new();
    private readonly System.Windows.Forms.Timer _outlineTimer = new() { Interval = 150 }; // follows the selected window
    private Rectangle? _recordedRegion; // custom region currently being recorded

    // Header
    private readonly Label _title = new() { AutoSize = true, Anchor = AnchorStyles.Left, Text = "LiteRec" };
    private readonly ComboBox _language = NewCombo(110);

    // Capture area
    private readonly GroupBox _captureGroup = new();
    private readonly RadioButton _screenMode = NewRadio();
    private readonly RadioButton _windowMode = NewRadio();
    private readonly RadioButton _regionMode = NewRadio();
    private readonly ComboBox _screens = NewCombo();
    private readonly ComboBox _windows = NewCombo();
    private readonly Label _regionInfo = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Button _selectRegion = NewButton();

    // Audio
    private readonly GroupBox _audioGroup = new();
    private readonly CheckBox _systemAudio = NewCheck();
    private readonly CheckBox _microphone = NewCheck();
    private readonly ComboBox _systemDevices = NewCombo();
    private readonly ComboBox _micDevices = NewCombo();
    private readonly TrackBar _systemVolume = NewVolume();
    private readonly TrackBar _micVolume = NewVolume();

    // Video
    private readonly GroupBox _videoGroup = new();
    private readonly Label _resolutionLabel = NewLabel();
    private readonly Label _qualityLabel = NewLabel();
    private readonly Label _motionLabel = NewLabel();
    private readonly ComboBox _resolution = NewCombo(); // value = maximum video height (0 = original)
    private readonly ComboBox _quality = NewCombo();
    private readonly ComboBox _motion = NewCombo();     // value = frames per second
    private readonly CheckBox _cursor = NewCheck();
    private readonly CheckBox _hidePanel = NewCheck();

    // Output
    private readonly GroupBox _outputGroup = new();
    private readonly TextBox _outputPath = new() { ReadOnly = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, RightToLeft = RightToLeft.No };
    private readonly Button _browse = NewButton();
    private readonly Button _openFolder = NewButton();

    // Status and controls
    private readonly Label _statusDot = new() { AutoSize = true, Anchor = AnchorStyles.Left, Text = "●" };
    private readonly Label _status = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Label _elapsed = new() { AutoSize = true, Anchor = AnchorStyles.Right, Text = "00:00:00", RightToLeft = RightToLeft.No };
    private readonly Label _stats = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Button _start = NewBigButton();
    private readonly Button _pause = NewBigButton();
    private readonly Button _stop = NewBigButton();
    private readonly LinkLabel _lastFile = new() { AutoSize = true, Anchor = AnchorStyles.Left, Visible = false };
    private readonly Label _hotkeyHint = new() { AutoSize = true, Anchor = AnchorStyles.Left };

    private List<AudioDevice> _playbackDevices = [];
    private List<AudioDevice> _microphones = [];
    private Rectangle? _region;
    private RegionOutlineForm? _outline;
    private string? _lastSavedPath;
    private string? _notice;        // one-off message shown in the status line while idle
    private bool _noticeIsError;
    private string? _hotkeyProblem; // name of a hotkey that could not be registered
    private bool _loading = true;   // suppresses change handlers while controls are being filled
    private bool _closeAfterStop;

    public MainForm()
    {
        _settings = AppSettings.Load();
        Loc.Current = _settings.Language;

        BuildLayout();
        FillStaticLists();
        ApplySettingsToControls();
        ApplyLanguage();
        WireEvents();
        UpdateControlStates();

        Icon = CreateAppIcon();
        _loading = false;
    }

    // =============================================================================================
    // Layout
    // =============================================================================================

    private void BuildLayout()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12, 10, 12, 10);

        _title.Font = new Font(Font.FontFamily, 14f, FontStyle.Bold);
        _elapsed.Font = new Font("Consolas", 20f, FontStyle.Bold);
        _statusDot.Font = new Font(Font.FontFamily, 13f);
        _status.Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold);
        _start.Font = new Font(Font, FontStyle.Bold);
        _stats.ForeColor = SystemColors.GrayText;
        _hotkeyHint.ForeColor = SystemColors.GrayText;

        // Header: app name on one side, language switch on the other.
        var header = NewTable(2);
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(_title, 0, 0);
        header.Controls.Add(_language, 1, 0);
        _language.Anchor = AnchorStyles.Right;

        // Capture area: one row per mode.
        var capture = NewTable(3);
        capture.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        capture.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        capture.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        capture.Controls.Add(_screenMode, 0, 0);
        capture.Controls.Add(_screens, 1, 0);
        capture.SetColumnSpan(_screens, 2);
        capture.Controls.Add(_windowMode, 0, 1);
        capture.Controls.Add(_windows, 1, 1);
        capture.SetColumnSpan(_windows, 2);
        capture.Controls.Add(_regionMode, 0, 2);
        capture.Controls.Add(_regionInfo, 1, 2);
        capture.Controls.Add(_selectRegion, 2, 2);
        Host(_captureGroup, capture);

        // Audio: enable checkbox, device, volume.
        var audio = NewTable(3);
        audio.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        audio.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        audio.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        audio.Controls.Add(_systemAudio, 0, 0);
        audio.Controls.Add(_systemDevices, 1, 0);
        audio.Controls.Add(_systemVolume, 2, 0);
        audio.Controls.Add(_microphone, 0, 1);
        audio.Controls.Add(_micDevices, 1, 1);
        audio.Controls.Add(_micVolume, 2, 1);
        Host(_audioGroup, audio);

        // Video: plain-language presets (resolution, quality, motion), then two checkboxes.
        var video = NewTable(4);
        video.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        video.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        video.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        video.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        video.Controls.Add(_resolutionLabel, 0, 0);
        video.Controls.Add(_resolution, 1, 0);
        video.Controls.Add(_qualityLabel, 2, 0);
        video.Controls.Add(_quality, 3, 0);
        video.Controls.Add(_motionLabel, 0, 1);
        video.Controls.Add(_motion, 1, 1);
        video.SetColumnSpan(_motion, 3);
        video.Controls.Add(_cursor, 0, 2);
        video.SetColumnSpan(_cursor, 4);
        video.Controls.Add(_hidePanel, 0, 3);
        video.SetColumnSpan(_hidePanel, 4);
        Host(_videoGroup, video);

        // Output folder.
        var output = NewTable(3);
        output.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        output.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        output.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        output.Controls.Add(_outputPath, 0, 0);
        output.Controls.Add(_browse, 1, 0);
        output.Controls.Add(_openFolder, 2, 0);
        Host(_outputGroup, output);

        // Status line: indicator dot, state text, elapsed time; statistics underneath.
        var status = NewTable(3);
        status.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        status.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        status.Controls.Add(_statusDot, 0, 0);
        status.Controls.Add(_status, 1, 0);
        status.Controls.Add(_elapsed, 2, 0);
        status.SetRowSpan(_elapsed, 2);
        status.Controls.Add(_stats, 0, 1);
        status.SetColumnSpan(_stats, 2);
        status.Margin = new Padding(0, 2, 0, 4);

        // Start / Pause / Stop.
        var buttons = NewTable(3);
        for (int i = 0; i < 3; i++)
        {
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        }

        buttons.Controls.Add(_start, 0, 0);
        buttons.Controls.Add(_pause, 1, 0);
        buttons.Controls.Add(_stop, 2, 0);

        // Root: everything stacked in a single fixed-width column.
        var root = NewTable(1);
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 500));
        root.Controls.Add(header);
        root.Controls.Add(_captureGroup);
        root.Controls.Add(_audioGroup);
        root.Controls.Add(_videoGroup);
        root.Controls.Add(_outputGroup);
        root.Controls.Add(status);
        root.Controls.Add(buttons);
        root.Controls.Add(_lastFile);
        root.Controls.Add(_hotkeyHint);

        Controls.Add(root);
        ResumeLayout(false);
        PerformLayout();
    }

    private static TableLayoutPanel NewTable(int columns) => new()
    {
        ColumnCount = columns,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Fill,
        Margin = Padding.Empty,
    };

    private static void Host(GroupBox group, TableLayoutPanel content)
    {
        group.AutoSize = true;
        group.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        group.Dock = DockStyle.Fill;
        group.Padding = new Padding(8, 3, 8, 6);
        group.Margin = new Padding(0, 4, 0, 6);
        group.Controls.Add(content);
    }

    private static ComboBox NewCombo(int width = 60) => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
        Width = width,
    };

    private static RadioButton NewRadio() => new() { AutoSize = true, Anchor = AnchorStyles.Left };

    private static CheckBox NewCheck() => new() { AutoSize = true, Anchor = AnchorStyles.Left };

    private static Label NewLabel() => new() { AutoSize = true, Anchor = AnchorStyles.Left };

    private static Button NewButton() => new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(6, 1, 6, 1),
        Anchor = AnchorStyles.Right,
    };

    private static Button NewBigButton() => new() { Dock = DockStyle.Fill, Height = 40, Margin = new Padding(3) };

    private static TrackBar NewVolume() => new()
    {
        Minimum = 0,
        Maximum = 100,
        TickStyle = TickStyle.None,
        AutoSize = false,
        Height = 24,
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
        SmallChange = 5,
        LargeChange = 10,
    };

    /// <summary>Draws a simple "record" icon at runtime so no icon file has to ship with the app.</summary>
    private static Icon CreateAppIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var ring = new Pen(Color.FromArgb(70, 70, 70), 3f);
            using var dot = new SolidBrush(RecordingColor);
            g.DrawEllipse(ring, 2, 2, 27, 27);
            g.FillEllipse(dot, 8, 8, 15, 15);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    // =============================================================================================
    // Filling controls
    // =============================================================================================

    private void FillStaticLists()
    {
        _language.Items.Add(new Item<Language>("English", Language.English));
        _language.Items.Add(new Item<Language>("עברית", Language.Hebrew));
    }

    /// <summary>Replaces a list's entries (their text depends on the language) while keeping the chosen value.</summary>
    private static void Refill<T>(ComboBox combo, T fallback, params Item<T>[] items)
    {
        T selected = combo.SelectedItem is Item<T> current ? current.Value : fallback;
        combo.Items.Clear();
        combo.Items.AddRange(items);
        SelectValue(combo, selected, fallbackIndex: 0);
    }

    private void ApplySettingsToControls()
    {
        _language.SelectedIndex = _settings.Language == Language.Hebrew ? 1 : 0;

        (_settings.CaptureMode switch { 1 => _windowMode, 2 => _regionMode, _ => _screenMode }).Checked = true;
        if (_settings.RegionWidth > 0 && _settings.RegionHeight > 0)
        {
            var saved = new Rectangle(_settings.RegionX, _settings.RegionY, _settings.RegionWidth, _settings.RegionHeight);
            if (SystemInformation.VirtualScreen.Contains(saved))
            {
                _region = saved; // only reuse the region if it still fits the current monitor layout
            }
        }

        _systemAudio.Checked = _settings.SystemAudioEnabled;
        _microphone.Checked = _settings.MicrophoneEnabled;
        _systemVolume.Value = Math.Clamp(_settings.SystemAudioVolume, 0, 100);
        _micVolume.Value = Math.Clamp(_settings.MicrophoneVolume, 0, 100);

        _cursor.Checked = _settings.CaptureCursor;
        _hidePanel.Checked = _settings.HidePanelFromRecording;
        _outputPath.Text = _settings.OutputDirectory;
    }

    private static void SelectValue<T>(ComboBox combo, T value, int fallbackIndex)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is Item<T> item && EqualityComparer<T>.Default.Equals(item.Value, value))
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = Math.Min(fallbackIndex, combo.Items.Count - 1);
        }
    }

    private static T? SelectedValue<T>(ComboBox combo) => combo.SelectedItem is Item<T> item ? item.Value : default;

    /// <summary>Sets every visible string (and the layout direction) for the current language.</summary>
    private void ApplyLanguage()
    {
        bool wasLoading = _loading;
        _loading = true;

        bool rtl = Loc.IsRightToLeft;
        RightToLeft = rtl ? RightToLeft.Yes : RightToLeft.No;
        RightToLeftLayout = rtl;
        _systemVolume.RightToLeftLayout = rtl;
        _micVolume.RightToLeftLayout = rtl;

        Text = Loc.T("Title");
        _captureGroup.Text = Loc.T("GroupCapture");
        _screenMode.Text = Loc.T("ModeScreen");
        _windowMode.Text = Loc.T("ModeWindow");
        _regionMode.Text = Loc.T("ModeRegion");
        _selectRegion.Text = Loc.T("SelectRegion");

        _audioGroup.Text = Loc.T("GroupAudio");
        _systemAudio.Text = Loc.T("SystemAudio");
        _microphone.Text = Loc.T("Microphone");

        _videoGroup.Text = Loc.T("GroupVideo");
        _resolutionLabel.Text = Loc.T("Resolution");
        _qualityLabel.Text = Loc.T("Quality");
        _motionLabel.Text = Loc.T("Motion");
        _cursor.Text = Loc.T("CaptureCursor");
        _hidePanel.Text = Loc.T("HidePanel");

        _outputGroup.Text = Loc.T("GroupOutput");
        _browse.Text = Loc.T("Browse");
        _openFolder.Text = Loc.T("OpenFolder");

        _start.Text = Loc.T("Start");
        _stop.Text = Loc.T("Stop");

        // Lists whose entries contain translated text are rebuilt, keeping the current choice.
        Refill(_resolution, _settings.ResolutionHeight,
            new Item<int>("Full HD (1080p)", 1080),
            new Item<int>("HD (720p)", 720),
            new Item<int>("SD (480p)", 480),
            new Item<int>(Loc.T("ResOriginal"), 0));
        Refill(_quality, _settings.Quality,
            new Item<VideoQuality>(Loc.T("QualityBalanced"), VideoQuality.Balanced),
            new Item<VideoQuality>(Loc.T("QualityHigh"), VideoQuality.High),
            new Item<VideoQuality>(Loc.T("QualitySmall"), VideoQuality.SmallFile));
        Refill(_motion, _settings.FramesPerSecond,
            new Item<int>(Loc.T("MotionStandard"), 30),
            new Item<int>(Loc.T("MotionSmooth"), 60),
            new Item<int>(Loc.T("MotionLight"), 15));

        FillScreens();
        FillAudioDevices(_systemDevices, _playbackDevices, SelectedValue<string>(_systemDevices) ?? _settings.SystemAudioDeviceId);
        FillAudioDevices(_micDevices, _microphones, SelectedValue<string>(_micDevices) ?? _settings.MicrophoneDeviceId);

        UpdateRegionInfo();
        UpdateStatus();
        UpdateHotkeyHint();
        UpdateLastFileLink();

        _loading = wasLoading;
    }

    private void FillScreens()
    {
        int selected = Math.Max(0, _screens.SelectedIndex);
        _screens.Items.Clear();
        Screen[] screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            string text = Loc.T("ScreenItem", i + 1, Loc.Ltr($"{screens[i].Bounds.Width} x {screens[i].Bounds.Height}"));
            if (screens[i].Primary && screens.Length > 1)
            {
                text += Loc.T("PrimarySuffix");
            }

            _screens.Items.Add(new Item<Screen>(text, screens[i]));
        }

        if (_screens.Items.Count > 0)
        {
            _screens.SelectedIndex = Math.Min(selected, _screens.Items.Count - 1);
        }
    }

    private void FillWindows()
    {
        IntPtr selected = SelectedValue<WindowInfo>(_windows)?.Handle ?? IntPtr.Zero;
        _windows.Items.Clear();
        foreach (WindowInfo window in WindowEnumerator.GetOpenWindows())
        {
            _windows.Items.Add(new Item<WindowInfo>(window.ToString(), window));
            if (window.Handle == selected)
            {
                _windows.SelectedIndex = _windows.Items.Count - 1;
            }
        }
    }

    private static void FillAudioDevices(ComboBox combo, List<AudioDevice> devices, string selectedId)
    {
        combo.Items.Clear();
        combo.Items.Add(new Item<string>(Loc.T("DefaultDevice"), string.Empty));
        foreach (AudioDevice device in devices)
        {
            combo.Items.Add(new Item<string>(device.Name, device.Id));
        }

        SelectValue(combo, selectedId, fallbackIndex: 0);
    }

    private async Task LoadAudioDevicesAsync()
    {
        _playbackDevices = await AudioDevices.GetPlaybackDevicesAsync();
        _microphones = await AudioDevices.GetMicrophonesAsync();
        if (IsDisposed)
        {
            return;
        }

        _loading = true;
        FillAudioDevices(_systemDevices, _playbackDevices, _settings.SystemAudioDeviceId);
        FillAudioDevices(_micDevices, _microphones, _settings.MicrophoneDeviceId);
        _loading = false;
    }

    // =============================================================================================
    // Events
    // =============================================================================================

    private void WireEvents()
    {
        _language.SelectedIndexChanged += (_, _) =>
        {
            if (_loading)
            {
                return;
            }

            Loc.Current = SelectedValue<Language>(_language);
            ApplyLanguage();
        };

        _screenMode.CheckedChanged += (_, _) => { UpdateControlStates(); UpdateOutline(); };
        _windowMode.CheckedChanged += (_, _) => { UpdateControlStates(); UpdateOutline(); };
        _regionMode.CheckedChanged += (_, _) => { UpdateControlStates(); UpdateOutline(); };
        _windows.DropDown += (_, _) => FillWindows(); // always show the windows that are open right now
        _windows.SelectedIndexChanged += (_, _) => UpdateOutline();
        _outlineTimer.Tick += (_, _) => UpdateOutline();
        _selectRegion.Click += (_, _) => SelectRegion();

        _systemAudio.CheckedChanged += (_, _) => OnAudioSettingChanged();
        _microphone.CheckedChanged += (_, _) => OnAudioSettingChanged();
        _systemVolume.ValueChanged += (_, _) => OnAudioSettingChanged();
        _micVolume.ValueChanged += (_, _) => OnAudioSettingChanged();

        _hidePanel.CheckedChanged += (_, _) => ApplyCaptureVisibility();
        _browse.Click += (_, _) => BrowseForFolder();
        _openFolder.Click += (_, _) => OpenOutputFolder();
        _lastFile.LinkClicked += (_, _) => RevealLastFile();

        _start.Click += async (_, _) => await StartRecordingAsync();
        _pause.Click += (_, _) => _recorder.TogglePause();
        _stop.Click += async (_, _) => await StopRecordingAsync();

        _uiTimer.Tick += (_, _) => UpdateProgress();
        _recorder.StateChanged += _ => OnRecorderStateChanged();
        _recorder.Interrupted += OnRecorderInterrupted;
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        FillWindows();
        try
        {
            await LoadAudioDevicesAsync();
        }
        catch (Exception)
        {
            // Device enumeration is best effort; "Default device" always remains available.
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // Global hotkeys are bound to the window handle, which WinForms recreates when the layout
        // direction changes, so (re)register them here.
        _hotkeyProblem = null;
        if (!NativeMethods.RegisterHotKey(Handle, HotkeyStartPause, NativeMethods.MOD_NOREPEAT, NativeMethods.VK_F9))
        {
            _hotkeyProblem = "F9";
        }

        if (!NativeMethods.RegisterHotKey(Handle, HotkeyStop, NativeMethods.MOD_NOREPEAT, NativeMethods.VK_F10))
        {
            _hotkeyProblem = _hotkeyProblem is null ? "F10" : "F9 / F10";
        }

        UpdateHotkeyHint();
        ApplyCaptureVisibility();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        NativeMethods.UnregisterHotKey(Handle, HotkeyStartPause);
        NativeMethods.UnregisterHotKey(Handle, HotkeyStop);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY)
        {
            OnHotkey((int)m.WParam);
            return;
        }

        base.WndProc(ref m);
    }

    private async void OnHotkey(int id)
    {
        if (id == HotkeyStartPause)
        {
            if (_recorder.State == RecorderState.Idle)
            {
                await StartRecordingAsync();
            }
            else
            {
                _recorder.TogglePause();
            }
        }
        else if (id == HotkeyStop)
        {
            await StopRecordingAsync();
        }
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);

        if (_recorder.State != RecorderState.Idle && !_closeAfterStop)
        {
            // Never lose a recording: finish and save it first, then close.
            e.Cancel = true;
            _closeAfterStop = true;
            await StopRecordingAsync();
            Close();
            return;
        }

        SaveSettings();
    }

    // =============================================================================================
    // Recording control
    // =============================================================================================

    private async Task StartRecordingAsync()
    {
        if (_recorder.State != RecorderState.Idle)
        {
            return;
        }

        RecordingOptions? options = BuildOptions(out string? problem);
        if (options is null)
        {
            ShowError(problem!);
            return;
        }

        SaveSettings();
        _notice = null;

        try
        {
            await _recorder.StartAsync(options);
        }
        catch (Exception ex)
        {
            ShowError(Loc.T("ErrStart", ex.Message));
            return;
        }

        _recordedRegion = _regionMode.Checked ? options.Target.ScreenBounds : null;
        UpdateOutline();
    }

    /// <summary>
    /// Shows a red frame around what will be / is being recorded: the selected window's content
    /// (as soon as a window is chosen, following it when it moves) or the custom region while recording.
    /// </summary>
    private void UpdateOutline()
    {
        Rectangle? area = null;
        WindowInfo? window = _windowMode.Checked ? SelectedValue<WindowInfo>(_windows) : null;

        if (window is not null)
        {
            if (NativeMethods.IsWindow(window.Handle) && !NativeMethods.IsIconic(window.Handle) &&
                NativeMethods.GetClientRect(window.Handle, out var client) && client.Width > 0 && client.Height > 0)
            {
                var origin = new NativeMethods.POINT();
                NativeMethods.ClientToScreen(window.Handle, ref origin);
                area = new Rectangle(origin.X, origin.Y, client.Width, client.Height);
            }
        }
        else if (_recordedRegion is not null)
        {
            area = _recordedRegion;
        }

        // Poll the window position only while a window is selected; otherwise no timer runs.
        _outlineTimer.Enabled = window is not null;

        if (area is null)
        {
            _outline?.Hide();
            return;
        }

        _outline ??= new RegionOutlineForm();
        _outline.SetTarget(area.Value);
        _outline.SetPaused(_recorder.State == RecorderState.Paused);
        if (!_outline.Visible)
        {
            _outline.Show();
        }
    }

    private async Task StopRecordingAsync(bool targetClosed = false, string? interruptError = null)
    {
        if (_recorder.State is not (RecorderState.Recording or RecorderState.Paused))
        {
            return;
        }

        string? error = interruptError;
        try
        {
            string? path = await _recorder.StopAsync();
            if (path is not null)
            {
                _lastSavedPath = path;
                _notice = targetClosed && error is null ? Loc.T("TargetClosed") : null;
                _noticeIsError = false;
            }
        }
        catch (Exception ex)
        {
            error ??= ex.Message;
        }
        finally
        {
            _recordedRegion = null;
            UpdateOutline();
        }

        UpdateLastFileLink();
        UpdateStatus();

        if (error is not null)
        {
            ShowError(Loc.T("ErrRecording", error));
        }
    }

    /// <summary>Called on a worker thread when the recorder cannot continue; finish up on the UI thread.</summary>
    private void OnRecorderInterrupted(string? error)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        BeginInvoke(async () => await StopRecordingAsync(targetClosed: error is null, interruptError: error));
    }

    /// <summary>Reads the form into a <see cref="RecordingOptions"/>; returns null and a message if something is missing.</summary>
    private RecordingOptions? BuildOptions(out string? problem)
    {
        problem = null;
        CaptureTarget target;

        if (_windowMode.Checked)
        {
            WindowInfo? window = SelectedValue<WindowInfo>(_windows);
            if (window is null)
            {
                problem = Loc.T("ErrNoWindow");
                return null;
            }

            if (!NativeMethods.IsWindow(window.Handle))
            {
                problem = Loc.T("ErrWindowGone");
                return null;
            }

            if (NativeMethods.IsIconic(window.Handle))
            {
                problem = Loc.T("ErrWindowMinimized");
                return null;
            }

            target = CaptureTarget.ForWindow(window.Handle);
        }
        else if (_regionMode.Checked)
        {
            if (_region is null)
            {
                problem = Loc.T("ErrNoRegion");
                return null;
            }

            target = CaptureTarget.ForRegion(_region.Value);
        }
        else
        {
            Screen? screen = SelectedValue<Screen>(_screens);
            if (screen is null)
            {
                problem = Loc.T("ErrNoScreen");
                return null;
            }

            target = CaptureTarget.ForScreen(screen);
        }

        return new RecordingOptions
        {
            Target = target,
            OutputDirectory = _outputPath.Text,
            FramesPerSecond = SelectedValue<int>(_motion),
            MaxHeight = SelectedValue<int>(_resolution),
            Quality = SelectedValue<VideoQuality>(_quality),
            CaptureCursor = _cursor.Checked,
            SystemAudioDeviceId = _systemAudio.Checked ? SelectedValue<string>(_systemDevices) ?? string.Empty : null,
            MicrophoneDeviceId = _microphone.Checked ? SelectedValue<string>(_micDevices) ?? string.Empty : null,
            SystemAudioGain = _systemVolume.Value / 100.0,
            MicrophoneGain = _micVolume.Value / 100.0,
        };
    }

    // =============================================================================================
    // UI state
    // =============================================================================================

    private void OnRecorderStateChanged()
    {
        RecorderState state = _recorder.State;
        _outline?.SetPaused(state == RecorderState.Paused);
        if (state == RecorderState.Idle)
        {
            UpdateOutline();
        }

        // The timer only runs while there is something to update, so the idle app uses no CPU.
        _uiTimer.Enabled = state is RecorderState.Recording or RecorderState.Paused;
        UpdateControlStates();
        UpdateStatus();
        UpdateProgress();
    }

    private void UpdateControlStates()
    {
        RecorderState state = _recorder.State;
        bool idle = state == RecorderState.Idle;
        bool active = state is RecorderState.Recording or RecorderState.Paused;

        _captureGroup.Enabled = idle;
        _videoGroup.Enabled = idle;
        _outputGroup.Enabled = idle;
        _language.Enabled = idle;

        _screens.Enabled = _screenMode.Checked;
        _windows.Enabled = _windowMode.Checked;
        _selectRegion.Enabled = _regionMode.Checked;

        // While recording, the checkboxes act as live mute switches and the sliders as live volume;
        // only the device choice is locked.
        _audioGroup.Enabled = idle || active;
        _systemDevices.Enabled = idle && _systemAudio.Checked;
        _micDevices.Enabled = idle && _microphone.Checked;
        _systemVolume.Enabled = _systemAudio.Checked;
        _micVolume.Enabled = _microphone.Checked;

        _start.Enabled = idle;
        _pause.Enabled = active;
        _stop.Enabled = active;
        _pause.Text = Loc.T(state == RecorderState.Paused ? "Resume" : "Pause");
    }

    private void UpdateStatus()
    {
        (string text, Color color) = _recorder.State switch
        {
            RecorderState.Starting => (Loc.T("StatusStarting"), BusyColor),
            RecorderState.Recording => (Loc.T("StatusRecording"), RecordingColor),
            RecorderState.Paused => (Loc.T("StatusPaused"), PausedColor),
            RecorderState.Stopping => (Loc.T("StatusSaving"), BusyColor),
            _ => (Loc.T("StatusReady"), SystemColors.GrayText),
        };

        _status.Text = text;
        _statusDot.ForeColor = color;
        _pause.Text = Loc.T(_recorder.State == RecorderState.Paused ? "Resume" : "Pause");

        if (_recorder.State == RecorderState.Idle)
        {
            _stats.Text = _notice ?? string.Empty;
            _stats.ForeColor = _notice is not null && _noticeIsError ? RecordingColor : SystemColors.GrayText;
        }
    }

    private void UpdateProgress()
    {
        if (_recorder.State == RecorderState.Idle)
        {
            return;
        }

        // Both read-outs are refreshed together once per recorded second; assigning unchanged text
        // would still trigger a layout pass, so skip it.
        TimeSpan elapsed = _recorder.Elapsed;
        string time = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        if (time != _elapsed.Text || _stats.Text.Length == 0)
        {
            _elapsed.Text = time;
            _stats.ForeColor = SystemColors.GrayText;
            _stats.Text = Loc.T("Stats", _recorder.FramesWritten, _recorder.FramesDropped);
        }
    }

    private void UpdateRegionInfo()
    {
        _regionInfo.Text = _region is { } r
            ? Loc.Ltr($"{r.Width} x {r.Height}  ({r.X}, {r.Y})")
            : Loc.T("NoRegion");
    }

    private void UpdateHotkeyHint()
    {
        bool problem = _hotkeyProblem is not null;
        _hotkeyHint.Text = problem ? Loc.T("ErrHotkey", _hotkeyProblem!) : Loc.T("HotkeyHint");
        _hotkeyHint.ForeColor = problem ? PausedColor : SystemColors.GrayText;
    }

    private void UpdateLastFileLink()
    {
        _lastFile.Visible = _lastSavedPath is not null;
        if (_lastSavedPath is not null)
        {
            _lastFile.Text = Loc.T("Saved", Loc.Ltr(Path.GetFileName(_lastSavedPath)));
        }
    }

    private void ShowError(string message)
    {
        _notice = message.Split('\n')[0];
        _noticeIsError = true;
        UpdateStatus();

        MessageBoxOptions options = Loc.IsRightToLeft ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : 0;
        MessageBox.Show(this, message, Loc.T("ErrorTitle"), MessageBoxButtons.OK, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1, options);
    }

    // =============================================================================================
    // Smaller actions
    // =============================================================================================

    private void SelectRegion()
    {
        // Get the panel out of the way so the user can draw over whatever is underneath it.
        Hide();
        try
        {
            using var selector = new RegionSelectorForm(Loc.T("RegionHint"));
            if (selector.ShowDialog() == DialogResult.OK && selector.Selection is { } selection)
            {
                _region = selection;
                UpdateRegionInfo();
            }
        }
        finally
        {
            Show();
            Activate();
        }
    }

    private void OnAudioSettingChanged()
    {
        _toolTip.SetToolTip(_systemVolume, $"{_systemVolume.Value}%");
        _toolTip.SetToolTip(_micVolume, $"{_micVolume.Value}%");
        if (_loading)
        {
            return;
        }

        UpdateControlStates();
        _recorder.SetAudioGains(
            _systemAudio.Checked ? _systemVolume.Value / 100.0 : 0.0,
            _microphone.Checked ? _micVolume.Value / 100.0 : 0.0);
    }

    /// <summary>Optionally makes this window invisible to screen capture (it stays visible to the user).</summary>
    private void ApplyCaptureVisibility()
    {
        if (IsHandleCreated)
        {
            NativeMethods.SetWindowDisplayAffinity(Handle,
                _hidePanel.Checked ? NativeMethods.WDA_EXCLUDEFROMCAPTURE : NativeMethods.WDA_NONE);
        }
    }

    private void BrowseForFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = Loc.T("BrowseTitle"),
            UseDescriptionForTitle = true,
            SelectedPath = _outputPath.Text,
            ShowNewFolderButton = true,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _outputPath.Text = dialog.SelectedPath;
            SaveSettings();
        }
    }

    private void OpenOutputFolder()
    {
        try
        {
            Directory.CreateDirectory(_outputPath.Text);
            Process.Start(new ProcessStartInfo(_outputPath.Text) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void RevealLastFile()
    {
        if (_lastSavedPath is not null && File.Exists(_lastSavedPath))
        {
            Process.Start("explorer.exe", $"/select,\"{_lastSavedPath}\"");
        }
    }

    private void SaveSettings()
    {
        _settings.Language = Loc.Current;
        _settings.CaptureMode = _windowMode.Checked ? 1 : _regionMode.Checked ? 2 : 0;
        if (_region is { } r)
        {
            (_settings.RegionX, _settings.RegionY, _settings.RegionWidth, _settings.RegionHeight) = (r.X, r.Y, r.Width, r.Height);
        }

        _settings.SystemAudioEnabled = _systemAudio.Checked;
        _settings.SystemAudioDeviceId = SelectedValue<string>(_systemDevices) ?? string.Empty;
        _settings.SystemAudioVolume = _systemVolume.Value;
        _settings.MicrophoneEnabled = _microphone.Checked;
        _settings.MicrophoneDeviceId = SelectedValue<string>(_micDevices) ?? string.Empty;
        _settings.MicrophoneVolume = _micVolume.Value;
        _settings.FramesPerSecond = SelectedValue<int>(_motion);
        _settings.ResolutionHeight = SelectedValue<int>(_resolution);
        _settings.Quality = SelectedValue<VideoQuality>(_quality);
        _settings.CaptureCursor = _cursor.Checked;
        _settings.HidePanelFromRecording = _hidePanel.Checked;
        _settings.OutputDirectory = _outputPath.Text;
        _settings.Save();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _uiTimer.Dispose();
            _outlineTimer.Dispose();
            _toolTip.Dispose();
            _outline?.Dispose();
        }

        base.Dispose(disposing);
    }
}
