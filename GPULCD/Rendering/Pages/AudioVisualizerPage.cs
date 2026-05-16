using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using GPULCD.Services;

namespace GPULCD.Rendering.Pages;

public class AudioVisualizerPage : IPage
{
    private readonly AudioCaptureService _audio;
    private const int FftSize = 2048;
    private const int BarCount = 48;
    private readonly float[] _samples = new float[FftSize];
    private readonly double[] _barHeights = new double[BarCount];
    private readonly double[] _barPeaks = new double[BarCount];
    private const double DecayRate = 0.85;
    private const double PeakDecay = 0.97;

    public string Name => "Audio Visualizer";
    public int PreferredIntervalMs => 125; // ~8 FPS
    public bool IsActive => _audio.HasAudio;

    public AudioVisualizerPage(AudioCaptureService audio)
    {
        _audio = audio;
    }

    public void Update()
    {
        if (!_audio.IsCapturing)
            _audio.Start();

        int count = _audio.GetSamples(_samples);
        if (count < FftSize)
        {
            // Not enough samples — decay existing bars
            for (int i = 0; i < BarCount; i++)
            {
                _barHeights[i] *= DecayRate;
                _barPeaks[i] *= PeakDecay;
            }
            return;
        }

        // Apply Hanning window
        var windowed = new double[FftSize];
        for (int i = 0; i < FftSize; i++)
            windowed[i] = _samples[i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1)));

        // FFT
        var fftResult = FftSharp.FFT.Forward(windowed);
        var magnitudes = FftSharp.FFT.Magnitude(fftResult);

        // Map to bars using logarithmic frequency bands
        int halfFft = magnitudes.Length;
        for (int bar = 0; bar < BarCount; bar++)
        {
            // Logarithmic mapping: lower bars = fewer bins, higher bars = more bins
            double frac0 = (double)bar / BarCount;
            double frac1 = (double)(bar + 1) / BarCount;
            int bin0 = (int)(Math.Pow(frac0, 2.0) * halfFft);
            int bin1 = (int)(Math.Pow(frac1, 2.0) * halfFft);
            bin0 = Math.Max(1, bin0);
            bin1 = Math.Max(bin0 + 1, bin1);
            bin1 = Math.Min(bin1, halfFft);

            double sum = 0;
            for (int b = bin0; b < bin1; b++)
                sum = Math.Max(sum, magnitudes[b]);

            // Convert to dB-ish scale, normalize
            double db = sum > 0 ? 20 * Math.Log10(sum + 1e-10) : -100;
            double normalized = Math.Clamp((db + 60) / 60, 0, 1); // -60dB floor

            // Smooth: rise fast, fall slow
            if (normalized > _barHeights[bar])
                _barHeights[bar] = normalized;
            else
                _barHeights[bar] = _barHeights[bar] * DecayRate + normalized * (1 - DecayRate);

            // Peak tracker
            if (_barHeights[bar] > _barPeaks[bar])
                _barPeaks[bar] = _barHeights[bar];
            else
                _barPeaks[bar] *= PeakDecay;
        }
    }

    public Bitmap Render(int width, int height)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.FromArgb(8, 8, 12));

        int margin = 12;
        int bottomMargin = 16;
        int topMargin = 8;
        int barArea = width - margin * 2;
        int barWidth = barArea / BarCount;
        int gap = Math.Max(1, barWidth / 5);
        barWidth -= gap;
        int maxBarH = height - topMargin - bottomMargin;

        for (int i = 0; i < BarCount; i++)
        {
            int x = margin + i * (barWidth + gap);
            int barH = (int)(_barHeights[i] * maxBarH);
            barH = Math.Max(2, barH);
            int y = height - bottomMargin - barH;

            // Gradient color: blue → purple → red based on bar index
            float t = (float)i / (BarCount - 1);
            Color barColor;
            if (t < 0.5f)
            {
                float t2 = t * 2;
                barColor = Color.FromArgb(
                    (int)(40 + 120 * t2),
                    (int)(140 - 60 * t2),
                    (int)(255 - 100 * t2));
            }
            else
            {
                float t2 = (t - 0.5f) * 2;
                barColor = Color.FromArgb(
                    (int)(160 + 80 * t2),
                    (int)(80 - 40 * t2),
                    (int)(155 - 80 * t2));
            }

            // Bar with vertical gradient
            using (var brush = new LinearGradientBrush(
                new Rectangle(x, y, barWidth, barH),
                barColor,
                Color.FromArgb(barColor.A, barColor.R / 3, barColor.G / 3, barColor.B / 3),
                LinearGradientMode.Vertical))
            {
                g.FillRectangle(brush, x, y, barWidth, barH);
            }

            // Peak dot
            int peakY = height - bottomMargin - (int)(_barPeaks[i] * maxBarH);
            using (var brush = new SolidBrush(Color.FromArgb(180, barColor)))
                g.FillRectangle(brush, x, peakY, barWidth, 2);
        }

        // Subtle reflection
        using (var brush = new LinearGradientBrush(
            new Rectangle(0, height - bottomMargin, width, bottomMargin),
            Color.FromArgb(15, 100, 100, 255),
            Color.Transparent,
            LinearGradientMode.Vertical))
        {
            g.FillRectangle(brush, 0, height - bottomMargin, width, bottomMargin);
        }

        return bmp;
    }

    public void Dispose()
    {
        _audio.Stop();
    }
}
