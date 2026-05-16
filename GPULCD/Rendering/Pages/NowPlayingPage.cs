using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using GPULCD.Services;

namespace GPULCD.Rendering.Pages;

public class NowPlayingPage : IPage
{
    private readonly MediaSessionService _media;
    private bool _initialized;

    public string Name => "Now Playing";
    public int PreferredIntervalMs => 500;
    public bool IsActive => _media.IsPlaying;
    public bool ShouldSkip => !_media.IsPlaying;

    public NowPlayingPage(MediaSessionService media)
    {
        _media = media;
    }

    public void Update()
    {
        if (!_initialized)
        {
            _media.InitializeAsync().ConfigureAwait(false);
            _initialized = true;
        }
        _media.Update();
    }

    public Bitmap Render(int width, int height)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.FromArgb(15, 15, 20));

        if (!_media.HasSession || string.IsNullOrEmpty(_media.Title))
        {
            RenderNoMedia(g, width, height);
            return bmp;
        }

        var accent = Color.FromArgb(100, 220, 130);
        int margin = 16;

        // Album art — large square on the left
        int artSize = height - margin * 2 - 24; // leave room for progress bar
        int artX = margin;
        int artY = margin;

        if (_media.AlbumArt != null)
        {
            using var path = RoundedRect(artX, artY, artSize, artSize, 14);
            g.SetClip(path);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(_media.AlbumArt, artX, artY, artSize, artSize);
            g.ResetClip();
            using var pen = new Pen(Color.FromArgb(50, 55, 70), 1);
            g.DrawPath(pen, path);
        }
        else
        {
            using var brush = new SolidBrush(Color.FromArgb(25, 28, 40));
            using var path = RoundedRect(artX, artY, artSize, artSize, 14);
            g.FillPath(brush, path);
            using var noteFont = new Font("Segoe UI", 36);
            using var noteBrush = new SolidBrush(Color.FromArgb(50, 55, 70));
            var noteSize = g.MeasureString("\u266B", noteFont);
            g.DrawString("\u266B", noteFont, noteBrush,
                artX + (artSize - noteSize.Width) / 2,
                artY + (artSize - noteSize.Height) / 2);
        }

        // Text area — right side
        int textX = artX + artSize + 18;
        int textW = width - textX - margin;
        int textY = artY + 12;

        // Playing/paused status
        string status = _media.IsPlaying ? "NOW PLAYING" : "PAUSED";
        using (var font = new Font("Segoe UI", 8, FontStyle.Bold))
        using (var brush = new SolidBrush(accent))
        {
            g.DrawString(status, font, brush, textX, textY);
            textY += 22;
        }

        // Title
        using (var font = new Font("Segoe UI", 15, FontStyle.Bold))
        using (var brush = new SolidBrush(Color.White))
        {
            var title = TruncateText(g, _media.Title, font, textW);
            g.DrawString(title, font, brush, textX, textY);
            textY += 32;
        }

        // Artist
        using (var font = new Font("Segoe UI", 11))
        using (var brush = new SolidBrush(Color.FromArgb(160, 165, 180)))
        {
            var artist = TruncateText(g, _media.Artist, font, textW);
            g.DrawString(artist, font, brush, textX, textY);
            textY += 24;
        }

        // Album
        if (!string.IsNullOrEmpty(_media.Album))
        {
            using var font = new Font("Segoe UI", 9);
            using var brush = new SolidBrush(Color.FromArgb(100, 105, 120));
            var album = TruncateText(g, _media.Album, font, textW);
            g.DrawString(album, font, brush, textX, textY);
        }

        // Time elapsed / total — bottom right of text area
        using (var font = new Font("Segoe UI", 9))
        using (var brush = new SolidBrush(Color.FromArgb(120, 125, 140)))
        {
            string elapsed = FormatTime(_media.Position);
            string total = FormatTime(_media.Duration);
            string timeStr = $"{elapsed} / {total}";
            var tSize = g.MeasureString(timeStr, font);
            g.DrawString(timeStr, font, brush,
                textX, artY + artSize - tSize.Height - 4);
        }

        // Progress bar — full width at the bottom
        int barY = height - margin - 8;
        int barH = 6;
        int barX = margin;
        int barW = width - margin * 2;

        using (var brush = new SolidBrush(Color.FromArgb(30, 35, 50)))
        {
            var path = RoundedRect(barX, barY, barW, barH, 3);
            g.FillPath(brush, path);
        }

        int fillW = (int)(barW * _media.ProgressPercent / 100.0);
        if (fillW > 0)
        {
            fillW = Math.Max(barH, Math.Min(fillW, barW));
            using var brush = new SolidBrush(accent);
            var path = RoundedRect(barX, barY, fillW, barH, 3);
            g.FillPath(brush, path);
        }

        return bmp;
    }

    private void RenderNoMedia(Graphics g, int w, int h)
    {
        using var noteFont = new Font("Segoe UI", 32);
        using var noteBrush = new SolidBrush(Color.FromArgb(50, 55, 70));
        string note = "\u266B";
        var noteSize = g.MeasureString(note, noteFont);
        g.DrawString(note, noteFont, noteBrush, (w - noteSize.Width) / 2, h / 2 - 50);

        using var font = new Font("Segoe UI", 13);
        using var brush = new SolidBrush(Color.FromArgb(70, 75, 90));
        var text = "Nothing Playing";
        var size = g.MeasureString(text, font);
        g.DrawString(text, font, brush, (w - size.Width) / 2, h / 2 + 10);
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

    public void Dispose() { }
}
