using System.Drawing;

namespace GPULCD.Rendering;

public interface IPage : IDisposable
{
    string Name { get; }
    int PreferredIntervalMs { get; }

    /// <summary>Whether this page has active content that should prevent auto-rotate.</summary>
    bool IsActive { get; }

    /// <summary>Whether this page should be skipped during auto-rotate (nothing worth showing).</summary>
    bool ShouldSkip => false;

    void Update();
    Bitmap Render(int width, int height);
}
