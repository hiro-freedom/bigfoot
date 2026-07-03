using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    private const double MinSegmentSeconds = 0.08;
    private const double RecommendedMinSegmentSeconds = 0.20;
    private const double RecommendedMaxSegmentSeconds = 0.60;
    private const int MinSegmentsPerLabelForStrongRecommendation = 5;
    private const int MinFramesPerLabelForStrongRecommendation = 48;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly string RecommendationDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "bigfoot",
        "analysis-recommendations");

    private readonly DispatcherTimer _timelineTimer;
    private readonly ObservableCollection<SegmentAnnotation> _segments = new();
    private readonly ObservableCollection<SavedRecommendationListItem> _savedRecommendations = new();
    private readonly AppSettings _appSettings;

    private AudioFileReader? _audioFileReader;
    private WaveOutEvent? _waveOut;
    private bool _isDraggingTimeline;
    private bool _isDraggingWaveformSelection;
    private bool _isDisposed;
    private float[] _monoSamples = Array.Empty<float>();
    private int _monoSampleRate;
    private double? _selectionStartSeconds;
    private double? _selectionEndSeconds;
    private AnalysisResult? _latestAnalysis;

    public FrequencyBandAnalysisWindow()
    {
        InitializeComponent();

        _appSettings = AppSettingsStore.Load();

        SegmentListView.ItemsSource = _segments;
        SavedRecommendationListView.ItemsSource = _savedRecommendations;
        SelectionRangeText.Text = "Selection: None";
        SeekSegmentButton.IsEnabled = false;
        DeleteSegmentButton.IsEnabled = false;
        AnalysisStatsText.Text = "Analysis: waiting for labeled segments";
        RecommendedBandText.Text = "Recommended band: N/A (need labeled segments)";
        SaveRecommendationButton.IsEnabled = false;
        ProfileNameTextBox.Text = NormalizeProfileName(_appSettings.AnalysisLastProfileName);

        RefreshSavedRecommendations();

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

        (_monoSamples, _monoSampleRate) = ExtractMonoSamplesForWaveform(filePath);
        _segments.Clear();
        ClearSelection();
        _latestAnalysis = null;
        AnalysisStatsText.Text = "Analysis: waiting for labeled segments";
        RecommendedBandText.Text = "Recommended band: N/A (need labeled segments)";
        SaveRecommendationButton.IsEnabled = false;
        RedrawWaveform();
        RedrawAnalysisChart();
        UpdateSegmentActionButtons();

        UpdateStatus("Status: WAV loaded and ready");
    }

    private static (float[] Samples, int SampleRate) ExtractMonoSamplesForWaveform(string filePath)
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

        return (result.ToArray(), waveFormat.SampleRate);
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

    private void OnWaveformMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_audioFileReader is null || TimelineSlider.Maximum <= 0)
        {
            return;
        }

        var point = e.GetPosition(WaveformCanvas);
        var seconds = CanvasXToSeconds(point.X);
        _selectionStartSeconds = seconds;
        _selectionEndSeconds = seconds;
        _isDraggingWaveformSelection = true;
        WaveformCanvas.CaptureMouse();
        UpdateSelectionUi();
        RedrawWaveform();
    }

    private void OnWaveformMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingWaveformSelection || _audioFileReader is null)
        {
            return;
        }

        var point = e.GetPosition(WaveformCanvas);
        _selectionEndSeconds = CanvasXToSeconds(point.X);
        UpdateSelectionUi();
        RedrawWaveform();
    }

    private void OnWaveformMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        CompleteWaveformSelection();
    }

    private void OnWaveformLostMouseCapture(object sender, MouseEventArgs e)
    {
        CompleteWaveformSelection();
    }

    private void CompleteWaveformSelection()
    {
        if (!_isDraggingWaveformSelection)
        {
            return;
        }

        _isDraggingWaveformSelection = false;
        if (WaveformCanvas.IsMouseCaptured)
        {
            WaveformCanvas.ReleaseMouseCapture();
        }

        UpdateSelectionUi();
        RedrawWaveform();
    }

    private void OnAddFootstepClick(object sender, RoutedEventArgs e)
    {
        AddSegment("Footstep");
    }

    private void OnAddAmbienceClick(object sender, RoutedEventArgs e)
    {
        AddSegment("Ambience");
    }

    private void OnAddMixedClick(object sender, RoutedEventArgs e)
    {
        AddSegment("Mixed");
    }

    private void AddSegment(string label)
    {
        if (!TryGetNormalizedSelection(out var startSeconds, out var endSeconds))
        {
            UpdateStatus("Status: Drag on waveform first to select a segment");
            return;
        }

        var duration = endSeconds - startSeconds;
        if (duration < MinSegmentSeconds)
        {
            UpdateStatus($"Status: Segment too short (min {MinSegmentSeconds:0.00}s)");
            return;
        }

        var annotation = new SegmentAnnotation(label, startSeconds, endSeconds);
        _segments.Add(annotation);
        SegmentListView.SelectedItem = annotation;

        _latestAnalysis = null;
        AnalysisStatsText.Text = "Analysis: new segment added, re-run analysis";
        RecommendedBandText.Text = "Recommended band: N/A (analysis out of date)";
        SaveRecommendationButton.IsEnabled = false;
        RedrawAnalysisChart();

        var recommendation = duration < RecommendedMinSegmentSeconds || duration > RecommendedMaxSegmentSeconds
            ? " (outside recommended 0.20-0.60s)"
            : string.Empty;

        UpdateStatus($"Status: Added {label} segment {annotation.StartText} - {annotation.EndText}{recommendation}");
        UpdateSegmentActionButtons();
        RedrawWaveform();
    }

    private void OnSeekSegmentClick(object sender, RoutedEventArgs e)
    {
        if (SegmentListView.SelectedItem is not SegmentAnnotation annotation || _audioFileReader is null)
        {
            return;
        }

        var clampedStart = Math.Clamp(annotation.StartSeconds, 0, TimelineSlider.Maximum);
        _audioFileReader.CurrentTime = TimeSpan.FromSeconds(clampedStart);
        SyncTimelineFromReader();
        UpdateStatus($"Status: Seeked to {annotation.StartText}");
    }

    private void OnDeleteSegmentClick(object sender, RoutedEventArgs e)
    {
        if (SegmentListView.SelectedItem is not SegmentAnnotation annotation)
        {
            return;
        }

        _segments.Remove(annotation);
        _latestAnalysis = null;
        AnalysisStatsText.Text = "Analysis: waiting for labeled segments";
        RecommendedBandText.Text = "Recommended band: N/A (need labeled segments)";
        SaveRecommendationButton.IsEnabled = false;
        UpdateSegmentActionButtons();
        RedrawWaveform();
        RedrawAnalysisChart();
        UpdateStatus("Status: Segment deleted");
    }

    private void OnSegmentListViewMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OnSeekSegmentClick(sender, new RoutedEventArgs());
    }

    private void OnSegmentListViewSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSegmentActionButtons();
    }

    private void OnRunAnalysisClick(object sender, RoutedEventArgs e)
    {
        if (_monoSamples.Length == 0 || _monoSampleRate <= 0)
        {
            UpdateStatus("Status: Load a WAV file before analysis");
            return;
        }

        var footstepSegments = _segments.Where(x => x.Label == "Footstep").ToList();
        var ambienceSegments = _segments.Where(x => x.Label == "Ambience").ToList();

        if (footstepSegments.Count == 0 || ambienceSegments.Count == 0)
        {
            AnalysisStatsText.Text = "Analysis: need at least 1 Footstep and 1 Ambience segment";
            RecommendedBandText.Text = "Recommended band: N/A (label coverage insufficient)";
            _latestAnalysis = null;
            SaveRecommendationButton.IsEnabled = false;
            RedrawAnalysisChart();
            UpdateStatus("Status: Add both Footstep and Ambience segments first");
            return;
        }

        var analysis = BuildAnalysis(footstepSegments, ambienceSegments);
        if (analysis is null)
        {
            AnalysisStatsText.Text = "Analysis: not enough spectral frames; add longer/more segments";
            RecommendedBandText.Text = "Recommended band: N/A (insufficient frame count)";
            _latestAnalysis = null;
            SaveRecommendationButton.IsEnabled = false;
            RedrawAnalysisChart();
            UpdateStatus("Status: Add more or longer segments and retry");
            return;
        }

        _latestAnalysis = analysis;
        var confidence = _latestAnalysis.Recommendation.Confidence;
        AnalysisStatsText.Text = string.Format(
            CultureInfo.InvariantCulture,
            "Analysis: Footstep={0} seg / {1} frames, Ambience={2} seg / {3} frames, bins={4}, confidence={5} ({6})",
            footstepSegments.Count,
            _latestAnalysis.FootstepFrameCount,
            ambienceSegments.Count,
            _latestAnalysis.AmbienceFrameCount,
            _latestAnalysis.Frequencies.Length,
            confidence.Text,
            confidence.Note);

        RecommendedBandText.Text = _latestAnalysis.RecommendedBandText;
        SaveRecommendationButton.IsEnabled = true;
        RedrawAnalysisChart();
        UpdateStatus("Status: Segment analysis completed");
    }

    private void OnSaveRecommendationClick(object sender, RoutedEventArgs e)
    {
        if (_latestAnalysis is null)
        {
            UpdateStatus("Status: Run analysis before saving recommendation");
            return;
        }

        var profileName = NormalizeProfileName(ProfileNameTextBox.Text);
        if (string.IsNullOrWhiteSpace(profileName))
        {
            UpdateStatus("Status: Enter a valid profile name");
            return;
        }

        try
        {
            Directory.CreateDirectory(RecommendationDirectory);

            var nowUtc = DateTimeOffset.UtcNow;
            var fileTimestamp = nowUtc.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var fileName = $"{profileName}_{fileTimestamp}.json";
            var fullPath = Path.Combine(RecommendationDirectory, fileName);

            var payload = RecommendationFileModel.FromRuntime(
                profileName,
                FilePathText.Text,
                _latestAnalysis,
                _segments);

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            File.WriteAllText(fullPath, json);

            _appSettings.AnalysisLastProfileName = profileName;
            _appSettings.AnalysisLastRecommendationDirectory = RecommendationDirectory;
            _appSettings.AnalysisLastRecommendationFilePath = fullPath;
            AppSettingsStore.Save(_appSettings);

            RefreshSavedRecommendations();
            UpdateStatus($"Status: Recommendation saved ({fileName})");
        }
        catch (Exception ex)
        {
            UpdateStatus($"Status: Failed to save recommendation ({ex.Message})");
        }
    }

    private void OnRefreshHistoryClick(object sender, RoutedEventArgs e)
    {
        RefreshSavedRecommendations();
        UpdateStatus("Status: Recommendation history refreshed");
    }

    private void OnLoadHistoryClick(object sender, RoutedEventArgs e)
    {
        LoadSelectedSavedRecommendation();
    }

    private void OnSavedRecommendationDoubleClick(object sender, MouseButtonEventArgs e)
    {
        LoadSelectedSavedRecommendation();
    }

    private void RefreshSavedRecommendations()
    {
        _savedRecommendations.Clear();

        if (!Directory.Exists(RecommendationDirectory))
        {
            return;
        }

        var files = Directory.GetFiles(RecommendationDirectory, "*.json", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        Array.Reverse(files);

        foreach (var filePath in files)
        {
            try
            {
                var text = File.ReadAllText(filePath);
                var model = JsonSerializer.Deserialize<RecommendationFileModel>(text);
                if (model is null)
                {
                    continue;
                }

                var item = new SavedRecommendationListItem(
                    filePath,
                    model.ProfileName,
                    model.CreatedUtc,
                    $"{model.HighPassHz}-{model.LowPassHz}Hz",
                    model.Confidence);
                _savedRecommendations.Add(item);
            }
            catch
            {
                // Ignore unreadable/legacy files and continue listing others.
            }
        }
    }

    private void LoadSelectedSavedRecommendation()
    {
        if (SavedRecommendationListView.SelectedItem is not SavedRecommendationListItem selected)
        {
            UpdateStatus("Status: Select a saved recommendation first");
            return;
        }

        try
        {
            var text = File.ReadAllText(selected.FilePath);
            var model = JsonSerializer.Deserialize<RecommendationFileModel>(text);
            if (model is null)
            {
                UpdateStatus("Status: Failed to parse selected recommendation file");
                return;
            }

            ProfileNameTextBox.Text = model.ProfileName;
            _latestAnalysis = null;
            SaveRecommendationButton.IsEnabled = false;

            _appSettings.AnalysisLastProfileName = NormalizeProfileName(model.ProfileName);
            _appSettings.AnalysisLastRecommendationDirectory = Path.GetDirectoryName(selected.FilePath) ?? RecommendationDirectory;
            _appSettings.AnalysisLastRecommendationFilePath = selected.FilePath;
            AppSettingsStore.Save(_appSettings);

            AnalysisStatsText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "Loaded saved recommendation: Footstep={0} seg / {1} frames, Ambience={2} seg / {3} frames",
                model.FootstepSegmentCount,
                model.FootstepFrameCount,
                model.AmbienceSegmentCount,
                model.AmbienceFrameCount);

            var sourceFileText = string.IsNullOrWhiteSpace(model.SourceAudioFile)
                ? string.Empty
                : $", source={Path.GetFileName(model.SourceAudioFile)}";

            RecommendedBandText.Text =
                $"Recommended band: {model.HighPassHz} Hz - {model.LowPassHz} Hz (confidence {model.Confidence}, saved {model.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}{sourceFileText})";

            RedrawAnalysisChart();
            UpdateStatus("Status: Loaded saved recommendation into summary");
        }
        catch (Exception ex)
        {
            UpdateStatus($"Status: Failed to load selected recommendation ({ex.Message})");
        }
    }

    private static string NormalizeProfileName(string? rawProfileName)
    {
        var fallback = "default";
        if (string.IsNullOrWhiteSpace(rawProfileName))
        {
            return fallback;
        }

        var trimmed = rawProfileName.Trim();
        var invalidChars = Path.GetInvalidFileNameChars();
        var buffer = new char[trimmed.Length];
        var idx = 0;
        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            if (invalidChars.Contains(ch))
            {
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                ch = '_';
            }

            buffer[idx++] = ch;
        }

        var normalized = idx == 0 ? fallback : new string(buffer, 0, idx);
        if (normalized.Length > 48)
        {
            normalized = normalized[..48];
        }

        return normalized;
    }

    private AnalysisResult? BuildAnalysis(IReadOnlyList<SegmentAnnotation> footstepSegments, IReadOnlyList<SegmentAnnotation> ambienceSegments)
    {
        const int fftSize = 1024;
        const int hopSize = 512;

        var footstepAggregation = ComputeAggregatedPsd(footstepSegments, fftSize, hopSize);
        var ambienceAggregation = ComputeAggregatedPsd(ambienceSegments, fftSize, hopSize);
        if (footstepAggregation.FrameCount <= 0 || ambienceAggregation.FrameCount <= 0)
        {
            return null;
        }

        var binCount = fftSize / 2 + 1;

        var freqs = new double[binCount];
        var db = new double[binCount];
        for (var i = 0; i < binCount; i++)
        {
            freqs[i] = i * _monoSampleRate / (double)fftSize;
            var foot = footstepAggregation.Psd[i] + 1e-12;
            var amb = ambienceAggregation.Psd[i] + 1e-12;
            db[i] = 10.0 * Math.Log10(foot / amb);
        }

        var recommendation = RecommendBand(
            freqs,
            db,
            footstepSegments.Count,
            ambienceSegments.Count,
            footstepAggregation.FrameCount,
            ambienceAggregation.FrameCount);

        return new AnalysisResult(
            freqs,
            db,
            recommendation,
            footstepAggregation.FrameCount,
            ambienceAggregation.FrameCount,
            footstepSegments.Count,
            ambienceSegments.Count);
    }

    private PsdAggregation ComputeAggregatedPsd(IReadOnlyList<SegmentAnnotation> segments, int fftSize, int hopSize)
    {
        var binCount = fftSize / 2 + 1;
        var aggregate = new double[binCount];
        var frameCount = 0;
        var window = BuildHannWindow(fftSize);

        for (var segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
        {
            var segment = segments[segmentIndex];
            var startSample = (int)Math.Max(0, Math.Floor(segment.StartSeconds * _monoSampleRate));
            var endSample = (int)Math.Min(_monoSamples.Length, Math.Ceiling(segment.EndSeconds * _monoSampleRate));
            if (endSample - startSample < fftSize)
            {
                continue;
            }

            for (var offset = startSample; offset + fftSize <= endSample; offset += hopSize)
            {
                var real = new double[fftSize];
                var imag = new double[fftSize];
                for (var i = 0; i < fftSize; i++)
                {
                    real[i] = _monoSamples[offset + i] * window[i];
                }

                FastFourierTransform(real, imag);

                for (var bin = 0; bin < binCount; bin++)
                {
                    var mag2 = (real[bin] * real[bin]) + (imag[bin] * imag[bin]);
                    aggregate[bin] += mag2;
                }

                frameCount++;
            }
        }

        if (frameCount == 0)
        {
            return new PsdAggregation(aggregate, 0);
        }

        for (var i = 0; i < aggregate.Length; i++)
        {
            aggregate[i] /= frameCount;
        }

        return new PsdAggregation(aggregate, frameCount);
    }

    private static double[] BuildHannWindow(int size)
    {
        var window = new double[size];
        for (var i = 0; i < size; i++)
        {
            window[i] = 0.5 - (0.5 * Math.Cos((2.0 * Math.PI * i) / (size - 1)));
        }

        return window;
    }

    private static void FastFourierTransform(double[] real, double[] imag)
    {
        var n = real.Length;
        var j = 0;
        for (var i = 1; i < n; i++)
        {
            var bit = n >> 1;
            while ((j & bit) != 0)
            {
                j ^= bit;
                bit >>= 1;
            }

            j |= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = -2.0 * Math.PI / len;
            var wLenReal = Math.Cos(angle);
            var wLenImag = Math.Sin(angle);

            for (var i = 0; i < n; i += len)
            {
                var wReal = 1.0;
                var wImag = 0.0;
                for (var k = 0; k < len / 2; k++)
                {
                    var evenIndex = i + k;
                    var oddIndex = i + k + (len / 2);

                    var oddReal = (real[oddIndex] * wReal) - (imag[oddIndex] * wImag);
                    var oddImag = (real[oddIndex] * wImag) + (imag[oddIndex] * wReal);

                    real[oddIndex] = real[evenIndex] - oddReal;
                    imag[oddIndex] = imag[evenIndex] - oddImag;
                    real[evenIndex] += oddReal;
                    imag[evenIndex] += oddImag;

                    var nextWReal = (wReal * wLenReal) - (wImag * wLenImag);
                    wImag = (wReal * wLenImag) + (wImag * wLenReal);
                    wReal = nextWReal;
                }
            }
        }
    }

    private static BandRecommendation RecommendBand(
        double[] frequencies,
        double[] ratioDb,
        int footstepSegmentCount,
        int ambienceSegmentCount,
        int footstepFrameCount,
        int ambienceFrameCount)
    {
        const double minFrequency = 50;
        const double maxFrequency = 5000;
        const double thresholdDb = 1.2;
        const int smoothingRadius = 2;

        var smoothed = Smooth(ratioDb, smoothingRadius);

        var bestStart = -1;
        var bestEnd = -1;
        var currentStart = -1;
        var bestScore = double.MinValue;
        var currentScore = 0.0;

        for (var i = 0; i < frequencies.Length; i++)
        {
            var freq = frequencies[i];
            if (freq < minFrequency || freq > maxFrequency)
            {
                continue;
            }

            if (smoothed[i] >= thresholdDb)
            {
                if (currentStart < 0)
                {
                    currentStart = i;
                    currentScore = 0;
                }

                currentScore += smoothed[i];
            }
            else if (currentStart >= 0)
            {
                var currentEnd = i - 1;
                var length = Math.Max(1, currentEnd - currentStart + 1);
                var score = currentScore + (length * 0.6);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestStart = currentStart;
                    bestEnd = currentEnd;
                }

                currentStart = -1;
                currentScore = 0;
            }
        }

        if (currentStart >= 0)
        {
            var currentEnd = frequencies.Length - 1;
            var length = Math.Max(1, currentEnd - currentStart + 1);
            var score = currentScore + (length * 0.6);
            if (score > bestScore)
            {
                bestStart = currentStart;
                bestEnd = currentEnd;
            }
        }

        if (bestStart < 0 || bestEnd <= bestStart)
        {
            var confidenceFallback = ComputeConfidence(
                0,
                footstepSegmentCount,
                ambienceSegmentCount,
                footstepFrameCount,
                ambienceFrameCount,
                strongContrast: false);

            return new BandRecommendation(
                120,
                3500,
                $"Recommended band: fallback to default 120-3500 Hz (insufficient contrast, confidence {confidenceFallback.Text})",
                confidenceFallback,
                true);
        }

        var highPass = (int)Math.Round(frequencies[bestStart] / 10.0) * 10;
        var lowPass = (int)Math.Round(frequencies[bestEnd] / 10.0) * 10;
        highPass = Math.Clamp(highPass, 40, 4000);
        lowPass = Math.Clamp(lowPass, highPass + 100, 7000);

        var peakContrast = 0.0;
        for (var i = bestStart; i <= bestEnd; i++)
        {
            peakContrast = Math.Max(peakContrast, smoothed[i]);
        }

        var confidence = ComputeConfidence(
            peakContrast,
            footstepSegmentCount,
            ambienceSegmentCount,
            footstepFrameCount,
            ambienceFrameCount,
            strongContrast: peakContrast >= 2.0);

        var text = $"Recommended band: {highPass} Hz - {lowPass} Hz (confidence {confidence.Text})";
        return new BandRecommendation(highPass, lowPass, text, confidence, false);
    }

    private static double[] Smooth(double[] values, int radius)
    {
        if (radius <= 0)
        {
            return values.ToArray();
        }

        var smoothed = new double[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var start = Math.Max(0, i - radius);
            var end = Math.Min(values.Length - 1, i + radius);
            double sum = 0;
            for (var j = start; j <= end; j++)
            {
                sum += values[j];
            }

            smoothed[i] = sum / (end - start + 1);
        }

        return smoothed;
    }

    private static RecommendationConfidence ComputeConfidence(
        double peakContrastDb,
        int footstepSegments,
        int ambienceSegments,
        int footstepFrames,
        int ambienceFrames,
        bool strongContrast)
    {
        var score = 0;

        if (footstepSegments >= MinSegmentsPerLabelForStrongRecommendation)
        {
            score += 1;
        }

        if (ambienceSegments >= MinSegmentsPerLabelForStrongRecommendation)
        {
            score += 1;
        }

        if (footstepFrames >= MinFramesPerLabelForStrongRecommendation)
        {
            score += 1;
        }

        if (ambienceFrames >= MinFramesPerLabelForStrongRecommendation)
        {
            score += 1;
        }

        if (strongContrast || peakContrastDb >= 2.5)
        {
            score += 1;
        }
        else if (peakContrastDb >= 1.5)
        {
            score += 0;
        }
        else
        {
            score -= 1;
        }

        return score switch
        {
            >= 4 => new RecommendationConfidence("High", "High confidence: sample coverage and contrast are strong"),
            >= 2 => new RecommendationConfidence("Medium", "Medium confidence: add more labeled segments to stabilize"),
            _ => new RecommendationConfidence("Low", "Low confidence: add more labeled segments and re-run analysis")
        };
    }

    private void OnAnalysisCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RedrawAnalysisChart();
    }

    private void RedrawAnalysisChart()
    {
        AnalysisCanvas.Children.Clear();

        var width = AnalysisCanvas.ActualWidth;
        var height = AnalysisCanvas.ActualHeight;
        if (width < 20 || height < 20)
        {
            return;
        }

        var background = new Rectangle
        {
            Width = width,
            Height = height,
            Fill = new SolidColorBrush(Color.FromRgb(11, 18, 32)),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(background, 0);
        Canvas.SetTop(background, 0);
        AnalysisCanvas.Children.Add(background);

        if (_latestAnalysis is null || _latestAnalysis.Frequencies.Length == 0)
        {
            var emptyText = new TextBlock
            {
                Text = "Run analysis to view Footstep/Ambience contrast curve",
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139))
            };
            Canvas.SetLeft(emptyText, 10);
            Canvas.SetTop(emptyText, 10);
            AnalysisCanvas.Children.Add(emptyText);
            return;
        }

        var leftPad = 30.0;
        var rightPad = 8.0;
        var topPad = 8.0;
        var bottomPad = 18.0;
        var plotWidth = Math.Max(10, width - leftPad - rightPad);
        var plotHeight = Math.Max(10, height - topPad - bottomPad);

        var minDb = Math.Min(-8.0, _latestAnalysis.RatioDb.Min());
        var maxDb = Math.Max(8.0, _latestAnalysis.RatioDb.Max());
        if (maxDb - minDb < 1e-6)
        {
            maxDb = minDb + 1;
        }

        var zeroY = topPad + ((maxDb - 0) / (maxDb - minDb)) * plotHeight;
        var zeroLine = new Line
        {
            X1 = leftPad,
            X2 = leftPad + plotWidth,
            Y1 = zeroY,
            Y2 = zeroY,
            Stroke = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
            StrokeThickness = 1
        };
        AnalysisCanvas.Children.Add(zeroLine);

        var minFrequency = 50.0;
        var maxFrequency = 5000.0;
        var points = new PointCollection();
        for (var i = 0; i < _latestAnalysis.Frequencies.Length; i++)
        {
            var freq = _latestAnalysis.Frequencies[i];
            if (freq < minFrequency || freq > maxFrequency)
            {
                continue;
            }

            var xRatio = (freq - minFrequency) / (maxFrequency - minFrequency);
            var yRatio = (_latestAnalysis.RatioDb[i] - minDb) / (maxDb - minDb);
            var x = leftPad + (xRatio * plotWidth);
            var y = topPad + ((1.0 - yRatio) * plotHeight);
            points.Add(new Point(x, y));
        }

        var bandLeftRatio = (_latestAnalysis.Recommendation.HighPassHz - minFrequency) / (maxFrequency - minFrequency);
        var bandRightRatio = (_latestAnalysis.Recommendation.LowPassHz - minFrequency) / (maxFrequency - minFrequency);
        if (bandRightRatio > 0 && bandLeftRatio < 1)
        {
            var bandLeft = leftPad + (Math.Clamp(bandLeftRatio, 0, 1) * plotWidth);
            var bandRight = leftPad + (Math.Clamp(bandRightRatio, 0, 1) * plotWidth);
            if (bandRight > bandLeft)
            {
                var bandRect = new Rectangle
                {
                    Width = bandRight - bandLeft,
                    Height = plotHeight,
                    Fill = new SolidColorBrush(Color.FromArgb(35, 147, 197, 253)),
                    Stroke = new SolidColorBrush(Color.FromArgb(110, 147, 197, 253)),
                    StrokeThickness = 1,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(bandRect, bandLeft);
                Canvas.SetTop(bandRect, topPad);
                AnalysisCanvas.Children.Add(bandRect);
            }
        }

        if (points.Count >= 2)
        {
            var curve = new Polyline
            {
                Points = points,
                Stroke = new SolidColorBrush(Color.FromRgb(248, 113, 113)),
                StrokeThickness = 1.4,
                SnapsToDevicePixels = true
            };
            AnalysisCanvas.Children.Add(curve);
        }

        var yLabel = new TextBlock
        {
            Text = "dB",
            Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
            FontSize = 10
        };
        Canvas.SetLeft(yLabel, 4);
        Canvas.SetTop(yLabel, 2);
        AnalysisCanvas.Children.Add(yLabel);

        var xLabel = new TextBlock
        {
            Text = "50Hz to 5000Hz",
            Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
            FontSize = 10
        };
        Canvas.SetLeft(xLabel, width - 88);
        Canvas.SetTop(xLabel, height - 15);
        AnalysisCanvas.Children.Add(xLabel);
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

        DrawSegmentOverlays(width, height);
        DrawSelectionOverlay(width, height);

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

    private void DrawSegmentOverlays(double width, double height)
    {
        if (_audioFileReader is null || TimelineSlider.Maximum <= 0)
        {
            return;
        }

        foreach (var segment in _segments)
        {
            var left = SecondsToCanvasX(segment.StartSeconds, width);
            var right = SecondsToCanvasX(segment.EndSeconds, width);
            if (right <= left)
            {
                continue;
            }

            var color = segment.Label switch
            {
                "Footstep" => Color.FromArgb(70, 59, 130, 246),
                "Ambience" => Color.FromArgb(70, 16, 185, 129),
                _ => Color.FromArgb(70, 245, 158, 11)
            };

            var rect = new Rectangle
            {
                Width = right - left,
                Height = height,
                Fill = new SolidColorBrush(color),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(rect, left);
            Canvas.SetTop(rect, 0);
            WaveformCanvas.Children.Add(rect);
        }
    }

    private void DrawSelectionOverlay(double width, double height)
    {
        if (!TryGetNormalizedSelection(out var startSeconds, out var endSeconds) || endSeconds - startSeconds < 1e-6)
        {
            return;
        }

        var left = SecondsToCanvasX(startSeconds, width);
        var right = SecondsToCanvasX(endSeconds, width);
        var rect = new Rectangle
        {
            Width = Math.Max(1, right - left),
            Height = height,
            Fill = new SolidColorBrush(Color.FromArgb(58, 248, 250, 252)),
            Stroke = new SolidColorBrush(Color.FromRgb(186, 230, 253)),
            StrokeThickness = 1,
            IsHitTestVisible = false
        };

        Canvas.SetLeft(rect, left);
        Canvas.SetTop(rect, 0);
        WaveformCanvas.Children.Add(rect);
    }

    private bool TryGetNormalizedSelection(out double startSeconds, out double endSeconds)
    {
        startSeconds = 0;
        endSeconds = 0;

        if (!_selectionStartSeconds.HasValue || !_selectionEndSeconds.HasValue)
        {
            return false;
        }

        startSeconds = Math.Min(_selectionStartSeconds.Value, _selectionEndSeconds.Value);
        endSeconds = Math.Max(_selectionStartSeconds.Value, _selectionEndSeconds.Value);
        return true;
    }

    private void UpdateSelectionUi()
    {
        if (!TryGetNormalizedSelection(out var startSeconds, out var endSeconds))
        {
            SelectionRangeText.Text = "Selection: None";
            AddFootstepButton.IsEnabled = false;
            AddAmbienceButton.IsEnabled = false;
            AddMixedButton.IsEnabled = false;
            return;
        }

        var duration = endSeconds - startSeconds;
        SelectionRangeText.Text = string.Format(
            CultureInfo.InvariantCulture,
            "Selection: {0} - {1} ({2:0.000}s)",
            FormatTime(TimeSpan.FromSeconds(startSeconds)),
            FormatTime(TimeSpan.FromSeconds(endSeconds)),
            duration);

        var hasFile = _audioFileReader is not null;
        var canAdd = hasFile && duration >= MinSegmentSeconds;
        AddFootstepButton.IsEnabled = canAdd;
        AddAmbienceButton.IsEnabled = canAdd;
        AddMixedButton.IsEnabled = canAdd;
    }

    private void UpdateSegmentActionButtons()
    {
        var hasSelection = SegmentListView.SelectedItem is SegmentAnnotation;
        SeekSegmentButton.IsEnabled = hasSelection && _audioFileReader is not null;
        DeleteSegmentButton.IsEnabled = hasSelection;
    }

    private void ClearSelection()
    {
        _selectionStartSeconds = null;
        _selectionEndSeconds = null;
        UpdateSelectionUi();
    }

    private double CanvasXToSeconds(double x)
    {
        var width = Math.Max(1, WaveformCanvas.ActualWidth);
        var ratio = Math.Clamp(x / width, 0, 1);
        return ratio * TimelineSlider.Maximum;
    }

    private double SecondsToCanvasX(double seconds, double width)
    {
        if (TimelineSlider.Maximum <= 0)
        {
            return 0;
        }

        var ratio = Math.Clamp(seconds / TimelineSlider.Maximum, 0, 1);
        return ratio * width;
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
        PlayPauseButton.Content = "Play";
        PlayPauseButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        TimelineSlider.IsEnabled = false;
        TimelineSlider.Minimum = 0;
        TimelineSlider.Maximum = 1;
        TimelineSlider.Value = 0;
        CurrentTimeText.Text = "00:00";
        DurationText.Text = "00:00";
        _monoSamples = Array.Empty<float>();
        _monoSampleRate = 0;
        _latestAnalysis = null;
        AnalysisStatsText.Text = "Analysis: waiting for labeled segments";
        RecommendedBandText.Text = "Recommended band: N/A (need labeled segments)";
        RedrawWaveform();
        RedrawAnalysisChart();
        UpdateSelectionUi();
        UpdateSegmentActionButtons();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        _appSettings.AnalysisLastProfileName = NormalizeProfileName(ProfileNameTextBox.Text);
        if (string.IsNullOrWhiteSpace(_appSettings.AnalysisLastRecommendationDirectory))
        {
            _appSettings.AnalysisLastRecommendationDirectory = RecommendationDirectory;
        }

        AppSettingsStore.Save(_appSettings);
        Close();
    }

    private sealed class SegmentAnnotation
    {
        public SegmentAnnotation(string label, double startSeconds, double endSeconds)
        {
            Label = label;
            StartSeconds = startSeconds;
            EndSeconds = endSeconds;
            StartText = FormatTime(TimeSpan.FromSeconds(startSeconds));
            EndText = FormatTime(TimeSpan.FromSeconds(endSeconds));
            DurationText = string.Format(CultureInfo.InvariantCulture, "{0:0.000}s", endSeconds - startSeconds);
        }

        public string Label { get; }

        public double StartSeconds { get; }

        public double EndSeconds { get; }

        public string StartText { get; }

        public string EndText { get; }

        public string DurationText { get; }
    }

    private sealed class AnalysisResult
    {
        public AnalysisResult(
            double[] frequencies,
            double[] ratioDb,
            BandRecommendation recommendation,
            int footstepFrameCount,
            int ambienceFrameCount,
            int footstepSegmentCount,
            int ambienceSegmentCount)
        {
            Frequencies = frequencies;
            RatioDb = ratioDb;
            Recommendation = recommendation;
            RecommendedBandText = recommendation.Text;
            FootstepFrameCount = footstepFrameCount;
            AmbienceFrameCount = ambienceFrameCount;
            FootstepSegmentCount = footstepSegmentCount;
            AmbienceSegmentCount = ambienceSegmentCount;
        }

        public double[] Frequencies { get; }

        public double[] RatioDb { get; }

        public BandRecommendation Recommendation { get; }

        public string RecommendedBandText { get; }

        public int FootstepFrameCount { get; }

        public int AmbienceFrameCount { get; }

        public int FootstepSegmentCount { get; }

        public int AmbienceSegmentCount { get; }
    }

    private sealed class BandRecommendation
    {
        public BandRecommendation(int highPassHz, int lowPassHz, string text, RecommendationConfidence confidence, bool usedFallback)
        {
            HighPassHz = highPassHz;
            LowPassHz = lowPassHz;
            Text = text;
            Confidence = confidence;
            UsedFallback = usedFallback;
        }

        public int HighPassHz { get; }

        public int LowPassHz { get; }

        public string Text { get; }

        public RecommendationConfidence Confidence { get; }

        public bool UsedFallback { get; }
    }

    private sealed class PsdAggregation
    {
        public PsdAggregation(double[] psd, int frameCount)
        {
            Psd = psd;
            FrameCount = frameCount;
        }

        public double[] Psd { get; }

        public int FrameCount { get; }
    }

    private sealed class RecommendationConfidence
    {
        public RecommendationConfidence(string text, string note)
        {
            Text = text;
            Note = note;
        }

        public string Text { get; }

        public string Note { get; }
    }

    private sealed class RecommendationFileModel
    {
        public string ProfileName { get; set; } = "default";

        public DateTimeOffset CreatedUtc { get; set; }

        public string SourceAudioFile { get; set; } = string.Empty;

        public int HighPassHz { get; set; }

        public int LowPassHz { get; set; }

        public string Confidence { get; set; } = "Unknown";

        public string ConfidenceNote { get; set; } = string.Empty;

        public bool UsedFallback { get; set; }

        public int FootstepSegmentCount { get; set; }

        public int AmbienceSegmentCount { get; set; }

        public int MixedSegmentCount { get; set; }

        public int FootstepFrameCount { get; set; }

        public int AmbienceFrameCount { get; set; }

        public string[] SegmentRanges { get; set; } = Array.Empty<string>();

        public static RecommendationFileModel FromRuntime(
            string profileName,
            string sourceAudioFile,
            AnalysisResult analysis,
            IReadOnlyCollection<SegmentAnnotation> segments)
        {
            var segmentRanges = segments
                .Select(x => $"{x.Label}:{x.StartText}-{x.EndText}({x.DurationText})")
                .ToArray();

            return new RecommendationFileModel
            {
                ProfileName = profileName,
                CreatedUtc = DateTimeOffset.UtcNow,
                SourceAudioFile = sourceAudioFile,
                HighPassHz = analysis.Recommendation.HighPassHz,
                LowPassHz = analysis.Recommendation.LowPassHz,
                Confidence = analysis.Recommendation.Confidence.Text,
                ConfidenceNote = analysis.Recommendation.Confidence.Note,
                UsedFallback = analysis.Recommendation.UsedFallback,
                FootstepSegmentCount = analysis.FootstepSegmentCount,
                AmbienceSegmentCount = analysis.AmbienceSegmentCount,
                MixedSegmentCount = segments.Count(x => x.Label == "Mixed"),
                FootstepFrameCount = analysis.FootstepFrameCount,
                AmbienceFrameCount = analysis.AmbienceFrameCount,
                SegmentRanges = segmentRanges
            };
        }
    }

    private sealed class SavedRecommendationListItem
    {
        public SavedRecommendationListItem(string filePath, string profileName, DateTimeOffset createdUtc, string bandText, string confidenceText)
        {
            FilePath = filePath;
            ProfileName = profileName;
            CreatedUtc = createdUtc;
            CreatedLocalText = createdUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            BandText = bandText;
            ConfidenceText = confidenceText;
        }

        public string FilePath { get; }

        public string ProfileName { get; }

        public DateTimeOffset CreatedUtc { get; }

        public string CreatedLocalText { get; }

        public string BandText { get; }

        public string ConfidenceText { get; }
    }
}
