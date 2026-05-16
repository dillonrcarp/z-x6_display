using System.Drawing;
using System.Drawing.Drawing2D;

namespace GPULCD.Rendering.Widgets;

public static class SparklineWidget
{
    /// <summary>
    /// Auto-scaling sparkline. The Y-axis zooms to fit the actual data range
    /// with padding, so small variations are clearly visible.
    /// </summary>
    public static void Draw(Graphics g, int x, int y, int w, int h,
        List<double> history, double floorMin, double ceilMax,
        Color lineColor, Color fillColor)
    {
        if (history.Count < 2) return;

        // Downsample to pixel width if we have more points than pixels
        var data = history;
        if (history.Count > w)
        {
            data = new List<double>(w);
            double bucketSize = (double)history.Count / w;
            for (int i = 0; i < w; i++)
            {
                int start = (int)(i * bucketSize);
                int end = (int)((i + 1) * bucketSize);
                end = Math.Min(end, history.Count);
                // Use max within each bucket so spikes aren't hidden
                double val = double.MinValue;
                for (int j = start; j < end; j++)
                    val = Math.Max(val, history[j]);
                if (val == double.MinValue) val = history[start];
                data.Add(val);
            }
        }

        // Auto-scale: use actual data range with padding
        double dataMin = data.Min();
        double dataMax = data.Max();
        double dataRange = dataMax - dataMin;

        // Add padding so the line doesn't hug the edges
        double padding = Math.Max(dataRange * 0.3, 2.0);
        double viewMin = Math.Max(floorMin, dataMin - padding);
        double viewMax = Math.Min(ceilMax, dataMax + padding);

        // Ensure minimum visible range so flat lines sit in the middle
        double viewRange = viewMax - viewMin;
        if (viewRange < 5.0)
        {
            double center = (dataMin + dataMax) / 2;
            viewMin = Math.Max(floorMin, center - 2.5);
            viewMax = Math.Min(ceilMax, center + 2.5);
            viewRange = viewMax - viewMin;
        }

        // Build point array
        var points = new PointF[data.Count];
        float step = (float)w / (data.Count - 1);

        for (int i = 0; i < data.Count; i++)
        {
            float px = x + i * step;
            float normalized = (float)((data[i] - viewMin) / viewRange);
            normalized = Math.Clamp(normalized, 0f, 1f);
            float py = y + h - (normalized * h);
            points[i] = new PointF(px, py);
        }

        // Fill area under the line
        using (var fillPath = new GraphicsPath())
        {
            fillPath.AddLines(points);
            fillPath.AddLine(points[^1].X, points[^1].Y, x + w, y + h);
            fillPath.AddLine(x + w, y + h, x, y + h);
            fillPath.CloseFigure();

            using var brush = new LinearGradientBrush(
                new Rectangle(x, y, w, h),
                Color.FromArgb(50, fillColor),
                Color.FromArgb(5, fillColor),
                LinearGradientMode.Vertical);
            g.FillPath(brush, fillPath);
        }

        // Draw line
        using (var pen = new Pen(lineColor, 1.5f))
        {
            pen.LineJoin = LineJoin.Round;
            g.DrawLines(pen, points);
        }

        // Current value dot at the end
        var last = points[^1];
        using (var dotBrush = new SolidBrush(lineColor))
        {
            g.FillEllipse(dotBrush, last.X - 2.5f, last.Y - 2.5f, 5, 5);
        }
    }
}
