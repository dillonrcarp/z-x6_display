using System.Diagnostics;
using NAudio.Wave;

namespace GPULCD.Services;

public class AudioCaptureService : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private readonly float[] _sampleBuffer = new float[4096];
    private int _sampleCount;
    private readonly object _lock = new();
    private bool _hasAudio;

    public bool IsCapturing { get; private set; }
    public bool HasAudio => _hasAudio;

    public void Start()
    {
        if (IsCapturing) return;

        try
        {
            _capture = new WasapiLoopbackCapture();
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += (_, _) => IsCapturing = false;
            _capture.StartRecording();
            IsCapturing = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Audio capture start failed: {ex.Message}");
        }
    }

    public void Stop()
    {
        if (_capture != null)
        {
            try { _capture.StopRecording(); } catch { }
            _capture.Dispose();
            _capture = null;
        }
        IsCapturing = false;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_capture == null) return;

        int bytesPerSample = _capture.WaveFormat.BitsPerSample / 8;
        int channels = _capture.WaveFormat.Channels;
        int sampleCount = e.BytesRecorded / bytesPerSample / channels;

        lock (_lock)
        {
            _hasAudio = false;
            _sampleCount = 0;

            for (int i = 0; i < sampleCount && _sampleCount < _sampleBuffer.Length; i++)
            {
                int offset = i * channels * bytesPerSample;
                if (offset + bytesPerSample > e.BytesRecorded) break;

                float sample = _capture.WaveFormat.BitsPerSample switch
                {
                    32 => BitConverter.ToSingle(e.Buffer, offset),
                    16 => BitConverter.ToInt16(e.Buffer, offset) / 32768f,
                    _ => 0
                };

                // Mix to mono (just take first channel)
                _sampleBuffer[_sampleCount++] = sample;
                if (Math.Abs(sample) > 0.001f) _hasAudio = true;
            }
        }
    }

    /// <summary>
    /// Get the latest audio samples for FFT processing.
    /// Returns the number of samples copied.
    /// </summary>
    public int GetSamples(float[] output)
    {
        lock (_lock)
        {
            int count = Math.Min(_sampleCount, output.Length);
            Array.Copy(_sampleBuffer, output, count);
            return count;
        }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
