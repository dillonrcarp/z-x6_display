using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using GPULCD.Models;
using GPULCD.Services;

namespace GPULCD.Rendering.Pages;

public class AlertsPage : IPage
{
    private readonly ISensorService _sensors;
    private readonly AppSettings _settings;
    private readonly Dictionary<string, AlertThreshold> _thresholds;
    private List<ActiveAlert> _alerts = new();

    private static readonly Dictionary<string, string> SlotLabels = new()
    {
        ["CpuTemp"] = "CPU Temp",
        ["GpuTemp"] = "GPU Temp",
        ["RamUsage"] = "RAM Usage",
        ["CpuFan"] = "Radiator Fan",
    };

    public string Name => "Alerts";
    public int PreferredIntervalMs => 1000;
    public bool IsActive => _alerts.Count > 0; // stay visible while alerts are active
    public bool ShouldSkip => _alerts.Count == 0;

    public AlertsPage(ISensorService sensors, AppSettings settings)
    {
        _sensors = sensors;
        _settings = settings;
        _thresholds = new Dictionary<string, AlertThreshold>
        {
            ["CpuTemp"] = new() { WarnAt = 80, CritAt = 90 },
            ["GpuTemp"] = new() { WarnAt = 75, CritAt = 85 },
            ["RamUsage"] = new() { WarnAt = 85, CritAt = 95 },
            ["CpuFan"] = new() { MinRpm = 200 },
        };
    }

    public void Update()
    {
        var allReadings = _sensors.GetCurrentReadings();
        var mapped = new Dictionary<string, SensorReading>();

        bool IsFanSlot(string slot) => slot.EndsWith("Fan", StringComparison.OrdinalIgnoreCase);

        foreach (var (slot, sensorName) in _settings.SensorMappings)
        {
            var candidates = allReadings.Where(kvp =>
                kvp.Key.Contains(sensorName, StringComparison.OrdinalIgnoreCase)).ToList();

            if (candidates.Count == 0) continue;

            if (IsFanSlot(slot))
            {
                var rpmMatch = candidates.FirstOrDefault(kvp =>
                    kvp.Value.Unit.Equals("RPM", StringComparison.OrdinalIgnoreCase));
                if (rpmMatch.Value != null)
                {
                    mapped[slot] = rpmMatch.Value;
                    continue;
                }
            }

            if (allReadings.TryGetValue(sensorName, out var exact))
                mapped[slot] = exact;
            else
                mapped[slot] = candidates[0].Value;
        }

        _alerts = new List<ActiveAlert>();
        foreach (var (slot, threshold) in _thresholds)
        {
            if (!mapped.TryGetValue(slot, out var reading)) continue;
            var level = threshold.Check(reading.Value);
            if (level != AlertLevel.Ok)
            {
                double thresh = level == AlertLevel.Critical
                    ? (threshold.CritAt ?? threshold.MinRpm ?? 0)
                    : (threshold.WarnAt ?? 0);
                var label = SlotLabels.GetValueOrDefault(slot, reading.Name);
                _alerts.Add(new ActiveAlert(slot, label, reading.Value, reading.Unit, level, thresh));
            }
        }
    }

