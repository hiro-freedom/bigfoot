using System;
using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Windows;
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
    private readonly ScaleTransform[] _barScales;

    private double _targetRatio = 0.5;
    private double _targetLoudness;
    private double _smoothX;
    private double _smoothOpacity;
    private double _wavePhase;

    private static readonly double[] BaseBarProfile =
    {
        0.35, 0.48, 0.65, 0.85, 1.00, 0.85, 0.65, 0.48, 0.35
    };

    public MainWindow()
    {
        InitializeComponent();

        _barScales = new[]
        {
            Bar0Scale, Bar1Scale, Bar2Scale, Bar3Scale, Bar4Scale, Bar5Scale, Bar6Scale, Bar7Scale, Bar8Scale
        };

        InitializeOverlayWindow();

        _audioMonitor = new AudioMonitor();
        _audioMonitor.LevelCalculated += OnLevelCalculated;
        _audioMonitor.Start();

        _notifyIcon = CreateNotifyIcon();

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
        Loaded += (_, _) => AlignToPrimaryScreen();
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
        Dispatcher.BeginInvoke(AlignToPrimaryScreen);
    }

    private static Forms.NotifyIcon CreateNotifyIcon()
    {
        var trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.Add("Exit", null, (_, _) => Application.Current.Shutdown());

        return new Forms.NotifyIcon
        {
            Text = "Sound Source Visualizer",
            Icon = SystemIcons.Information,
            Visible = true,
            ContextMenuStrip = trayMenu
        };
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
        var targetX = usableWidth * ratio;
        // Low-pass smoothing to reduce jitter on fast stereo changes.
        _smoothX += (targetX - _smoothX) * 0.20;

        var targetOpacity = loudness < 0.02 ? 0.0 : 0.30 + loudness * 0.70;
        _smoothOpacity += (targetOpacity - _smoothOpacity) * 0.15;

        IndicatorTransform.X = _smoothX;
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
