using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Cs2Monitor.Detection;

namespace Cs2Monitor.UI;

/// <summary>Lists saved events (all sessions) and shows one: before/during/after table, observations, timelines.</summary>
internal sealed class EventViewerForm : Form
{
    private readonly string _root;
    private readonly ListView _list = new();
    private readonly TextBox _report = new();
    private readonly TimelineChart _loadChart = new();
    private readonly TimelineChart _frameChart = new();
    private readonly Label _status = new();
    private List<(string Path, EventRecord Record)> _events = new();
    private string? _selectPath;

    private static readonly Color StallBand = Color.FromArgb(70, 200, 60, 60);

    public EventViewerForm(string logRoot, string? selectPath = null)
    {
        _root = logRoot;
        _selectPath = selectPath;
        Text = "CS2 Stall Investigator — Events";
        BackColor = Color.FromArgb(18, 20, 23);
        ForeColor = Color.FromArgb(222, 226, 232);
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(1280, 820);
        StartPosition = FormStartPosition.CenterScreen;

        var split = new SplitContainer { Dock = DockStyle.Fill, BackColor = BackColor };
        Controls.Add(split);
        Load += (_, _) => split.SplitterDistance = 520;

        // Left: list + buttons
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(8) };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        buttons.Controls.Add(Button("Refresh", (_, _) => Reload()));
        buttons.Controls.Add(Button("Open event folder", (_, _) => OpenSelectedFolder()));
        buttons.Controls.Add(Button("Open CSV", (_, _) => OpenSelected(".csv")));
        left.Controls.Add(buttons);

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.BackColor = Color.FromArgb(28, 31, 36);
        _list.ForeColor = ForeColor;
        _list.BorderStyle = BorderStyle.None;
        _list.Columns.Add("#", 55);
        _list.Columns.Add("Start (local)", 135);
        _list.Columns.Add("Type", 70);
        _list.Columns.Add("Duration", 70);
        _list.Columns.Add("Main rule", 170);
        _list.SelectedIndexChanged += (_, _) => ShowSelected();
        left.Controls.Add(_list);

        _status.AutoSize = true;
        _status.ForeColor = Color.FromArgb(140, 146, 156);
        left.Controls.Add(_status);
        split.Panel1.Controls.Add(left);