    public Bitmap Render(int width, int height)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.FromArgb(15, 15, 20));

        // Header
        using (var font = new Font("Segoe UI", 11, FontStyle.Bold))
        using (var brush = new SolidBrush(Color.FromArgb(200, 200, 210)))
        {
            var text = "ALERTS";
            var size = g.MeasureString(text, font);
            g.DrawString(text, font, brush, (width - size.Width) / 2, 6);
        }
        using (var pen = new Pen(Color.FromArgb(50, 55, 70), 1))
            g.DrawLine(pen, 16, 30, width - 16, 30);

        if (_alerts.Count == 0)
        {
            RenderAllClear(g, width, height);
        }
        else
        {
            RenderAlerts(g, width, height);
        }

        return bmp;
    }

    private void RenderAllClear(Graphics g, int w, int h)
    {
        var green = Color.FromArgb(80, 200, 120);

        // Large checkmark circle
        int circleSize = 80;
        int cx = (w - circleSize) / 2;
        int cy = 60;

        using (var brush = new SolidBrush(Color.FromArgb(20, green)))
        {
            g.FillEllipse(brush, cx, cy, circleSize, circleSize);
        }
        using (var pen = new Pen(green, 3))
        {
            g.DrawEllipse(pen, cx, cy, circleSize, circleSize);
            // Checkmark
            int mx = cx + circleSize / 2;
            int my = cy + circleSize / 2;
            g.DrawLine(pen, mx - 18, my, mx - 6, my + 14);
            g.DrawLine(pen, mx - 6, my + 14, mx + 20, my - 12);
        }

        using (var font = new Font("Segoe UI", 16, FontStyle.Bold))
        using (var brush = new SolidBrush(green))
        {
            var text = "ALL CLEAR";
            var size = g.MeasureString(text, font);
            g.DrawString(text, font, brush, (w - size.Width) / 2, cy + circleSize + 16);
        }

        using (var font = new Font("Segoe UI", 9))
        using (var brush = new SolidBrush(Color.FromArgb(100, 105, 120)))
        {
            var text = "All systems nominal";
            var size = g.MeasureString(text, font);
            g.DrawString(text, font, brush, (w - size.Width) / 2, cy + circleSize + 46);
        }

        // Timestamp
        using (var font = new Font("Segoe UI", 7))
        using (var brush = new SolidBrush(Color.FromArgb(60, 65, 75)))
        {
            var time = DateTime.Now.ToString("HH:mm:ss");
            var size = g.MeasureString(time, font);
            g.DrawString(time, font, brush, w - size.Width - 12, h - 16);
        }
    }

    private void RenderAlerts(Graphics g, int w, int h)
    {
        int y = 40;
        int margin = 16;
        int cardH = 52;
        int gap = 8;

        foreach (var alert in _alerts)
        {
            if (y + cardH > h - 20) break;

            var color = alert.Level == AlertLevel.Critical
                ? Color.FromArgb(220, 60, 60)
                : Color.FromArgb(255, 180, 40);
            var bgColor = alert.Level == AlertLevel.Critical
                ? Color.FromArgb(40, 20, 20)
                : Color.FromArgb(40, 35, 15);

            // Card background
            using (var brush = new SolidBrush(bgColor))
            {
                var path = RoundedRect(margin, y, w - margin * 2, cardH, 8);
                g.FillPath(brush, path);
            }

            // Left accent bar
            using (var brush = new SolidBrush(color))
                g.FillRectangle(brush, margin, y + 4, 4, cardH - 8);

            // Alert icon
            string icon = alert.Level == AlertLevel.Critical ? "!!" : "!";
            using (var font = new Font("Segoe UI", 14, FontStyle.Bold))
            using (var brush = new SolidBrush(color))
                g.DrawString(icon, font, brush, margin + 12, y + 12);

            // Sensor name
            using (var font = new Font("Segoe UI", 10, FontStyle.Bold))
            using (var brush = new SolidBrush(Color.White))
                g.DrawString(alert.DisplayName, font, brush, margin + 44, y + 6);

            // Value and threshold
            string detail = alert.Level == AlertLevel.Critical
                ? $"{alert.Value:F0}{alert.Unit} (crit: {alert.Threshold:F0})"
                : $"{alert.Value:F0}{alert.Unit} (warn: {alert.Threshold:F0})";
            using (var font = new Font("Segoe UI", 8))
            using (var brush = new SolidBrush(Color.FromArgb(180, 185, 195)))
                g.DrawString(detail, font, brush, margin + 44, y + 28);

            y += cardH + gap;
        }

        // Timestamp
        using (var font = new Font("Segoe UI", 7))
        using (var brush = new SolidBrush(Color.FromArgb(60, 65, 75)))
        {
            var time = DateTime.Now.ToString("HH:mm:ss");
            var size = g.MeasureString(time, font);
            g.DrawString(time, font, brush, w - size.Width - 12, h - 16);
        }
    }

    private static GraphicsPath RoundedRect(int x, int y, int w, int h, int r)
    {
        var path = new GraphicsPath();
        int d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose() { }
}
