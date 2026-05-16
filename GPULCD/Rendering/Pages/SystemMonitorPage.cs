using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using GPULCD.Models;
using GPULCD.Rendering.Widgets;
using GPULCD.Services;

namespace GPULCD.Rendering.Pages;

public class SystemMonitorPage : IPage
{
    private const int MaxHistory = 7200;
    private readonly Dictionary<string, List<double>> _history = new();
    private readonly ISensorService _sensors;
    private readonly AppSettings _settings;
    private Dictionary<string, SensorReading> _mapped = new();

    public string Name => "System Monitor";
    public int PreferredIntervalMs => 500;
    public bool IsActive => false; // never blocks auto-rotate

    public SystemMonitorPage(ISensorService sensors, AppSettings settings)
    {
        _sensors = sensors;
        _settings = settings;
    }

    public void Update()
    {
        var allReadings = _sensors.GetCurrentReadings();
        _mapped = new Dictionary<string, SensorReading>();

        bool IsFanSlot(string slot) => slot.EndsWith("Fan", StringComparison.OrdinalIgnoreCase);

        foreach (var (slot, sensorName) in _settings.SensorMappings)
        {
            var candidates = allReadings.Where(kvp =>
                kvp.Key.Contains(sensorName, StringComparison.OrdinalIgnoreCase)).ToList();

            if (candidates.Count == 0) continue;

            // For fan slots, prefer sensors with RPM unit to avoid matching temp sensors
            if (IsFanSlot(slot))
            {
                var rpmMatch = candidates.FirstOrDefault(kvp =>
                    kvp.Value.Unit.Equals("RPM", StringComparison.OrdinalIgnoreCase));
                if (rpmMatch.Value != null)
                {
                    _mapped[slot] = rpmMatch.Value;
                    continue;
                }
            }

            // Prefer exact key match, otherwise take first fuzzy match
            if (allReadings.TryGetValue(sensorName, out var exact))
                _mapped[slot] = exact;
            else
                _mapped[slot] = candidates[0].Value;
        }

        foreach (var (key, reading) in _mapped)
            RecordHistory(key, reading.Value);
    }

    public Bitmap Render(int width, int height)
    {
        var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.FromArgb(15, 15, 20));

        if (width >= height)
            RenderLandscape(g, width, height);
        else
            RenderPortrait(g, width, height);

