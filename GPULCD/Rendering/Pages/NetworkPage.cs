using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Net.NetworkInformation;
using GPULCD.Models;
using GPULCD.Rendering.Widgets;

namespace GPULCD.Rendering.Pages;

public class NetworkPage : IPage
{
    private const int MaxHistory = 3600; // 1 hour at 1s
    private readonly List<double> _dlHistory = new();
    private readonly List<double> _ulHistory = new();
    private readonly List<double> _pingHistory = new();
    private readonly string _pingTarget;

    private long _lastBytesReceived;
    private long _lastBytesSent;
    private DateTime _lastSampleTime = DateTime.MinValue;

    private double _dlSpeed; // bytes/sec
    private double _ulSpeed;
    private double _pingMs;
    private string _interfaceName = "";
    private string _ipAddress = "";
    private long _sessionDl;
    private long _sessionUl;
    private long _sessionStartRx;
    private long _sessionStartTx;
    private bool _initialized;
    private int _pingsSent;
    private int _pingsLost;
    private double _packetLossPct;

    public string Name => "Network";
    public int PreferredIntervalMs => 1000;
    public bool IsActive => false;

    public NetworkPage(string pingTarget = "8.8.8.8")
    {
        _pingTarget = pingTarget;
    }

    private int _updateCount;

    public void Update()
    {
        try
        {
            var iface = GetActiveInterface();
            if (iface == null)
            {
                if (_updateCount++ < 5)
                    Debug.WriteLine($"NetworkPage: no active interface found (attempt {_updateCount})");
                return;
            }

            _interfaceName = iface.Name;
            var ipProps = iface.GetIPProperties();
            var addr = ipProps.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            _ipAddress = addr?.Address.ToString() ?? "N/A";

            var stats = iface.GetIPv4Statistics();
            var now = DateTime.UtcNow;

            if (!_initialized)
            {
                _lastBytesReceived = stats.BytesReceived;
                _lastBytesSent = stats.BytesSent;
                _sessionStartRx = stats.BytesReceived;
                _sessionStartTx = stats.BytesSent;
                _lastSampleTime = now;
                _initialized = true;
                return;
            }

            double elapsed = (now - _lastSampleTime).TotalSeconds;
            if (elapsed < 0.1) return;

            long deltaRx = stats.BytesReceived - _lastBytesReceived;
            long deltaTx = stats.BytesSent - _lastBytesSent;

            _dlSpeed = deltaRx / elapsed;
            _ulSpeed = deltaTx / elapsed;
            _sessionDl = stats.BytesReceived - _sessionStartRx;
            _sessionUl = stats.BytesSent - _sessionStartTx;

            _lastBytesReceived = stats.BytesReceived;
            _lastBytesSent = stats.BytesSent;
            _lastSampleTime = now;

            RecordHistory(_dlHistory, _dlSpeed);
            RecordHistory(_ulHistory, _ulSpeed);

            // Ping (async but we'll do it synchronously since we're on a background timer)
            try
            {
                using var ping = new Ping();
                var reply = ping.Send(_pingTarget, 1000);
                _pingsSent++;
                if (reply.Status == IPStatus.Success)
                {
                    _pingMs = reply.RoundtripTime;
                }
                else
                {
                    _pingMs = -1;
                    _pingsLost++;
                }
            }
            catch
            {
                _pingMs = -1;
                _pingsSent++;
                _pingsLost++;
            }

            _packetLossPct = _pingsSent > 0 ? _pingsLost * 100.0 / _pingsSent : 0;
            RecordHistory(_pingHistory, Math.Max(0, _pingMs));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Network update error: {ex.Message}");
        }
    }

