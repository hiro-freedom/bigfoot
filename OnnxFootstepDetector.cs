using System;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace bigfoot;

/// <summary>
/// Thread-safe ONNX Runtime service for footstep detection with DirectML GPU acceleration fallback.
/// </summary>
public sealed class OnnxFootstepDetector : IDisposable
{
    private readonly object _lock = new();
    private InferenceSession? _session;
    private bool _isDisposed;

    public const int TargetSampleRate = 32000;
    public const int WindowSamples = 12800; // 400ms at 32kHz
    public bool IsModelLoaded => _session is not null;
    public bool IsGpuAccelerated { get; private set; }

    public OnnxFootstepDetector(string? modelPath = null)
    {
        modelPath ??= Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models", "footstep_detector.onnx");
        InitSession(modelPath);
    }

    private void InitSession(string modelPath)
    {
        if (!File.Exists(modelPath))
        {
            // Try fallback search in parent directories (useful during dev/debugging)
            var altPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "models", "footstep_detector.onnx");
            if (File.Exists(altPath))
            {
                modelPath = Path.GetFullPath(altPath);
            }
            else
            {
                return;
            }
        }

        try
        {
            // First attempt: Try GPU acceleration via DirectML (Device ID 0, e.g., RTX 4070)
            var gpuOptions = new SessionOptions();
            gpuOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            try
            {
                gpuOptions.AppendExecutionProvider_DML(0);
                _session = new InferenceSession(modelPath, gpuOptions);
                IsGpuAccelerated = true;
                return;
            }
            catch
            {
                // DirectML not available on current environment, fallback to CPU
                gpuOptions.Dispose();
            }

            // Fallback attempt: CPU Execution Provider
            var cpuOptions = new SessionOptions();
            cpuOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            _session = new InferenceSession(modelPath, cpuOptions);
            IsGpuAccelerated = false;
        }
        catch
        {
            _session = null;
        }
    }

    /// <summary>
    /// Predict footstep probability for 400ms mono float PCM samples [12800].
    /// Returns a probability between 0.0f and 1.0f.
    /// </summary>
    public float Predict(float[] pcmSamples)
    {
        if (_session is null || pcmSamples.Length != WindowSamples)
        {
            return 0f;
        }

        lock (_lock)
        {
            try
            {
                var dimensions = new[] { 1, WindowSamples };
                var tensor = new DenseTensor<float>(pcmSamples, dimensions);
                var inputs = new[] { NamedOnnxValue.CreateFromTensor("pcm_audio", tensor) };

                using var results = _session.Run(inputs);
                var outputTensor = results.FirstOrDefault()?.AsTensor<float>();
                if (outputTensor is not null && outputTensor.Length > 0)
                {
                    return outputTensor.GetValue(0);
                }
            }
            catch
            {
                // Graceful fallback on error
            }

            return 0f;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (!_isDisposed)
            {
                _session?.Dispose();
                _session = null;
                _isDisposed = true;
            }
        }
    }
}
