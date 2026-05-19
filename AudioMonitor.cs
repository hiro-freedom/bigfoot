using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace bigfoot;

public sealed class AudioMonitor : IDisposable
{
    private readonly object _syncRoot = new();
    private WasapiLoopbackCapture? _capture;
    private bool _started;

    public bool ExcludeMyselfEnabled { get; set; }
    public float SideActivationThreshold { get; set; } = 0.0125f;

    public event Action<float, float>? LevelCalculated;

    public void Start()
    {
        lock (_syncRoot)
        {
            if (_started)
            {
                return;
            }

            // Capture default render device output (what the user hears).
            _capture = new WasapiLoopbackCapture();
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();
            _started = true;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var capture = _capture;
        if (capture is null)
        {
            return;
        }

        var format = capture.WaveFormat;
        var channels = format.Channels;
        if (channels < 2)
        {
            return;
        }

        var bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample <= 0)
        {
            return;
        }

        var blockAlign = format.BlockAlign;
        if (blockAlign <= 0)
        {
            return;
        }

        var frameCount = e.BytesRecorded / blockAlign;
        if (frameCount <= 0)
        {
            return;
        }

        // Short-window RMS energy per channel for stable loudness estimation.
        double leftSum = 0;
        double rightSum = 0;
        double sideSum = 0;

        for (var frame = 0; frame < frameCount; frame++)
        {
            var frameOffset = frame * blockAlign;
            var left = ReadSample(format.Encoding, e.Buffer, frameOffset, bytesPerSample);
            var right = ReadSample(format.Encoding, e.Buffer, frameOffset + bytesPerSample, bytesPerSample);

            // Mid-Side processing:
            // Mid  = (L + R) / 2 (center-panned audio)
            // Side = (L - R) / 2 (directional difference between channels)
            // When only center audio is present, Side energy is close to zero.
            var side = (left - right) * 0.5f;

            leftSum += left * left;
            rightSum += right * right;
            sideSum += side * side;
        }

        var leftRms = (float)Math.Sqrt(leftSum / frameCount);
        var rightRms = (float)Math.Sqrt(rightSum / frameCount);

        if (ExcludeMyselfEnabled)
        {
            var sideRms = (float)Math.Sqrt(sideSum / frameCount);
            if (sideRms < SideActivationThreshold)
            {
                LevelCalculated?.Invoke(0f, 0f);
                return;
            }
        }

        LevelCalculated?.Invoke(leftRms, rightRms);
    }

    private static float ReadSample(WaveFormatEncoding encoding, byte[] buffer, int offset, int bytesPerSample)
    {
        return encoding switch
        {
            WaveFormatEncoding.IeeeFloat when bytesPerSample == 4 => BitConverter.ToSingle(buffer, offset),
            WaveFormatEncoding.Pcm when bytesPerSample == 2 => BitConverter.ToInt16(buffer, offset) / 32768f,
            WaveFormatEncoding.Pcm when bytesPerSample == 3 => Read24BitSample(buffer, offset),
            WaveFormatEncoding.Pcm when bytesPerSample == 4 => BitConverter.ToInt32(buffer, offset) / 2147483648f,
            _ => 0f
        };
    }

    private static float Read24BitSample(byte[] buffer, int offset)
    {
        // Manual sign extension for packed 24-bit PCM.
        var sample = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
        if ((sample & 0x800000) != 0)
        {
            sample |= unchecked((int)0xFF000000);
        }

        return sample / 8388608f;
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            LevelCalculated?.Invoke(0f, 0f);
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_capture is not null)
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;

                try
                {
                    _capture.StopRecording();
                }
                catch
                {
                    // No-op: capture might already be stopped.
                }

                _capture.Dispose();
                _capture = null;
            }

            _started = false;
        }
    }
}
