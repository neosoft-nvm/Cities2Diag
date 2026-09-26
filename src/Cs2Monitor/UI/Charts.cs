using System.Drawing.Drawing2D;

namespace Cs2Monitor.UI;

internal sealed record ChartSeries(string Name, Color Color, Func<Sample, double?> Value);

/// <summary>Scrolling 0–100 % line chart of the in-memory history. Gaps are drawn where a value is missing.</summary>
internal sealed class MiniChart : Control
{
    private Sample[] _samples = Array.Empty<Sample>();
    public List<ChartSeries> Series { get; } = new();
    public double WindowMs { get; set; } = 120_000;

    public MiniChart()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Color.FromArgb(24, 26, 30);
        ForeColor = Color.FromArgb(170, 176, 186);
    }

    public void SetData(Sample[] samples)
    {
        _samples = samples;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var plot = new Rectangle(40, 22, Width - 50, Height - 40);
        if (plot.Width < 20 || plot.Height < 20) return;

        using var grid = new Pen(Color.FromArgb(48, 52, 58));
        using var text = new SolidBrush(ForeColor);
        foreach (int pct in new[] { 0, 25, 50, 75, 100 })
        {
            int y = plot.Bottom - pct * plot.Height / 100;
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            g.DrawString(pct + "%", Font, text, 2, y - Font.Height / 2);
        }

        // Legend
        float lx = plot.Left;
        foreach (var s in Series)
        {
            using var b = new SolidBrush(s.Color);
            g.FillRectangle(b, lx, 6, 10, 10);
            g.DrawString(s.Name, Font, text, lx + 13, 3);
            lx += 13 + g.MeasureString(s.Name, Font).Width + 12;
        }

        if (_samples.Length < 2) return;
        double tEnd = _samples[^1].TMs;
        double tStart = tEnd - WindowMs;
        g.DrawString($"last {WindowMs / 1000:0} s", Font, text, plot.Left, plot.Bottom + 2);

        foreach (var series in Series)
        {
            using var pen = new Pen(series.Color, 1.6f);
            PointF? prev = null;
            foreach (var s in _samples)
            {
                if (s.TMs < tStart) continue;
                var v = series.Value(s);
                if (v == null) { prev = null; continue; }
                float x = plot.Left + (float)((s.TMs - tStart) / WindowMs * plot.Width);
                float y = plot.Bottom - (float)(Math.Clamp(v.Value, 0, 100) / 100 * plot.Height);
                var p = new PointF(x, y);
                if (prev.HasValue) g.DrawLine(pen, prev.Value, p);
                prev = p;
            }
        }

        // Manual markers as vertical lines
        using var markerPen = new Pen(Color.FromArgb(230, 200, 60), 1f) { DashStyle = DashStyle.Dash };
        foreach (var s in _samples)
        {
            if (s.Marker == null || s.TMs < tStart) continue;
            float x = plot.Left + (float)((s.TMs - tStart) / WindowMs * plot.Width);
            g.DrawLine(markerPen, x, plot.Top, x, plot.Bottom);
        }
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
