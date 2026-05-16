using System.Drawing;
using System.Drawing.Drawing2D;
using GPULCD.Models;

namespace GPULCD.Rendering.Widgets;

public static class ArcGaugeWidget
{
    public static void Draw(Graphics g, int x, int y, int w, int h,
        string label, SensorReading reading, double maxValue,
        Color foreColor, Color bgColor)
    {
        float percent = (float)Math.Clamp(reading.Value / maxValue, 0, 1);
        float arcWidth = 10f;
        float startAngle = 135f;
        float totalSweep = 270f;
        float sweepAngle = totalSweep * percent;

        var rect = new RectangleF(x + arcWidth / 2, y + arcWidth / 2,
            w - arcWidth, h - arcWidth);

        // Background arc
        using (var bgPen = new Pen(bgColor, arcWidth))
        {
            bgPen.StartCap = LineCap.Round;
            bgPen.EndCap = LineCap.Round;
            g.DrawArc(bgPen, rect, startAngle, totalSweep);
        }

        // Foreground arc
        if (sweepAngle > 0.5f)
        {
            using var fgPen = new Pen(foreColor, arcWidth);
            fgPen.StartCap = LineCap.Round;
            fgPen.EndCap = LineCap.Round;
            g.DrawArc(fgPen, rect, startAngle, sweepAngle);
        }

        // Center value text
        using (var valueFont = new Font("Segoe UI", 18, FontStyle.Bold))
        using (var valueBrush = new SolidBrush(Color.White))
        {
            var valueStr = reading.FormattedValue;
            var valueSize = g.MeasureString(valueStr, valueFont);
            g.DrawString(valueStr, valueFont, valueBrush,
                x + (w - valueSize.Width) / 2,
                y + (h - valueSize.Height) / 2 - 4);
        }

        // Label below value
        using (var labelFont = new Font("Segoe UI", 9, FontStyle.Bold))
        using (var labelBrush = new SolidBrush(Color.FromArgb(150, 155, 170)))
        {
            var labelSize = g.MeasureString(label, labelFont);
            g.DrawString(label, labelFont, labelBrush,
                x + (w - labelSize.Width) / 2,
                y + (h - labelSize.Height) / 2 + 20);
        }
    }
}