        return bmp;
    }

    private void RenderLandscape(Graphics g, int w, int h)
    {
        var blue = Color.FromArgb(65, 165, 255);
        var red = Color.FromArgb(255, 100, 80);
        var purple = Color.FromArgb(140, 110, 255);
        var cyan = Color.FromArgb(80, 220, 220);
        var bgDark = Color.FromArgb(30, 35, 50);

        using (var font = new Font("Segoe UI", 11, FontStyle.Bold))
        using (var brush = new SolidBrush(Color.FromArgb(200, 200, 210)))
        {
            var text = "SYSTEM MONITOR";
            var size = g.MeasureString(text, font);
            g.DrawString(text, font, brush, (w - size.Width) / 2, 6);
        }
        using (var pen = new Pen(Color.FromArgb(50, 55, 70), 1))
            g.DrawLine(pen, 16, 30, w - 16, 30);

        int gaugeSize = 112;
        int leftX = 12;
        int topY = 36;
        int sparkH_gauge = 20;

        if (_mapped.TryGetValue("CpuTemp", out var cpuTemp))
        {
            ArcGaugeWidget.Draw(g, leftX, topY, gaugeSize, gaugeSize, "CPU", cpuTemp, 100, blue, bgDark);
            SparklineWidget.Draw(g, leftX + 6, topY + gaugeSize + 2, gaugeSize - 12, sparkH_gauge,
                GetHistory("CpuTemp"), 20, 100, blue, blue);
        }

        int gpu_y = topY + gaugeSize + sparkH_gauge + 8;
        if (_mapped.TryGetValue("GpuTemp", out var gpuTemp))
        {
            ArcGaugeWidget.Draw(g, leftX, gpu_y, gaugeSize, gaugeSize, "GPU", gpuTemp, 100, red, bgDark);
            SparklineWidget.Draw(g, leftX + 6, gpu_y + gaugeSize + 2, gaugeSize - 12, sparkH_gauge,
                GetHistory("GpuTemp"), 20, 100, red, red);
        }

        int barX = leftX + gaugeSize + 14;
        int barW = w - barX - 14;
        int barH = 24;
        int sparkH = 18;
        int rowSpacing = 48;
        int barY = topY + 4;

        if (_mapped.TryGetValue("CpuUsage", out var cpuUsage))
        {
            ProgressBarWidget.Draw(g, barX, barY, barW, barH, "CPU", cpuUsage, 100, blue, bgDark);
            SparklineWidget.Draw(g, barX, barY + barH + 2, barW, sparkH,
                GetHistory("CpuUsage"), 0, 100, blue, blue);
        }
        barY += rowSpacing;

        if (_mapped.TryGetValue("GpuUsage", out var gpuUsage))
        {
            ProgressBarWidget.Draw(g, barX, barY, barW, barH, "GPU", gpuUsage, 100, red, bgDark);
            SparklineWidget.Draw(g, barX, barY + barH + 2, barW, sparkH,
                GetHistory("GpuUsage"), 0, 100, red, red);
        }
        barY += rowSpacing;

        if (_mapped.TryGetValue("RamUsage", out var ramUsage))
        {
            ProgressBarWidget.Draw(g, barX, barY, barW, barH, "RAM", ramUsage, 100, purple, bgDark);
            SparklineWidget.Draw(g, barX, barY + barH + 2, barW, sparkH,
                GetHistory("RamUsage"), 0, 100, purple, purple);
        }

        int fanRowY = barY + barH + sparkH + 12;
        int fanBoxSize = (barW - 12) / 3;
        int fanGap = 6;

        if (_mapped.TryGetValue("CpuFan", out var cpuFan))
            FanBoxWidget.Draw(g, barX, fanRowY, fanBoxSize, "RAD FAN", cpuFan, 2000, blue, bgDark);
        if (_mapped.TryGetValue("GpuFan", out var gpuFan))
            FanBoxWidget.Draw(g, barX + fanBoxSize + fanGap, fanRowY, fanBoxSize, "GPU FAN", gpuFan, 2500, red, bgDark);
        if (_mapped.TryGetValue("CaseFan", out var caseFan))
            FanBoxWidget.Draw(g, barX + (fanBoxSize + fanGap) * 2, fanRowY, fanBoxSize, "CASE", caseFan, 1500, cyan, bgDark);

        using (var font = new Font("Segoe UI", 7))
        using (var brush = new SolidBrush(Color.FromArgb(60, 65, 75)))
        {
            var time = DateTime.Now.ToString("HH:mm:ss");
            var size = g.MeasureString(time, font);
            g.DrawString(time, font, brush, w - size.Width - 12, h - 16);
        }
    }

    private void RenderPortrait(Graphics g, int w, int h)
    {
        var blue = Color.FromArgb(65, 165, 255);
        var red = Color.FromArgb(255, 100, 80);
        var purple = Color.FromArgb(140, 110, 255);
        var bgDark = Color.FromArgb(30, 35, 50);

        using (var font = new Font("Segoe UI", 14, FontStyle.Bold))
        using (var brush = new SolidBrush(Color.FromArgb(200, 200, 210)))
        {
            var text = "SYSTEM MONITOR";
            var size = g.MeasureString(text, font);
            g.DrawString(text, font, brush, (w - size.Width) / 2, 16);
        }
        using (var pen = new Pen(Color.FromArgb(50, 55, 70), 1))
            g.DrawLine(pen, 20, 48, w - 20, 48);

        int y = 60;
        if (_mapped.TryGetValue("CpuTemp", out var cpuTemp))
        {
            ArcGaugeWidget.Draw(g, 20, y, 130, 130, "CPU", cpuTemp, 100, blue, bgDark);
            SparklineWidget.Draw(g, 30, y + 126, 110, 20, GetHistory("CpuTemp"), 20, 100, blue, blue);
        }
        if (_mapped.TryGetValue("GpuTemp", out var gpuTemp))
        {
            ArcGaugeWidget.Draw(g, 170, y, 130, 130, "GPU", gpuTemp, 100, red, bgDark);
            SparklineWidget.Draw(g, 180, y + 126, 110, 20, GetHistory("GpuTemp"), 20, 100, red, red);
        }

        y += 160;
        int margin = 20;
        int barW = w - margin * 2;

        if (_mapped.TryGetValue("CpuUsage", out var cpuUsage))
        {
            ProgressBarWidget.Draw(g, margin, y, barW, 36, "CPU", cpuUsage, 100, blue, bgDark);
            SparklineWidget.Draw(g, margin, y + 38, barW, 24, GetHistory("CpuUsage"), 0, 100, blue, blue);
        }
        y += 74;
        if (_mapped.TryGetValue("GpuUsage", out var gpuUsage))
        {
            ProgressBarWidget.Draw(g, margin, y, barW, 36, "GPU", gpuUsage, 100, red, bgDark);
            SparklineWidget.Draw(g, margin, y + 38, barW, 24, GetHistory("GpuUsage"), 0, 100, red, red);
        }
        y += 74;
        if (_mapped.TryGetValue("RamUsage", out var ramUsage))
        {
            ProgressBarWidget.Draw(g, margin, y, barW, 36, "RAM", ramUsage, 100, purple, bgDark);
            SparklineWidget.Draw(g, margin, y + 38, barW, 24, GetHistory("RamUsage"), 0, 100, purple, purple);
        }

        using (var font = new Font("Segoe UI", 8))
        using (var brush = new SolidBrush(Color.FromArgb(80, 85, 95)))
        {
            var time = DateTime.Now.ToString("HH:mm:ss");
            var size = g.MeasureString(time, font);
            g.DrawString(time, font, brush, (w - size.Width) / 2, h - 24);
        }
    }

    private void RecordHistory(string key, double value)
    {
        if (!_history.TryGetValue(key, out var list))
        {
            list = new List<double>();
            _history[key] = list;
        }
        list.Add(value);
        if (list.Count > MaxHistory)
            list.RemoveAt(0);
    }

    private List<double> GetHistory(string key)
        => _history.TryGetValue(key, out var list) ? list : new List<double>();

    public void Dispose() { }
}
