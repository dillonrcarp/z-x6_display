using System.Text;
using System.Threading;

namespace GPULCD;

public partial class App : System.Windows.Application
{
    private static Mutex? _mutex;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        // Register Windows-1252 encoding (needed for HWiNFO degree symbol)
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // Single instance enforcement
        _mutex = new Mutex(true, "GPULCD_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show("GPULCD is already running. Check your system tray.",
                "GPULCD", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
