using System.Drawing;
using System.Drawing.Drawing2D;
using GPULCD.Models;

namespace GPULCD.Rendering.Widgets;

public static class ProgressBarWidget
{
    public static void Draw(Graphics g, int x, int y, int w, int h,
        string label, SensorReading reading, double maxValue,
        Color foreColor, Color bgColor)
    {
        float percent = (float)Math.Clamp(reading.Value / maxValue, 0, 1);
        int cornerRadius = h / 2;

        // Background bar
        using (var bgBrush = new SolidBrush(bgColor))
        {
            var bgPath = RoundedRect(x, y, w, h, cornerRadius);
            g.FillPath(bgBrush, bgPath);
        }

        // Foreground bar
        int fillWidth = Math.Max((int)(w * percent), h); // minimum width = height for rounded ends
        if (percent > 0.01f)
        {
            fillWidth = Math.Min(fillWidth, w);
            using var fgBrush = new SolidBrush(foreColor);
            var fgPath = RoundedRect(x, y, fillWidth, h, cornerRadius);
            g.FillPath(fgBrush, fgPath);
        }

        // Label (left)
        using (var labelFont = new Font("Segoe UI", 9, FontStyle.Bold))
        using (var labelBrush = new SolidBrush(Color.White))
        {
            g.DrawString(label, labelFont, labelBrush, x + 12, y + (h - labelFont.Height) / 2);
        }

        // Value (right)
        using (var valueFont = new Font("Segoe UI", 9, FontStyle.Bold))
        using (var valueBrush = new SolidBrush(Color.White))
        {
            var valueStr = reading.FormattedValue;
            var valueSize = g.MeasureString(valueStr, valueFont);
            g.DrawString(valueStr, valueFont, valueBrush,
                x + w - valueSize.Width - 12,
                y + (h - valueFont.Height) / 2);
        }
    }

    private static GraphicsPath RoundedRect(int x, int y, int w, int h, int r)
    {
        var path = new GraphicsPath();
        r = Math.Min(r, Math.Min(w, h) / 2);
        int d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
