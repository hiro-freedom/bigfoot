using System;
using Drawing = System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace bigfoot;

public partial class MainWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;

    private readonly DispatcherTimer _uiTimer;
    private readonly AudioMonitor _audioMonitor;
    private readonly object _audioLock = new();
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly System.Windows.Shapes.Rectangle[] _bars;
    private readonly ScaleTransform[] _barScales;
    private readonly Forms.ToolStripDropDown _quickSettingsDropDown;
    private readonly Forms.TrackBar _thresholdTrackBar;
    private readonly Forms.ToolStripLabel _thresholdLabel;
    private readonly Forms.TrackBar _verticalTrackBar;
    private readonly Forms.ToolStripLabel _verticalLabel;
    private readonly Forms.ToolStripMenuItem _excludeMyselfMenuItem;
    private readonly Forms.ToolStripMenuItem _themeDefaultMenuItem;
    private readonly Forms.ToolStripMenuItem _themeRedMenuItem;
    private readonly Forms.ToolStripMenuItem _themeBlackMenuItem;
    private readonly Forms.ToolStripMenuItem _themeWhiteMenuItem;
    private readonly AppSettings _settings;

    private double _targetRatio = 0.5;
    private double _targetLoudness;
    private double _smoothOpacity;
    private double _wavePhase;
    private double _silenceThreshold = 0.02;
    private double _verticalPositionRatio = 0.08;

    private static readonly double[] BaseBarProfile =
    {
        0.35, 0.48, 0.65, 0.85, 1.00, 0.85, 0.65, 0.48, 0.35
    };

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettingsStore.Load();
        _silenceThreshold = Math.Clamp(_settings.SilenceThreshold, 0.0, 0.2);
        _verticalPositionRatio = Math.Clamp(_settings.VerticalPositionRatio, 0.0, 1.0);

        _bars = new[]
        {
            Bar0, Bar1, Bar2, Bar3, Bar4, Bar5, Bar6, Bar7, Bar8
        };
        _barScales = new[]
        {
            Bar0Scale, Bar1Scale, Bar2Scale, Bar3Scale, Bar4Scale, Bar5Scale, Bar6Scale, Bar7Scale, Bar8Scale
        };

        InitializeOverlayWindow();

        _audioMonitor = new AudioMonitor();
        _audioMonitor.ExcludeMyselfEnabled = _settings.ExcludeMyself;
        _audioMonitor.LevelCalculated += OnLevelCalculated;
        _audioMonitor.Start();

        (_notifyIcon, _excludeMyselfMenuItem, _themeDefaultMenuItem, _themeRedMenuItem, _themeBlackMenuItem, _themeWhiteMenuItem) = CreateNotifyIcon();
        (_quickSettingsDropDown, _thresholdTrackBar, _thresholdLabel, _verticalTrackBar, _verticalLabel) = CreateQuickSettingsDropDown();
        _thresholdTrackBar.Value = Math.Clamp((int)Math.Round(_silenceThreshold * 1000), _thresholdTrackBar.Minimum, _thresholdTrackBar.Maximum);
        _verticalTrackBar.Value = Math.Clamp((int)Math.Round(_verticalPositionRatio * 100), _verticalTrackBar.Minimum, _verticalTrackBar.Maximum);
        _thresholdTrackBar.ValueChanged += OnThresholdTrackBarValueChanged;
        _verticalTrackBar.ValueChanged += OnVerticalTrackBarValueChanged;

        _excludeMyselfMenuItem.Checked = _settings.ExcludeMyself;
        _excludeMyselfMenuItem.CheckedChanged += OnExcludeMyselfCheckedChanged;
        _themeDefaultMenuItem.Click += OnThemeMenuClick;
        _themeRedMenuItem.Click += OnThemeMenuClick;
        _themeBlackMenuItem.Click += OnThemeMenuClick;
        _themeWhiteMenuItem.Click += OnThemeMenuClick;

        ApplyTheme(_settings.ColorTheme);
        UpdateThresholdLabel();
        UpdateVerticalLabel();
        UpdateIndicatorVerticalPosition();
        _notifyIcon.MouseUp += OnNotifyIconMouseUp;

        _uiTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _uiTimer.Tick += OnUiTick;
        _uiTimer.Start();

        Closed += OnClosed;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        exStyle |= WsExTransparent | WsExToolWindow | WsExNoActivate;
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(exStyle));

        AlignToPrimaryScreen();
    }

    private void InitializeOverlayWindow()
    {
        ShowActivated = false;
        Focusable = false;
        Loaded += (_, _) =>
        {
            AlignToPrimaryScreen();
            UpdateIndicatorVerticalPosition();
        };
    }

    private void AlignToPrimaryScreen()
    {
        var primaryScreen = Forms.Screen.PrimaryScreen;
        if (primaryScreen is null)
        {
            return;
        }

        // Convert physical pixels to WPF DIPs to avoid DPI mismatch.
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = primaryScreen.Bounds.Left / dpi.DpiScaleX;
        Top = primaryScreen.Bounds.Top / dpi.DpiScaleY;
        Width = primaryScreen.Bounds.Width / dpi.DpiScaleX;
        Height = primaryScreen.Bounds.Height / dpi.DpiScaleY;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            AlignToPrimaryScreen();
            UpdateIndicatorVerticalPosition();
        });
    }

    private static (Forms.NotifyIcon NotifyIcon, Forms.ToolStripMenuItem ExcludeMenuItem, Forms.ToolStripMenuItem ThemeDefaultMenuItem, Forms.ToolStripMenuItem ThemeRedMenuItem, Forms.ToolStripMenuItem ThemeBlackMenuItem, Forms.ToolStripMenuItem ThemeWhiteMenuItem) CreateNotifyIcon()
    {
        var trayMenu = new Forms.ContextMenuStrip();

        var excludeMenuItem = new Forms.ToolStripMenuItem("Exclude Myself")
        {
            CheckOnClick = true
        };

        var themeMenu = new Forms.ToolStripMenuItem("Color Theme");
        var themeDefaultMenuItem = new Forms.ToolStripMenuItem("Default") { CheckOnClick = true };
        var themeRedMenuItem = new Forms.ToolStripMenuItem("Waveform near red") { CheckOnClick = true };
        var themeBlackMenuItem = new Forms.ToolStripMenuItem("Waveform near black") { CheckOnClick = true };
        var themeWhiteMenuItem = new Forms.ToolStripMenuItem("Waveform near white") { CheckOnClick = true };
        themeMenu.DropDownItems.Add(themeDefaultMenuItem);
        themeMenu.DropDownItems.Add(themeRedMenuItem);
        themeMenu.DropDownItems.Add(themeBlackMenuItem);
        themeMenu.DropDownItems.Add(themeWhiteMenuItem);

        trayMenu.Items.Add(themeMenu);
        trayMenu.Items.Add(excludeMenuItem);
        trayMenu.Items.Add(new Forms.ToolStripSeparator());
        trayMenu.Items.Add("Exit", null, (_, _) => Application.Current.Shutdown());

        var notifyIcon = new Forms.NotifyIcon
        {
            Text = "bigfoot",
            Icon = Drawing.SystemIcons.Information,
            Visible = true,
            ContextMenuStrip = trayMenu
        };

        return (notifyIcon, excludeMenuItem, themeDefaultMenuItem, themeRedMenuItem, themeBlackMenuItem, themeWhiteMenuItem);
    }

    private static (Forms.ToolStripDropDown DropDown, Forms.TrackBar ThresholdTrackBar, Forms.ToolStripLabel ThresholdLabel, Forms.TrackBar VerticalTrackBar, Forms.ToolStripLabel VerticalLabel) CreateQuickSettingsDropDown()
    {
        var thresholdLabel = new Forms.ToolStripLabel();
        var thresholdTrackBar = new Forms.TrackBar
        {
            Minimum = 0,
            Maximum = 200,
            TickFrequency = 10,
            Width = 180,
            Height = 36,
            AutoSize = false
        };

        var thresholdHost = new Forms.ToolStripControlHost(thresholdTrackBar)
        {
            AutoSize = false,
            Width = 190,
            Height = 42,
            Margin = new Forms.Padding(6, 0, 6, 2)
        };

        var verticalLabel = new Forms.ToolStripLabel();
        var verticalTrackBar = new Forms.TrackBar
        {
            Minimum = 0,
            Maximum = 100,
            TickFrequency = 10,
            Width = 180,
            Height = 36,
            AutoSize = false
        };

        var verticalHost = new Forms.ToolStripControlHost(verticalTrackBar)
        {
            AutoSize = false,
            Width = 190,
            Height = 42,
            Margin = new Forms.Padding(6, 0, 6, 6)
        };

        var dropDown = new Forms.ToolStripDropDown
        {
            AutoClose = true
        };

        var title = new Forms.ToolStripLabel("Quick Settings")
        {
            Margin = new Forms.Padding(8, 6, 8, 2),
            Font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Bold)
        };

        thresholdLabel.Margin = new Forms.Padding(8, 0, 8, 2);
        verticalLabel.Margin = new Forms.Padding(8, 0, 8, 2);

        dropDown.Items.Add(title);
        dropDown.Items.Add(thresholdLabel);
        dropDown.Items.Add(thresholdHost);
        dropDown.Items.Add(verticalLabel);
        dropDown.Items.Add(verticalHost);
        return (dropDown, thresholdTrackBar, thresholdLabel, verticalTrackBar, verticalLabel);
    }

    private void OnNotifyIconMouseUp(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Left)
        {
            return;
        }

        if (_quickSettingsDropDown.Visible)
        {
            _quickSettingsDropDown.Close();
            return;
        }

        _thresholdTrackBar.Value = Math.Clamp((int)Math.Round(_silenceThreshold * 1000), _thresholdTrackBar.Minimum, _thresholdTrackBar.Maximum);
        _verticalTrackBar.Value = Math.Clamp((int)Math.Round(_verticalPositionRatio * 100), _verticalTrackBar.Minimum, _verticalTrackBar.Maximum);
        UpdateThresholdLabel();
        UpdateVerticalLabel();

        var cursor = Forms.Control.MousePosition;
        _quickSettingsDropDown.Show(cursor.X - 10, cursor.Y - _quickSettingsDropDown.GetPreferredSize(Drawing.Size.Empty).Height - 8);
    }

    private void UpdateThresholdLabel()
    {
        _thresholdLabel.Text = $"Threshold: {_silenceThreshold:F3}";
    }

    private void UpdateVerticalLabel()
    {
        _verticalLabel.Text = $"Vertical Position: {_verticalPositionRatio:P0}";
    }

    private void UpdateIndicatorVerticalPosition()
    {
        var windowHeight = ActualHeight > 0 ? ActualHeight : Height;
        var usableHeight = Math.Max(0, windowHeight - IndicatorRoot.Height);
        Canvas.SetTop(IndicatorRoot, usableHeight * _verticalPositionRatio);
    }

    private void OnThresholdTrackBarValueChanged(object? sender, EventArgs e)
    {
        _silenceThreshold = _thresholdTrackBar.Value / 1000.0;
        _settings.SilenceThreshold = _silenceThreshold;
        AppSettingsStore.Save(_settings);
        UpdateThresholdLabel();
    }

    private void OnVerticalTrackBarValueChanged(object? sender, EventArgs e)
    {
        _verticalPositionRatio = _verticalTrackBar.Value / 100.0;
        _settings.VerticalPositionRatio = _verticalPositionRatio;
        AppSettingsStore.Save(_settings);
        UpdateVerticalLabel();
        UpdateIndicatorVerticalPosition();
    }

    private void OnExcludeMyselfCheckedChanged(object? sender, EventArgs e)
    {
        _audioMonitor.ExcludeMyselfEnabled = _excludeMyselfMenuItem.Checked;
        _settings.ExcludeMyself = _excludeMyselfMenuItem.Checked;
        AppSettingsStore.Save(_settings);
    }

    private void OnThemeMenuClick(object? sender, EventArgs e)
    {
        if (sender == _themeDefaultMenuItem)
        {
            ApplyTheme("Default");
        }
        else if (sender == _themeRedMenuItem)
        {
            ApplyTheme("Red");
        }
        else if (sender == _themeBlackMenuItem)
        {
            ApplyTheme("Black");
        }
        else if (sender == _themeWhiteMenuItem)
        {
            ApplyTheme("White");
        }
    }

    private void ApplyTheme(string? theme)
    {
        var normalizedTheme = theme switch
        {
            "Black" => "Black",
            "White" => "White",
            "Red" => "Red",
            _ => "Default"
        };

        _themeDefaultMenuItem.Checked = normalizedTheme == "Default";
        _themeRedMenuItem.Checked = normalizedTheme == "Red";
        _themeBlackMenuItem.Checked = normalizedTheme == "Black";
        _themeWhiteMenuItem.Checked = normalizedTheme == "White";

        Color mainColor;
        Color centerColor;

        if (normalizedTheme == "Black")
        {
            mainColor = Color.FromRgb(22, 22, 22);
            centerColor = Color.FromRgb(70, 70, 70);
        }
        else if (normalizedTheme == "White")
        {
            mainColor = Color.FromRgb(238, 238, 238);
            centerColor = Color.FromRgb(255, 255, 255);
        }
        else if (normalizedTheme == "Red")
        {
            mainColor = Color.FromRgb(239, 68, 68);
            centerColor = Color.FromRgb(248, 113, 113);
        }
        else
        {
            mainColor = Color.FromRgb(56, 189, 248);
            centerColor = Color.FromRgb(249, 115, 113);
        }

        var mainBrush = new SolidColorBrush(mainColor);
        mainBrush.Freeze();
        var centerBrush = new SolidColorBrush(centerColor);
        centerBrush.Freeze();

        for (var i = 0; i < _bars.Length; i++)
        {
            _bars[i].Fill = i == 4 ? centerBrush : mainBrush;
        }

        _settings.ColorTheme = normalizedTheme;
        AppSettingsStore.Save(_settings);
    }

    private void OnLevelCalculated(float left, float right)
    {
        var total = left + right;
        var ratio = total > 1e-6f ? right / total : 0.5f;

        lock (_audioLock)
        {
            // ratio=0 => left, ratio=1 => right.
            _targetRatio = Clamp01(ratio);
            _targetLoudness = Math.Clamp(total * 1.8, 0.0, 1.0);
        }
    }

    private void OnUiTick(object? sender, EventArgs e)
    {
        double ratio;
        double loudness;

        lock (_audioLock)
        {
            ratio = _targetRatio;
            loudness = _targetLoudness;
        }

        var usableWidth = Math.Max(0, ActualWidth - IndicatorRoot.Width);
        var quantizedIndex = (int)Math.Round(Math.Clamp(ratio, 0.0, 1.0) * 6.0);
        var targetX = usableWidth * (quantizedIndex / 6.0);

        var targetOpacity = loudness < _silenceThreshold ? 0.0 : 0.30 + loudness * 0.70;
        _smoothOpacity += (targetOpacity - _smoothOpacity) * 0.55;

        IndicatorTransform.X = targetX;
        IndicatorRoot.Opacity = _smoothOpacity;

        _wavePhase += 0.23 + loudness * 0.42;
        var pan = ratio * 2.0 - 1.0;

        // Build a moving waveform profile while biasing bars toward sound direction.
        for (var i = 0; i < _barScales.Length; i++)
        {
            var position = (i - (_barScales.Length - 1) / 2.0) / ((_barScales.Length - 1) / 2.0);
            var sideGain = 1.0 + pan * position * 0.45;
            var wave = 0.88 + 0.32 * Math.Sin(_wavePhase + i * 0.72);
            var level = BaseBarProfile[i] * (0.36 + loudness * 1.4) * sideGain * wave;
            _barScales[i].ScaleY = Math.Clamp(level, 0.12, 1.95);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _uiTimer.Stop();
        _audioMonitor.Dispose();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _notifyIcon.MouseUp -= OnNotifyIconMouseUp;
        _thresholdTrackBar.ValueChanged -= OnThresholdTrackBarValueChanged;
        _verticalTrackBar.ValueChanged -= OnVerticalTrackBarValueChanged;
        _excludeMyselfMenuItem.CheckedChanged -= OnExcludeMyselfCheckedChanged;
        _themeDefaultMenuItem.Click -= OnThemeMenuClick;
        _themeRedMenuItem.Click -= OnThemeMenuClick;
        _themeBlackMenuItem.Click -= OnThemeMenuClick;
        _themeWhiteMenuItem.Click -= OnThemeMenuClick;
        _quickSettingsDropDown.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    private static double Clamp01(double value)
    {
        return value < 0 ? 0 : value > 1 ? 1 : value;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern IntPtr GetWindowLongPtr32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern IntPtr SetWindowLongPtr32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
    {
        // Keep compatibility with 32-bit and 64-bit process targets.
        return IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLongPtr32(hWnd, nIndex);
    }

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        return IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : SetWindowLongPtr32(hWnd, nIndex, dwNewLong);
    }
}
