using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.Wave;
using System.Windows;

namespace bigfoot;

public partial class FrequencyBandAnalysisWindow : Window, IDisposable
{
    private readonly DispatcherTimer _timelineTimer;

    private AudioFileReader? _audioFileReader;
    private WaveOutEvent? _waveOut;
    private bool _isDraggingTimeline;
    private bool _isDisposed;
    private float[] _monoSamples = Array.Empty<float>();

    public FrequencyBandAnalysisWindow()
    {
        InitializeComponent();

        _timelineTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _timelineTimer.Tick += OnTimelineTimerTick;

        Closed += OnWindowClosed;
    }

    private void OnOpenFileClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Wave files (*.wav)|*.wav",
            Title = "Open WAV file for analysis"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            LoadAudioFile(dialog.FileName);
        }
        catch (Exception ex)
        {
            UpdateStatus($"Status: Failed to load WAV ({ex.Message})");
            MessageBox.Show(this, $"Failed to load file:\n{ex.Message}", "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadAudioFile(string filePath)
    {
        DisposePlaybackResources();

        _audioFileReader = new AudioFileReader(filePath);
        _waveOut = new WaveOutEvent();
        _waveOut.Init(_audioFileReader);
        _waveOut.PlaybackStopped += OnPlaybackStopped;

        TimelineSlider.Minimum = 0;
        TimelineSlider.Maximum = _audioFileReader.TotalTime.TotalSeconds;
        TimelineSlider.Value = 0;
        TimelineSlider.IsEnabled = true;

        PlayPauseButton.IsEnabled = true;
        StopButton.IsEnabled = true;
        PlayPauseButton.Content = "Play";

        FilePathText.Text = filePath;
        DurationText.Text = FormatTime(_audioFileReader.TotalTime);
        CurrentTimeText.Text = "00:00";

        _monoSamples = ExtractMonoSamplesForWaveform(filePath);
        RedrawWaveform();

        UpdateStatus("Status: WAV loaded and ready");
    }

    private static float[] ExtractMonoSamplesForWaveform(string filePath)
    {
        using var reader = new AudioFileReader(filePath);
        var waveFormat = reader.WaveFormat;
        var channels = Math.Max(1, waveFormat.Channels);

        var result = new List<float>(Math.Max(4096, (int)Math.Min(2_000_000, reader.Length / 4)));
        var buffer = new float[8192];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i += channels)
            {
                float sum = 0f;
                var channelRead = Math.Min(channels, read - i);
                for (var channel = 0; channel < channelRead; channel++)
                {
                    sum += buffer[i + channel];
                }

                result.Add(sum / channelRead);
            }
        }

        return result.ToArray();
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        if (_waveOut is null)
        {
            return;
        }

        if (_waveOut.PlaybackState == PlaybackState.Playing)
        {
            _waveOut.Pause();
            PlayPauseButton.Content = "Play";
            _timelineTimer.Stop();
            UpdateStatus("Status: Paused");
            return;
        }

        _waveOut.Play();
        PlayPauseButton.Content = "Pause";
        _timelineTimer.Start();
        UpdateStatus("Status: Playing");
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        if (_waveOut is null || _audioFileReader is null)
        {
            return;
        }

        _waveOut.Stop();
        _audioFileReader.CurrentTime = TimeSpan.Zero;
        SyncTimelineFromReader();
        UpdateStatus("Status: Stopped");
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _timelineTimer.Stop();
            PlayPauseButton.Content = "Play";

            if (e.Exception is not null)
            {
                UpdateStatus($"Status: Playback error ({e.Exception.Message})");
            }
        });
    }

    private void OnTimelineTimerTick(object? sender, EventArgs e)
    {
        SyncTimelineFromReader();
    }

    private void SyncTimelineFromReader()
    {
        if (_audioFileReader is null || _isDraggingTimeline)
        {
            return;
        }

        var currentSeconds = _audioFileReader.CurrentTime.TotalSeconds;
        currentSeconds = Math.Clamp(currentSeconds, TimelineSlider.Minimum, TimelineSlider.Maximum);
        TimelineSlider.Value = currentSeconds;
        CurrentTimeText.Text = FormatTime(_audioFileReader.CurrentTime);
    }

    private void OnTimelineValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_audioFileReader is null)
        {
            return;
        }

        if (_isDraggingTimeline)
        {
            CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(Math.Max(0, TimelineSlider.Value)));
            return;
        }

        if (Math.Abs(_audioFileReader.CurrentTime.TotalSeconds - TimelineSlider.Value) > 0.05)
        {
            _audioFileReader.CurrentTime = TimeSpan.FromSeconds(Math.Max(0, TimelineSlider.Value));
        }

        CurrentTimeText.Text = FormatTime(_audioFileReader.CurrentTime);
    }

    private void OnTimelineMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingTimeline = true;
    }

    private void OnTimelineMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        CompleteTimelineDrag();
    }

    private void OnTimelineLostMouseCapture(object sender, MouseEventArgs e)
    {
        CompleteTimelineDrag();
    }

    private void CompleteTimelineDrag()
    {
        _isDraggingTimeline = false;

        if (_audioFileReader is null)
        {
            return;
        }

        _audioFileReader.CurrentTime = TimeSpan.FromSeconds(Math.Max(0, TimelineSlider.Value));
        CurrentTimeText.Text = FormatTime(_audioFileReader.CurrentTime);
    }

    private void OnWaveformCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RedrawWaveform();
    }

    private void RedrawWaveform()
    {
        WaveformCanvas.Children.Clear();

        var width = WaveformCanvas.ActualWidth;
        var height = WaveformCanvas.ActualHeight;
        if (width < 2 || height < 2)
        {
            return;
        }

        var centerY = height / 2;
        var centerLine = new Line
        {
            X1 = 0,
            Y1 = centerY,
            X2 = width,
            Y2 = centerY,
            Stroke = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
            StrokeThickness = 1
        };
        WaveformCanvas.Children.Add(centerLine);

        if (_monoSamples.Length == 0)
        {
            return;
        }

        var topPoints = new PointCollection();
        var bottomPoints = new PointCollection();
        var pixels = Math.Max(2, (int)Math.Floor(width));
        var samplesPerPixel = Math.Max(1, _monoSamples.Length / pixels);
        var halfHeight = height * 0.45;

        for (var x = 0; x < pixels; x++)
        {
            var start = x * samplesPerPixel;
            if (start >= _monoSamples.Length)
            {
                break;
            }

            var end = Math.Min(_monoSamples.Length, start + samplesPerPixel);
            var peak = 0f;
            for (var i = start; i < end; i++)
            {
                var value = Math.Abs(_monoSamples[i]);
                if (value > peak)
                {
                    peak = value;
                }
            }

            var amplitude = peak * halfHeight;
            topPoints.Add(new Point(x, centerY - amplitude));
            bottomPoints.Add(new Point(x, centerY + amplitude));
        }

        if (topPoints.Count < 2)
        {
            return;
        }

        var topWaveform = new Polyline
        {
            Points = topPoints,
            Stroke = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
            StrokeThickness = 1.2,
            SnapsToDevicePixels = true
        };

        var bottomWaveform = new Polyline
        {
            Points = bottomPoints,
            Stroke = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
            StrokeThickness = 1.2,
            SnapsToDevicePixels = true
        };

        WaveformCanvas.Children.Add(topWaveform);
        WaveformCanvas.Children.Add(bottomWaveform);
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time.TotalHours >= 1)
        {
            return time.ToString(@"hh\:mm\:ss");
        }

        return time.ToString(@"mm\:ss");
    }

    private void UpdateStatus(string status)
    {
        StatusText.Text = status;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        Dispose();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Closed -= OnWindowClosed;

        _timelineTimer.Stop();
        _timelineTimer.Tick -= OnTimelineTimerTick;

        DisposePlaybackResources();
    }

    private void DisposePlaybackResources()
    {
        _timelineTimer.Stop();

        if (_waveOut is not null)
        {
            _waveOut.PlaybackStopped -= OnPlaybackStopped;

            try
            {
                _waveOut.Stop();
            }
            catch
            {
                // Ignore stop failures while disposing playback.
            }

            _waveOut.Dispose();
            _waveOut = null;
        }

        _audioFileReader?.Dispose();
        _audioFileReader = null;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
