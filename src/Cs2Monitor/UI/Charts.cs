using System.Drawing.Drawing2D;

namespace Cs2Monitor.UI;

internal sealed record ChartSeries(string Name, Color Color, double?[] Values);
internal sealed record ChartBand(double From, double To, Color Color);
internal sealed record ChartMark(double X, string Label);

/// <summary>
/// Line chart over time (x in seconds). Missing values (null) are drawn as gaps.
/// Bands shade time ranges (e.g. a stall); marks draw dashed vertical lines (e.g. key presses).
/// </summary>
internal sealed class TimelineChart : Control
{
    private double[] _x = Array.Empty<double>();
    private IReadOnlyList<ChartSeries> _series = Array.Empty<ChartSeries>();
    private IReadOnlyList<ChartBand> _bands = Array.Empty<ChartBand>();
    private IReadOnlyList<ChartMark> _marks = Array.Empty<ChartMark>();

    public string Unit { get; set; } = "%";
    /// <summary>Fixed y maximum; null = scale to data.</summary>
    public double? FixedMax { get; set; } = 100;
    public double? XMin { get; set; }
    public double? XMax { get; set; }
    public string XLabel { get; set; } = "s";

    public TimelineChart()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Color.FromArgb(24, 26, 30);
        ForeColor = Color.FromArgb(170, 176, 186);
    }

    public void SetData(double[] x, IReadOnlyList<ChartSeries> series, IReadOnlyList<ChartBand>? bands = null, IReadOnlyList<ChartMark>? marks = null)
    {
        _x = x;
        _series = series;
        _bands = bands ?? Array.Empty<ChartBand>();
        _marks = marks ?? Array.Empty<ChartMark>();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var plot = new Rectangle(52, 24, Width - 64, Height - 44);
        if (plot.Width < 20 || plot.Height < 20) return;

        using var grid = new Pen(Color.FromArgb(48, 52, 58));
        using var text = new SolidBrush(ForeColor);

        double xMin = XMin ?? (_x.Length > 0 ? _x[0] : 0);
        double xMax = XMax ?? (_x.Length > 0 ? _x[^1] : 1);
        if (xMax <= xMin) xMax = xMin + 1;
        double yMax = FixedMax ?? NiceMax(_series);

        float X(double v) => plot.Left + (float)((v - xMin) / (xMax - xMin) * plot.Width);
        float Y(double v) => plot.Bottom - (float)(Math.Clamp(v, 0, yMax) / yMax * plot.Height);

        foreach (var b in _bands)
        {
            float x0 = Math.Max(plot.Left, X(b.From)), x1 = Math.Min(plot.Right, X(b.To));
            if (x1 <= x0) continue;
            using var brush = new SolidBrush(b.Color);
            g.FillRectangle(brush, x0, plot.Top, x1 - x0, plot.Height);
        }

        for (int i = 0; i <= 4; i++)
        {
            double v = yMax * i / 4;
            float y = Y(v);
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            string label = (yMax >= 10 ? v.ToString("0") : v.ToString("0.#")) + (Unit == "%" ? "%" : "");
            g.DrawString(label, Font, text, 2, y - Font.Height / 2f);
        }
        if (Unit != "%") g.DrawString(Unit, Font, text, 2, plot.Top - Font.Height - 4);

        double step = NiceStep((xMax - xMin) / 8);
        for (double v = Math.Ceiling(xMin / step) * step; v <= xMax; v += step)
        {
            float x = X(v);
            g.DrawLine(grid, x, plot.Bottom, x, plot.Bottom + 3);
            string label = v.ToString(step < 1 ? "0.0" : "0") + XLabel;
            var size = g.MeasureString(label, Font);
            g.DrawString(label, Font, text, x - size.Width / 2, plot.Bottom + 3);
        }

        float lx = plot.Left;
        foreach (var s in _series)
        {
            using var b = new SolidBrush(s.Color);
            g.FillRectangle(b, lx, 7, 10, 10);
            g.DrawString(s.Name, Font, text, lx + 13, 4);
            lx += 13 + g.MeasureString(s.Name, Font).Width + 12;
        }

        g.SetClip(plot);
        foreach (var series in _series)
        {
            using var pen = new Pen(series.Color, 1.6f);
            PointF? prev = null;
            int n = Math.Min(_x.Length, series.Values.Length);
            for (int i = 0; i < n; i++)
            {
                if (_x[i] < xMin || series.Values[i] is not double v) { prev = null; continue; }
                var p = new PointF(X(_x[i]), Y(v));
                if (prev.HasValue) g.DrawLine(pen, prev.Value, p);
                prev = p;
            }
        }
        g.ResetClip();

        using var markPen = new Pen(Color.FromArgb(230, 200, 60), 1f) { DashStyle = DashStyle.Dash };
        using var markBrush = new SolidBrush(Color.FromArgb(230, 200, 60));
        foreach (var m in _marks)
        {
            if (m.X < xMin || m.X > xMax) continue;
            float x = X(m.X);
            g.DrawLine(markPen, x, plot.Top, x, plot.Bottom);
            g.DrawString(m.Label, Font, markBrush, x + 2, plot.Top + 2);
        }
    }

    private static double NiceMax(IReadOnlyList<ChartSeries> series)
    {
        double max = 0;
        foreach (var s in series)
            foreach (var v in s.Values)
                if (v is double d && double.IsFinite(d)) max = Math.Max(max, d);
        return max <= 0 ? 1 : NiceStep(max * 1.1 / 4) * 4;
    }

    private static double NiceStep(double raw)
    {
        if (raw <= 0) return 1;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double n = raw / mag;
        return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * mag;
    }
}

/// <summary>One bar per logical processor: makes single-core saturation visible at a glance.</summary>
internal sealed class CoreBars : Control
{
    private double?[] _values = Array.Empty<double?>();

    public CoreBars()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Color.FromArgb(24, 26, 30);
        ForeColor = Color.FromArgb(170, 176, 186);
    }

    public void SetData(double?[] values)
    {
        _values = values;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        int n = _values.Length;
        if (n == 0) return;
        int labelH = Font.Height + 2;
        float slot = (float)Width / n;
        float barW = Math.Max(4, slot - 6);
        int maxH = Height - labelH - 4;
        using var text = new SolidBrush(ForeColor);
        using var back = new SolidBrush(Color.FromArgb(40, 44, 50));
        for (int i = 0; i < n; i++)
        {
            float x = i * slot + (slot - barW) / 2;
            g.FillRectangle(back, x, 2, barW, maxH);
            var v = _values[i];
            if (v.HasValue)
            {
                float h = (float)(Math.Clamp(v.Value, 0, 100) / 100 * maxH);
                var color = v >= 90 ? Color.FromArgb(230, 90, 80) : v >= 70 ? Color.FromArgb(230, 170, 60) : Color.FromArgb(80, 160, 230);
                using var b = new SolidBrush(color);
                g.FillRectangle(b, x, 2 + maxH - h, barW, h);
            }
            var label = i.ToString();
            var size = g.MeasureString(label, Font);
            g.DrawString(label, Font, text, x + (barW - size.Width) / 2, Height - labelH);
        }
    }
}
