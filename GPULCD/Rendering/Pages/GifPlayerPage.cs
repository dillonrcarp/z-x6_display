using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ISImage = SixLabors.ImageSharp.Image;

namespace GPULCD.Rendering.Pages;

public class GifPlayerPage : IPage
{
    private readonly string _gifPath;
    private List<(Bitmap Frame, int DelayMs)>? _frames;
    private int _frameIndex;
    private DateTime _lastFrameTime = DateTime.UtcNow;
    private int _currentDelayMs = 100;
    private bool _loaded;
    private string? _loadError;

    public string Name => "GIF Player";
    public int PreferredIntervalMs => 33; // ~30 FPS max tick rate
    public bool IsActive => false;

    public GifPlayerPage(string gifPath)
    {
        _gifPath = gifPath;
    }

    public void Update()
    {
        if (!_loaded)
        {
            LoadGif();
            _loaded = true;
        }
    }

    public Bitmap Render(int width, int height)
    {
        if (_frames == null || _frames.Count == 0)
            return RenderError(width, height);

        // Advance frame based on elapsed time
        var now = DateTime.UtcNow;
        if ((now - _lastFrameTime).TotalMilliseconds >= _currentDelayMs)
        {
            _frameIndex = (_frameIndex + 1) % _frames.Count;
            _currentDelayMs = _frames[_frameIndex].DelayMs;
            if (_currentDelayMs < 20) _currentDelayMs = 100; // GIF spec: 0 = use default
            _lastFrameTime = now;
        }

        var source = _frames[_frameIndex].Frame;

        // Create output bitmap, center the GIF
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(System.Drawing.Color.Black);
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;

        // Scale to fit while maintaining aspect ratio
        float scale = Math.Min((float)width / source.Width, (float)height / source.Height);
        int dw = (int)(source.Width * scale);
        int dh = (int)(source.Height * scale);
        int dx = (width - dw) / 2;
        int dy = (height - dh) / 2;

        g.DrawImage(source, dx, dy, dw, dh);

        return bmp;
    }

    private void LoadGif()
    {
        try
        {
            if (!System.IO.File.Exists(_gifPath))
            {
                _loadError = $"File not found: {_gifPath}";
                return;
            }

            using var image = ISImage.Load<Rgba32>(_gifPath);
            _frames = new List<(Bitmap, int)>(image.Frames.Count);

            for (int i = 0; i < image.Frames.Count; i++)
            {
                var meta = image.Frames[i].Metadata.GetGifMetadata();
                int delayMs = meta.FrameDelay * 10; // centiseconds → ms

                // Clone the frame, apply all previous frames (GIF disposal)
                using var frameImage = image.Clone(ctx => { });
                // Select just this frame by extracting it
                var singleFrame = image.Frames.CloneFrame(i);

                // Convert ImageSharp image to System.Drawing.Bitmap
                using var ms = new System.IO.MemoryStream();
                singleFrame.SaveAsPng(ms);
                ms.Position = 0;
                var bitmap = new Bitmap(ms);

                _frames.Add((bitmap, delayMs));
                singleFrame.Dispose();
            }

            Debug.WriteLine($"GIF loaded: {_frames.Count} frames from {_gifPath}");
        }
        catch (Exception ex)
        {
            _loadError = $"Failed to load GIF: {ex.Message}";
            Debug.WriteLine(_loadError);
        }
    }

    private Bitmap RenderError(int width, int height)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(System.Drawing.Color.FromArgb(15, 15, 20));

        string msg = _loadError ?? "No GIF loaded";
        using var font = new System.Drawing.Font("Segoe UI", 10);
        using var brush = new SolidBrush(System.Drawing.Color.FromArgb(120, 125, 140));
        var size = g.MeasureString(msg, font, width - 40);
        g.DrawString(msg, font, brush, (width - size.Width) / 2, (height - size.Height) / 2);

        return bmp;
    }

    public void Dispose()
    {
        if (_frames != null)
        {
            foreach (var (frame, _) in _frames)
                frame.Dispose();
            _frames.Clear();
        }
    }
}
