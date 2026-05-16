namespace GPULCD.Models;

public enum ScreenModel
{
    Unknown,
    Turing35,
    UsbMonitor35,
    UsbMonitor5,
    UsbMonitor7
}

public static class ScreenModelExtensions
{
    public static (int Width, int Height) GetResolution(this ScreenModel model) => model switch
    {
        ScreenModel.UsbMonitor5 => (480, 800),
        ScreenModel.UsbMonitor7 => (600, 1024),
        _ => (320, 480)
    };
}
