using System.Drawing;
using System.Drawing.Drawing2D;
using GPULCD.Models;

namespace GPULCD.Rendering.Widgets;

public static class FanBoxWidget
{
    public static void Draw(Graphics g, int x, int y, int size,
        string label, SensorReading reading, double maxRpm,
        Color accentColor, Color bgColor)
    {
        float percent = (float)Math.Clamp(reading.Value / maxRpm, 0, 1);
        bool isOff = reading.Value <= 0;

        // Background rounded box
        using (var bgBrush = new SolidBrush(bgColor))
        {
            var path = RoundedRect(x, y, size, size, 8);
            g.FillPath(bgBrush, path);
        }

        // Mini arc gauge
        float arcWidth = 5f;
        float padding = 8f;
        var arcRect = new RectangleF(
            x + padding, y + padding,
            size - padding * 2, size - padding * 2);
        float startAngle = 135f;
        float totalSweep = 270f;

        // Background arc
        using (var pen = new Pen(Color.FromArgb(25, 30, 40), arcWidth))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            g.DrawArc(pen, arcRect, startAngle, totalSweep);
        }

        // Foreground arc
        if (!isOff && percent > 0.01f)
        {
            using var pen = new Pen(accentColor, arcWidth);
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            g.DrawArc(pen, arcRect, startAngle, totalSweep * percent);
        }

        // RPM value center
        string rpm = isOff ? "OFF" : $"{reading.Value:F0}";
        using (var font = new Font("Segoe UI", size > 80 ? 12 : 10, FontStyle.Bold))
        using (var brush = new SolidBrush(isOff ? Color.FromArgb(70, 75, 85) : Color.White))
        {
            var valSize = g.MeasureString(rpm, font);
            g.DrawString(rpm, font, brush,
                x + (size - valSize.Width) / 2,
                y + (size - valSize.Height) / 2 - 6);
        }

        // Label below value
        using (var font = new Font("Segoe UI", 7, FontStyle.Bold))
        using (var brush = new SolidBrush(Color.FromArgb(120, 125, 140)))
        {
            var lblSize = g.MeasureString(label, font);
            g.DrawString(label, font, brush,
                x + (size - lblSize.Width) / 2,
                y + (size - lblSize.Height) / 2 + 10);
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
}