    public Bitmap Render(int width, int height)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.FromArgb(15, 15, 20));

        var green = Color.FromArgb(80, 200, 120);
        var orange = Color.FromArgb(255, 160, 60);
        var cyan = Color.FromArgb(80, 220, 220);
        var bgDark = Color.FromArgb(30, 35, 50);

        // Header
        using (var font = new Font("Segoe UI", 11, FontStyle.Bold))
        using (var brush = new SolidBrush(Color.FromArgb(200, 200, 210)))
        {
            var text = "NETWORK";
            var size = g.MeasureString(text, font);
            g.DrawString(text, font, brush, (width - size.Width) / 2, 6);
        }
        using (var pen = new Pen(Color.FromArgb(50, 55, 70), 1))
            g.DrawLine(pen, 16, 30, width - 16, 30);

        int margin = 16;
        int barW = width - margin * 2;
        int barH = 24;
        int sparkH = 36;
        int rowGap = 8;
        int y = 36;

        // Download
        var dlReading = new SensorReading { Name = "Download", Value = _dlSpeed / 1024 / 1024, Unit = "MB/s" };
        ProgressBarWidget.Draw(g, margin, y, barW, barH, "DL", dlReading, 125, green, bgDark);
        y += barH + 2;
        SparklineWidget.Draw(g, margin, y, barW, sparkH, _dlHistory, 0, Math.Max(1, _dlHistory.Count > 0 ? _dlHistory.Max() * 1.2 : 1), green, green);
        y += sparkH + rowGap;

        // Upload
        var ulReading = new SensorReading { Name = "Upload", Value = _ulSpeed / 1024 / 1024, Unit = "MB/s" };
        ProgressBarWidget.Draw(g, margin, y, barW, barH, "UL", ulReading, 25, orange, bgDark);
        y += barH + 2;
        SparklineWidget.Draw(g, margin, y, barW, sparkH, _ulHistory, 0, Math.Max(1, _ulHistory.Count > 0 ? _ulHistory.Max() * 1.2 : 1), orange, orange);
        y += sparkH + rowGap;

        // Ping bar + packet loss indicator
        var pingReading = new SensorReading { Name = "Ping", Value = Math.Max(0, _pingMs), Unit = "ms" };
        ProgressBarWidget.Draw(g, margin, y, barW, barH, "PING", pingReading, 200, cyan, bgDark);

        // Packet loss badge on the right side of the ping bar
        using (var font = new Font("Segoe UI", 7, FontStyle.Bold))
        {
            var lossText = $"{_packetLossPct:F1}% loss";
            var lossSize = g.MeasureString(lossText, font);
            var lossColor = _packetLossPct > 5 ? Color.FromArgb(220, 60, 60)
                          : _packetLossPct > 0 ? Color.FromArgb(255, 180, 40)
                          : Color.FromArgb(80, 85, 100);
            using var brush = new SolidBrush(lossColor);
            g.DrawString(lossText, font, brush, width - margin - lossSize.Width, y + barH + 3);
        }

        y += barH + 2;
        SparklineWidget.Draw(g, margin, y, barW, sparkH, _pingHistory, 0, 200, cyan, cyan);
        y += sparkH + rowGap + 2;

        // Footer: interface + IP + session totals
        using (var font = new Font("Segoe UI", 7))
        using (var brush = new SolidBrush(Color.FromArgb(90, 95, 110)))
        {
            g.DrawString($"{_interfaceName} | {_ipAddress}", font, brush, margin, y);
            var totals = $"{FormatBytes(_sessionDl)} DL | {FormatBytes(_sessionUl)} UL";
            var tSize = g.MeasureString(totals, font);
            g.DrawString(totals, font, brush, width - margin - tSize.Width, y);
        }

        return bmp;
    }

    private static NetworkInterface? GetActiveInterface()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up
                && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                && n.GetIPv4Statistics().BytesReceived > 0);
    }

    private static void RecordHistory(List<double> list, double value)
    {
        list.Add(value);
        if (list.Count > MaxHistory)
            list.RemoveAt(0);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F0} KB";
        return $"{bytes} B";
    }

    public void Dispose() { }
}
