using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SoundSourceVisualizer;

public sealed class AudioMonitor : IDisposable
{
    private readonly object _syncRoot = new();
    private WasapiLoopbackCapture? _capture;
    private bool _started;

    public event Action<float, float>? LevelCalculated;

    public void Start()
    {
        lock (_syncRoot)
        {
            if (_started)
            {
                return;
            }

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

        double leftSum = 0;
        double rightSum = 0;

        for (var frame = 0; frame < frameCount; frame++)
        {
            var frameOffset = frame * blockAlign;
            var left = ReadSample(format.Encoding, e.Buffer, frameOffset, bytesPerSample);
            var right = ReadSample(format.Encoding, e.Buffer, frameOffset + bytesPerSample, bytesPerSample);

            leftSum += left * left;
            rightSum += right * right;
        }

        var leftRms = (float)Math.Sqrt(leftSum / frameCount);
        var rightRms = (float)Math.Sqrt(rightSum / frameCount);
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
