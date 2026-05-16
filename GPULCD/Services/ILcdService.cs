using System.Drawing;
using GPULCD.Models;

namespace GPULCD.Services;

public interface ILcdService : IDisposable
{
    ConnectionState State { get; }
    ScreenModel DetectedModel { get; }
    int DisplayWidth { get; }
    int DisplayHeight { get; }

    event EventHandler<ConnectionState>? StateChanged;

    Task ConnectAsync(string? comPort = null, CancellationToken ct = default);
    void Disconnect();
    void SetBrightness(int levelPercent);
    void SetOrientation(DisplayOrientation orientation);
    void SendBitmap(Bitmap bitmap, int x = 0, int y = 0);
    void ScreenOn();
    void ScreenOff();
}
