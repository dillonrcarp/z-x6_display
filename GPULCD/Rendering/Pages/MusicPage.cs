using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using GPULCD.Services;

namespace GPULCD.Rendering.Pages;

/// <summary>
/// Combined Now Playing + Audio Visualizer page.
/// Top half: album art, track info, progress bar.
/// Bottom half: spectrum bars reacting to audio.
/// </summary>
public class MusicPage : IPage
{
    private readonly MediaSessionService _media;
    private readonly AudioCaptureService _audio;
    private bool _mediaInitialized;

    private const int FftSize = 2048;
    private const int BarCount = 48;
    private readonly float[] _samples = new float[FftSize];
    private readonly double[] _barHeights = new double[BarCount];
    private readonly double[] _barPeaks = new double[BarCount];
    private const double DecayRate = 0.85;
    private const double PeakDecay = 0.97;

    public string Name => "Music";
    public int PreferredIntervalMs => 200; // 5 FPS — balanced for serial bandwidth
    public bool IsActive => _media.IsPlaying || _audio.HasAudio;
    public bool ShouldSkip => !_media.IsPlaying && !_audio.HasAudio;

    public MusicPage(MediaSessionService media, AudioCaptureService audio)
    {
        _media = media;
        _audio = audio;
    }

    public void Update()
    {
        // Initialize media session
        if (!_mediaInitialized)
        {
            _media.InitializeAsync().ConfigureAwait(false);
            _mediaInitialized = true;
        }
        _media.Update();

        // Audio capture + FFT
        if (!_audio.IsCapturing)
            _audio.Start();

        int count = _audio.GetSamples(_samples);
        if (count < FftSize)
        {
            for (int i = 0; i < BarCount; i++)
            {
                _barHeights[i] *= DecayRate;
                _barPeaks[i] *= PeakDecay;
            }
            return;
        }

        var windowed = new double[FftSize];
        for (int i = 0; i < FftSize; i++)
            windowed[i] = _samples[i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1)));

        var fftResult = FftSharp.FFT.Forward(windowed);
        var magnitudes = FftSharp.FFT.Magnitude(fftResult);

        int halfFft = magnitudes.Length;
        for (int bar = 0; bar < BarCount; bar++)
        {
            double frac0 = (double)bar / BarCount;
            double frac1 = (double)(bar + 1) / BarCount;
            int bin0 = Math.Max(1, (int)(Math.Pow(frac0, 2.0) * halfFft));
            int bin1 = Math.Min(halfFft, Math.Max(bin0 + 1, (int)(Math.Pow(frac1, 2.0) * halfFft)));

            double peak = 0;
            for (int b = bin0; b < bin1; b++)
                peak = Math.Max(peak, magnitudes[b]);

            double db = peak > 0 ? 20 * Math.Log10(peak + 1e-10) : -100;
            double normalized = Math.Clamp((db + 60) / 60, 0, 1);

            if (normalized > _barHeights[bar])
                _barHeights[bar] = normalized;
            else
                _barHeights[bar] = _barHeights[bar] * DecayRate + normalized * (1 - DecayRate);

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
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.FromArgb(8, 8, 12));

        // Layout: top section for track info, bottom section for visualizer
        int vizHeight = height * 45 / 100; // bottom 45%
        int infoHeight = height - vizHeight;

        // --- Draw visualizer bars in the bottom ---
        DrawVisualizer(g, 0, infoHeight, width, vizHeight);

        // --- Draw track info in the top ---
        DrawTrackInfo(g, width, infoHeight);

        // --- Progress bar spanning full width between sections ---
        DrawProgressBar(g, infoHeight - 12, width);

        return bmp;
    }

    private void DrawVisualizer(Graphics g, int x, int y, int w, int h)
    {
        int margin = 8;
        int barArea = w - margin * 2;
        int barWidth = barArea / BarCount;
        int gap = Math.Max(1, barWidth / 5);
        barWidth -= gap;
        int bottomPad = 4;

        for (int i = 0; i < BarCount; i++)
        {
            int bx = x + margin + i * (barWidth + gap);
            int barH = (int)(_barHeights[i] * (h - bottomPad));
            barH = Math.Max(2, barH);
            int by = y + h - bottomPad - barH;

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

            using (var brush = new LinearGradientBrush(
                new Rectangle(bx, by, barWidth, Math.Max(1, barH)),
                barColor,
                Color.FromArgb(barColor.R / 3, barColor.G / 3, barColor.B / 3),
                LinearGradientMode.Vertical))
            {
                g.FillRectangle(brush, bx, by, barWidth, barH);
            }

            // Peak dot
            int peakY = y + h - bottomPad - (int)(_barPeaks[i] * (h - bottomPad));
            using (var brush = new SolidBrush(Color.FromArgb(180, barColor)))
                g.FillRectangle(brush, bx, peakY, barWidth, 2);
        }
    }

    private void DrawTrackInfo(Graphics g, int width, int infoHeight)
    {
        var accent = Color.FromArgb(100, 220, 130);

        if (!_media.HasSession || string.IsNullOrEmpty(_media.Title))
        {
            // No media — just show a subtle label
            using var font = new Font("Segoe UI", 11);
            using var brush = new SolidBrush(Color.FromArgb(60, 65, 80));
            var text = "No media playing";
            var size = g.MeasureString(text, font);
            g.DrawString(text, font, brush, (width - size.Width) / 2, (infoHeight - size.Height) / 2);
            return;
        }

        int margin = 14;

        // Album art on the left
        int artSize = infoHeight - margin * 2 - 16;
        artSize = Math.Max(40, artSize);
        int artX = margin;
        int artY = margin;

        if (_media.AlbumArt != null)
        {
            using var path = RoundedRect(artX, artY, artSize, artSize, 10);
            g.SetClip(path);
            g.DrawImage(_media.AlbumArt, artX, artY, artSize, artSize);
            g.ResetClip();
            using var pen = new Pen(Color.FromArgb(40, 45, 60), 1);
            g.DrawPath(pen, path);
        }
        else
        {
            using var brush = new SolidBrush(Color.FromArgb(25, 28, 40));
            using var path = RoundedRect(artX, artY, artSize, artSize, 10);
            g.FillPath(brush, path);
            using var noteFont = new Font("Segoe UI", 22);
            using var noteBrush = new SolidBrush(Color.FromArgb(50, 55, 70));
            g.DrawString("\u266B", noteFont, noteBrush, artX + artSize / 2 - 14, artY + artSize / 2 - 18);
        }

        // Text on the right
        int textX = artX + artSize + 14;
        int textW = width - textX - margin;
        int textY = artY + 4;

        // Playing/paused
        string status = _media.IsPlaying ? "\u25B6" : "\u23F8";
        using (var font = new Font("Segoe UI", 8))
        using (var brush = new SolidBrush(accent))
        {
            g.DrawString(status, font, brush, textX, textY);
            textY += 16;
        }

        // Title
        using (var font = new Font("Segoe UI", 12, FontStyle.Bold))
        using (var brush = new SolidBrush(Color.White))
        {
            var title = TruncateText(g, _media.Title, font, textW);
            g.DrawString(title, font, brush, textX, textY);
            textY += 24;
        }

        // Artist
        using (var font = new Font("Segoe UI", 9))
        using (var brush = new SolidBrush(Color.FromArgb(150, 155, 170)))
        {
            var artist = TruncateText(g, _media.Artist, font, textW);
            g.DrawString(artist, font, brush, textX, textY);
            textY += 18;
        }

        // Album
        if (!string.IsNullOrEmpty(_media.Album))
        {
            using var font = new Font("Segoe UI", 7);
            using var brush = new SolidBrush(Color.FromArgb(90, 95, 110));
            var album = TruncateText(g, _media.Album, font, textW);
            g.DrawString(album, font, brush, textX, textY);
        }

        // Time on the right side
        using (var font = new Font("Segoe UI", 7))
        using (var brush = new SolidBrush(Color.FromArgb(100, 105, 120)))
        {
            string elapsed = FormatTime(_media.Position);
            string total = FormatTime(_media.Duration);
            string timeStr = $"{elapsed} / {total}";
            var tSize = g.MeasureString(timeStr, font);
            g.DrawString(timeStr, font, brush, width - margin - tSize.Width, infoHeight - 22);
        }
    }

    private void DrawProgressBar(Graphics g, int y, int width)
    {
        int margin = 14;
        int barH = 4;
        int barX = margin;
        int barW = width - margin * 2;

        // Background
        using (var brush = new SolidBrush(Color.FromArgb(25, 30, 45)))
            g.FillRectangle(brush, barX, y, barW, barH);

        // Fill
        int fillW = (int)(barW * _media.ProgressPercent / 100.0);
        if (fillW > 0)
        {
            using var brush = new SolidBrush(Color.FromArgb(100, 220, 130));
            g.FillRectangle(brush, barX, y, fillW, barH);
        }
    }

    private static string TruncateText(Graphics g, string text, Font font, int maxWidth)
    {
        if (g.MeasureString(text, font).Width <= maxWidth) return text;
        while (text.Length > 3 && g.MeasureString(text + "...", font).Width > maxWidth)
            text = text[..^1];
        return text + "...";
    }

    private static string FormatTime(TimeSpan ts) =>
        ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");

    private static GraphicsPath RoundedRect(int x, int y, int w, int h, int r)
    {
        var path = new GraphicsPath();
        int d = Math.Min(r * 2, Math.Min(w, h));
        r = d / 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        _audio.Stop();
    }
}
