using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace bigfoot;

public sealed class AudioMonitor : IDisposable
{
    private readonly object _syncRoot = new();
    private WasapiLoopbackCapture? _capture;
    private bool _started;

    private readonly BiquadHighPassFilter _highPassLeft = new();
    private readonly BiquadHighPassFilter _highPassRight = new();
    private readonly BiquadLowPassFilter _lowPassLeft = new();
    private readonly BiquadLowPassFilter _lowPassRight = new();

    private int _analysisWindowFrames;
    private float _leftEnvelope;
    private float _rightEnvelope;
    private float _excludeGateEnvelope;

    public bool ExcludeMyselfEnabled { get; set; }
    public bool UseFrequencyWeighting { get; set; } = true;
    public float SideActivationThreshold { get; set; } = 0.05f;

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

            ConfigureProcessing(_capture.WaveFormat.SampleRate);

            _capture.StartRecording();
            _started = true;
        }
    }

    private void ConfigureProcessing(int sampleRate)
    {
        // FEATURE 3: analyze a short trailing window to catch transient footsteps faster.
        _analysisWindowFrames = Math.Max(64, (int)(sampleRate * 0.016)); // ~16ms

        // FEATURE 4: band weighting for footsteps.
        // Tuned to 90Hz - 1200Hz to combine low-frequency footstep thumps with strong mid-high gunshot suppression.
        _highPassLeft.Configure(sampleRate, 90f);
        _highPassRight.Configure(sampleRate, 90f);
        _lowPassLeft.Configure(sampleRate, 1200f);
        _lowPassRight.Configure(sampleRate, 1200f);
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

        var startFrame = Math.Max(0, frameCount - _analysisWindowFrames);
        var analyzedFrames = frameCount - startFrame;
        if (analyzedFrames <= 0)
        {
            return;
        }

        double leftSum = 0;
        double rightSum = 0;
        double midSum = 0;
        double sideSum = 0;
        float leftPeak = 0;
        float rightPeak = 0;
        float midPeak = 0;
        float sidePeak = 0;

        for (var frame = startFrame; frame < frameCount; frame++)
        {
            var frameOffset = frame * blockAlign;
            var left = ReadSample(format.Encoding, e.Buffer, frameOffset, bytesPerSample);
            var right = ReadSample(format.Encoding, e.Buffer, frameOffset + bytesPerSample, bytesPerSample);

            // FEATURE 4: frequency weighting block.
            // Comment out this entire block if you want full-band raw detection.
            if (UseFrequencyWeighting)
            {
                left = _lowPassLeft.Process(_highPassLeft.Process(left));
                right = _lowPassRight.Process(_highPassRight.Process(right));
            }

            // Mid-Side processing:
            // Mid  = (L + R) / 2 (center-panned audio)
            // Side = (L - R) / 2 (directional difference between channels)
            // When only center audio is present, Side energy is close to zero.
            var mid = (left + right) * 0.5f;
            var side = (left - right) * 0.5f;

            leftPeak = Math.Max(leftPeak, Math.Abs(left));
            rightPeak = Math.Max(rightPeak, Math.Abs(right));
            midPeak = Math.Max(midPeak, Math.Abs(mid));
            sidePeak = Math.Max(sidePeak, Math.Abs(side));

            leftSum += left * left;
            rightSum += right * right;
            midSum += mid * mid;
            sideSum += side * side;
        }

        var leftRms = (float)Math.Sqrt(leftSum / analyzedFrames);
        var rightRms = (float)Math.Sqrt(rightSum / analyzedFrames);
        var midRms = (float)Math.Sqrt(midSum / analyzedFrames);
        var sideRms = (float)Math.Sqrt(sideSum / analyzedFrames);

        // FEATURE 2: RMS + Peak blend.
        // Comment out this block if you want pure RMS metrics.
        var leftMetric = (leftRms * 0.70f) + (leftPeak * 0.30f);
        var rightMetric = (rightRms * 0.70f) + (rightPeak * 0.30f);
        var midMetric = (midRms * 0.70f) + (midPeak * 0.30f);
        var sideMetric = (sideRms * 0.70f) + (sidePeak * 0.30f);

        if (ExcludeMyselfEnabled)
        {
            // FEATURE 1: hybrid soft gate.
            // Relative directionality blocks center-panned "myself" sounds.
            // Absolute side energy prevents tiny fluctuations from opening the gate.
            // A short gate envelope reduces flicker/chopping on quiet footsteps.
            var relativeDirectionality = sideMetric / (midMetric + 1e-6f);

            // Increased threshold from 0.0125f to 0.05f to tightly reject center sounds 
            // that have minor stereo phase/level imbalances (due to HRTF or 3D audio).
            var relativeGate = SmoothStep(
                SideActivationThreshold * 0.50f,
                SideActivationThreshold * 1.35f,
                relativeDirectionality);

            const float sideFloorOpen = 0.0030f;
            const float sideFloorFull = 0.0090f;
            var absoluteGate = SmoothStep(sideFloorOpen, sideFloorFull, sideMetric);

            // Reduced safety leak factor from 0.35f to 0.08f to heavily suppress 
            // loud centered sounds (like own gunfire) which bleed through the absolute floor.
            var targetGate = Math.Max(relativeGate, absoluteGate * 0.08f);
            _excludeGateEnvelope = ApplyEnvelope(targetGate, ref _excludeGateEnvelope, attack: 0.55f, release: 0.12f);

            if (_excludeGateEnvelope <= 0.02f)
            {
                LevelCalculated?.Invoke(0f, 0f);
                return;
            }

            leftMetric *= _excludeGateEnvelope;
            rightMetric *= _excludeGateEnvelope;
        }
        else
        {
            _excludeGateEnvelope = 1f;
        }

        // FEATURE 3: attack/release envelope to react quickly but decay smoothly.
        // Comment out this block to output unsmoothed metrics.
        var leftSmoothed = ApplyEnvelope(leftMetric, ref _leftEnvelope, attack: 0.55f, release: 0.20f);
        var rightSmoothed = ApplyEnvelope(rightMetric, ref _rightEnvelope, attack: 0.55f, release: 0.20f);

        LevelCalculated?.Invoke(leftSmoothed, rightSmoothed);
    }

    private static float ApplyEnvelope(float input, ref float state, float attack, float release)
    {
        var coeff = input >= state ? attack : release;
        state += (input - state) * coeff;
        return state;
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        if (edge1 <= edge0)
        {
            return x >= edge1 ? 1f : 0f;
        }

        var t = Clamp01((x - edge0) / (edge1 - edge0));
        return t * t * (3f - (2f * t));
    }

    private static float Clamp01(float value)
    {
        return Math.Clamp(value, 0f, 1f);
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

    private sealed class BiquadLowPassFilter
    {
        private float _b0, _b1, _b2, _a1, _a2;
        private float _x1, _x2, _y1, _y2;

        public void Configure(int sampleRate, float cutoffHz, float q = 0.707f)
        {
            double w0 = 2.0 * Math.PI * cutoffHz / sampleRate;
            double alpha = Math.Sin(w0) / (2.0 * q);
            double cosW0 = Math.Cos(w0);

            double a0 = 1.0 + alpha;
            _b0 = (float)((1.0 - cosW0) / 2.0 / a0);
            _b1 = (float)((1.0 - cosW0) / a0);
            _b2 = (float)((1.0 - cosW0) / 2.0 / a0);
            _a1 = (float)(-2.0 * cosW0 / a0);
            _a2 = (float)((1.0 - alpha) / a0);
        }

        public float Process(float input)
        {
            float output = _b0 * input + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1;
            _x1 = input;
            _y2 = _y1;
            _y1 = output;
            return output;
        }
    }

    private sealed class BiquadHighPassFilter
    {
        private float _b0, _b1, _b2, _a1, _a2;
        private float _x1, _x2, _y1, _y2;

        public void Configure(int sampleRate, float cutoffHz, float q = 0.707f)
        {
            double w0 = 2.0 * Math.PI * cutoffHz / sampleRate;
            double alpha = Math.Sin(w0) / (2.0 * q);
            double cosW0 = Math.Cos(w0);

            double a0 = 1.0 + alpha;
            _b0 = (float)((1.0 + cosW0) / 2.0 / a0);
            _b1 = (float)(-(1.0 + cosW0) / a0);
            _b2 = (float)((1.0 + cosW0) / 2.0 / a0);
            _a1 = (float)(-2.0 * cosW0 / a0);
            _a2 = (float)((1.0 - alpha) / a0);
        }

        public float Process(float input)
        {
            float output = _b0 * input + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1;
            _x1 = input;
            _y2 = _y1;
            _y1 = output;
            return output;
        }
    }
}