        // Right: report + charts
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(8) };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 46));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 27));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 27));
        _report.Dock = DockStyle.Fill;
        _report.Multiline = true;
        _report.ReadOnly = true;
        _report.ScrollBars = ScrollBars.Both;
        _report.WordWrap = false;
        _report.Font = new Font("Consolas", 9.5f);
        _report.BackColor = Color.FromArgb(28, 31, 36);
        _report.ForeColor = ForeColor;
        _report.BorderStyle = BorderStyle.None;
        right.Controls.Add(_report);
        _loadChart.Dock = DockStyle.Fill;
        _frameChart.Dock = DockStyle.Fill;
        _frameChart.Unit = "ms";
        _frameChart.FixedMax = null;
        right.Controls.Add(_loadChart);
        right.Controls.Add(_frameChart);
        split.Panel2.Controls.Add(right);

        Shown += (_, _) => Reload();
    }

    public void Reload(string? selectPath = null)
    {
        if (selectPath != null) _selectPath = selectPath;
        _status.Text = "Loading…";
        Task.Run(() => LoadAll(_root)).ContinueWith(t =>
        {
            if (IsDisposed) return;
            _events = t.Result;
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var (path, r) in _events)
            {
                var item = new ListViewItem(r.Id.ToString());
                item.SubItems.Add(r.StartUtc.ToLocalTime().ToString("MM-dd HH:mm:ss"));
                item.SubItems.Add(r.Type + (r.Type == "auto" && r.ConfirmedByUser ? " ✓" : ""));
                item.SubItems.Add(r.Type == "auto" ? $"{r.DurationSec:0.0} s" : "—");
                item.SubItems.Add(r.RulesFired.FirstOrDefault(h => !h.Name.EndsWith("(context)"))?.Name ?? (r.Type == "manual" ? "(manual)" : ""));
                item.Tag = path;
                _list.Items.Add(item);
            }
            _list.EndUpdate();
            _status.Text = $"{_events.Count} events in {_root}";
            var select = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => _selectPath != null && (string)i.Tag! == _selectPath)
                         ?? (_list.Items.Count > 0 ? _list.Items[0] : null);
            if (select != null) { select.Selected = true; select.EnsureVisible(); }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static List<(string, EventRecord)> LoadAll(string root)
    {
        var result = new List<(string, EventRecord)>();
        if (!Directory.Exists(root)) return result;
        foreach (var f in Directory.EnumerateFiles(root, "CS2_Event_*.json", SearchOption.AllDirectories))
        {
            try
            {
                var r = JsonSerializer.Deserialize<EventRecord>(File.ReadAllText(f));
                if (r != null) result.Add((f, r));
            }
            catch (Exception) { /* partially written or foreign file */ }
        }
        return result.OrderByDescending(e => e.Item2.Id).ToList();
    }

    private (string Path, EventRecord Record)? Selected()
    {
        if (_list.SelectedItems.Count == 0) return null;
        var path = (string)_list.SelectedItems[0].Tag!;
        var match = _events.FirstOrDefault(e => e.Path == path);
        return match.Record == null ? null : match;
    }

    private void ShowSelected()
    {
        if (Selected() is not { } sel) return;
        var r = sel.Record;
        _report.Text = BuildReport(r, sel.Path);

        var t = r.Timeline;
        var x = t.TSec.ToArray();
        double?[] S(string key) => t.Series.TryGetValue(key, out var v) ? v.ToArray() : new double?[x.Length];

        var bands = new List<ChartBand>();
        double? bandStart = null;
        for (int i = 0; i < x.Length; i++)
        {
            bool during = t.Period[i] == "during";
            if (during && bandStart == null) bandStart = x[i];
            if (!during && bandStart != null) { bands.Add(new ChartBand(bandStart.Value, x[i], StallBand)); bandStart = null; }
        }
        if (bandStart != null && x.Length > 0) bands.Add(new ChartBand(bandStart.Value, x[^1], StallBand));

        var marks = new List<ChartMark>();
        for (int i = 0; i < x.Length; i++)
            if (t.Markers[i] != null) marks.Add(new ChartMark(x[i], "key"));

        _loadChart.SetData(x, new[]
        {
            new ChartSeries("GPU load", Color.FromArgb(110, 200, 120), S("gpu_util")),
            new ChartSeries("System CPU", Color.FromArgb(80, 160, 230), S("cpu_busy")),
            new ChartSeries("Busiest core", Color.FromArgb(150, 110, 230), S("max_core_busy")),
            new ChartSeries("Game main thread", Color.FromArgb(235, 130, 60), S("main_thread_pct")),
            new ChartSeries("Game busiest thread", Color.FromArgb(235, 90, 110), S("top_thread_pct")),
        }, bands, marks);
        _frameChart.SetData(x, new[]
        {
            new ChartSeries("Frame time avg", Color.FromArgb(230, 200, 60), S("frametime_avg_ms")),
            new ChartSeries("Frame time max", Color.FromArgb(200, 120, 60), S("frametime_max_ms")),
            new ChartSeries("Frame CPU busy", Color.FromArgb(80, 160, 230), S("frame_cpu_busy_ms")),
            new ChartSeries("Frame GPU busy", Color.FromArgb(110, 200, 120), S("frame_gpu_busy_ms")),
        }, bands, marks);
    }

    private static string BuildReport(EventRecord r, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CS2 PERFORMANCE EVENT #{r.Id}   ({r.Type}{(r.Type == "auto" && r.ConfirmedByUser ? ", confirmed by you" : "")})");
        sb.AppendLine();
        sb.AppendLine($"Start:        {r.StartUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss.f}");
        if (r.Type == "auto") sb.AppendLine($"Duration:     {r.DurationSec:0.00} s{(r.Truncated ? "  (cut off: " + r.EndReason + ")" : "")}");
        sb.AppendLine($"Population:   {(r.Population?.ToString("N0") ?? "not measured (needs CS2 mod, Phase 3)")}");
        sb.AppendLine($"Simulation:   {r.SimulationState}");
        sb.AppendLine($"Frame timing: {r.FrameTiming}");
        sb.AppendLine($"Game version: {r.GameVersion ?? "?"}");
        sb.AppendLine();
        sb.AppendLine($"{"",-26}{"Before",12}{"During",12}{"After",12}");
        foreach (var m in r.Summary)
            sb.AppendLine($"{m.Label,-26}{Cell(m.Before, m.Unit),12}{Cell(m.During, m.Unit),12}{Cell(m.After, m.Unit),12}");
        sb.AppendLine();
        if (r.RulesFired.Count > 0)
            sb.AppendLine("Rules fired (samples):  " + string.Join(", ", r.RulesFired.Select(h => $"{h.Name} ({h.Samples})")));
        sb.AppendLine();
        sb.AppendLine("Observations (measured changes, not conclusions):");
        foreach (var o in r.Observations) sb.AppendLine("  • " + o);
        sb.AppendLine();
        if (r.OtherProcesses.Count > 0)
        {
            sb.AppendLine($"{"Other processes (CPU cores)",-34}{"Before",8}{"During",8}{"After",8}");
            foreach (var p in r.OtherProcesses)
                sb.AppendLine($"  {Trim(p.Name, 32),-32}{p.BeforeCores,8:0.00}{p.DuringCores,8:0.00}{p.AfterCores,8:0.00}");
            sb.AppendLine();
        }
        sb.AppendLine($"Classification: {r.Classification}");
        sb.AppendLine($"  {r.ClassificationNote}");
        sb.AppendLine();
        sb.AppendLine("Periods: " + r.PeriodDefinition);
        sb.AppendLine("File:    " + path);
        return sb.ToString();
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string Cell(double? v, string unit) => v is double d ? EventWriter.Fmt(d, unit) : "—";

    private void OpenSelectedFolder()
    {
        if (Selected() is { } sel) Open(Path.GetDirectoryName(sel.Path)!);
        else if (Directory.Exists(_root)) Open(_root);
    }

    private void OpenSelected(string extension)
    {
        if (Selected() is { } sel) Open(Path.ChangeExtension(sel.Path, extension));
    }

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception e) { MessageBox.Show(e.Message, "Could not open", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private static Button Button(string text, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(44, 48, 55),
            ForeColor = Color.FromArgb(222, 226, 232), Margin = new Padding(0, 0, 6, 6),
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(70, 75, 85);
        b.Click += onClick;
        return b;
    }
}
