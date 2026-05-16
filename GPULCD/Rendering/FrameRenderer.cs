using System.Drawing;

namespace GPULCD.Rendering;

/// <summary>
/// Routes rendering to the active page and manages page switching with smart auto-rotate.
/// </summary>
public class FrameRenderer : IDisposable
{
    private readonly int _width;
    private readonly int _height;
    private readonly List<IPage> _pages = new();
    private int _currentIndex;
    private DateTime _lastRotateTime = DateTime.UtcNow;

    public int AutoRotateSeconds { get; set; } = 30;
    public bool AutoRotateEnabled { get; set; } = true;

    public IPage? CurrentPage => _pages.Count > 0 ? _pages[_currentIndex] : null;
    public int CurrentIndex => _currentIndex;
    public IReadOnlyList<IPage> Pages => _pages;

    public event EventHandler<IPage>? PageChanged;

    public FrameRenderer(int width = 480, int height = 320)
    {
        _width = width;
        _height = height;
    }

    public void AddPage(IPage page) => _pages.Add(page);

    public void SetPage(int index)
    {
        if (index < 0 || index >= _pages.Count) return;
        _currentIndex = index;
        _lastRotateTime = DateTime.UtcNow;
        PageChanged?.Invoke(this, _pages[_currentIndex]);
    }

    public void NextPage()
    {
        if (_pages.Count == 0) return;
        SetPage((_currentIndex + 1) % _pages.Count);
    }

    public void PrevPage()
    {
        if (_pages.Count == 0) return;
        SetPage((_currentIndex - 1 + _pages.Count) % _pages.Count);
    }

    public Bitmap RenderFrame()
    {
        if (_pages.Count == 0)
            return CreateBlankFrame("No pages configured");

        // Smart auto-rotate
        if (AutoRotateEnabled && AutoRotateSeconds > 0)
        {
            var elapsed = (DateTime.UtcNow - _lastRotateTime).TotalSeconds;
            if (elapsed >= AutoRotateSeconds)
            {
                var current = _pages[_currentIndex];
                if (!current.IsActive) // don't rotate away from active pages
                {
                    int next = FindNextPage(_currentIndex);
                    if (next != _currentIndex)
                    {
                        _currentIndex = next;
                        _lastRotateTime = DateTime.UtcNow;
                        PageChanged?.Invoke(this, _pages[_currentIndex]);
                    }
                }
                else
                {
                    _lastRotateTime = DateTime.UtcNow; // reset timer, check again next interval
                }
            }
        }

        var page = _pages[_currentIndex];
        page.Update();
        return page.Render(_width, _height);
    }

    /// <summary>Find the next page that doesn't want to be skipped. Falls back to current if all skip.</summary>
    private int FindNextPage(int fromIndex)
    {
        for (int i = 1; i <= _pages.Count; i++)
        {
            int candidate = (fromIndex + i) % _pages.Count;
            if (!_pages[candidate].ShouldSkip)
                return candidate;
        }
        return fromIndex; // all pages want to skip, stay put
    }

    private Bitmap CreateBlankFrame(string message)
    {
        var bmp = new Bitmap(_width, _height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(15, 15, 20));
        using var font = new Font("Segoe UI", 12);
        using var brush = new SolidBrush(Color.FromArgb(100, 105, 120));
        var size = g.MeasureString(message, font);
        g.DrawString(message, font, brush, (_width - size.Width) / 2, (_height - size.Height) / 2);
        return bmp;
    }

    public void Dispose()
    {
        foreach (var page in _pages)
            page.Dispose();
        _pages.Clear();
    }
}
