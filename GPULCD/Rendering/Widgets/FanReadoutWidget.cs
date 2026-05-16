using System.Drawing;
using GPULCD.Models;

namespace GPULCD.Rendering.Widgets;

public static class FanReadoutWidget
{
    public static void Draw(Graphics g, int x, int y, int w,
        string label, SensorReading reading, Color labelColor)
    {
        using var labelFont = new Font("Segoe UI", 8, FontStyle.Bold);
        using var valueFont = new Font("Segoe UI", 8);
        using var labelBrush = new SolidBrush(labelColor);
        using var valueBrush = new SolidBrush(Color.FromArgb(180, 185, 195));

        string rpm = reading.Value > 0 ? $"{reading.Value:F0}" : "OFF";

        var labelSize = g.MeasureString(label, labelFont);
        g.DrawString(label, labelFont, labelBrush, x, y);

        string display = $"{rpm} RPM";
        var valSize = g.MeasureString(display, valueFont);
        g.DrawString(display, valueFont, valueBrush, x + w - valSize.Width, y);
    }
}
