using System.Diagnostics;
using System.Media;
using Cs2Monitor.Collectors;
using Cs2Monitor.Native;

namespace Cs2Monitor.UI;

internal sealed class MainForm : Form
{
    private const int HotkeyId = 0xC520;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;

    private readonly MonitorSettings _settings;
    private readonly Sampler _sampler;
    private readonly System.Windows.Forms.Timer _uiTimer = new();
    private readonly Dictionary<string, Label> _values = new();

    private readonly Label _status = new();
    private readonly Label _footer = new();
    private readonly MiniChart _chart = new();
    private readonly CoreBars _cores = new();
    private readonly TableLayoutPanel _gpuTable = NewValueTable();
    private int _markerCount;
    private bool _hotkeyRegistered;
    private string[] _gpuNames = Array.Empty<string>();

    private static readonly Color Bg = Color.FromArgb(18, 20, 23);
    private static readonly Color Panel = Color.FromArgb(28, 31, 36);
    private static readonly Color Fg = Color.FromArgb(222, 226, 232);
    private static readonly Color Dim = Color.FromArgb(140, 146, 156);
    private static readonly Color Good = Color.FromArgb(110, 200, 120);
    private static readonly Color Warn = Color.FromArgb(235, 180, 70);
    private static readonly Color Bad = Color.FromArgb(235, 95, 85);

    public MainForm(MonitorSettings settings, Sampler sampler)
    {
        _settings = settings;
        _sampler = sampler;

        Text = "CS2 Stall Investigator — Monitor (Phase 1)";
        BackColor = Bg;
        ForeColor = Fg;
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(1080, 780);
        MinimumSize = new Size(900, 640);
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 210));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        _status.AutoSize = true;
        _status.Font = new Font("Segoe UI Semibold", 14f);
        _status.Margin = new Padding(0, 0, 0, 8);
        root.Controls.Add(_status);

        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        for (int i = 0; i < 3; i++) columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        columns.Controls.Add(MakeGroup("Cities2.exe", new[]
        {
            "Process", "Version", "CPU (cores busy)", "CPU (% of PC)", "Main thread", "Busiest thread", "2nd busiest",
            "Threads ≥ 90%", "Thread count", "Working set", "Private bytes", "Page faults/s", "Disk I/O", "GPU 3D (game)", "VRAM (game)", "Responding",
        }), 0, 0);
        columns.Controls.Add(MakeGroup("System", new[]
        {
            "CPU busy", "CPU utility", "CPU clock", "Busiest core", "RAM used", "Memory load", "Commit", "Pagefile use",
            "Hard faults (pages in/s)", "Disk", "Disk queue",
        }), 1, 0);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 115));
        right.Controls.Add(WrapGroup("GPU", _gpuTable), 0, 0);
        right.Controls.Add(MakeGroup("Not measured yet", new[] { "FPS / frame time", "Population", "Simulation speed" }), 0, 1);
        columns.Controls.Add(right, 2, 0);
        root.Controls.Add(columns);

        _cores.Dock = DockStyle.Fill;
        _cores.Margin = new Padding(3, 6, 3, 6);
        root.Controls.Add(WrapGroup("Logical processors (% busy)", _cores));

        _chart.Dock = DockStyle.Fill;
        _chart.WindowMs = settings.HistorySeconds * 1000.0;
        _chart.Series.Add(new ChartSeries("System CPU", Color.FromArgb(80, 160, 230), s => s.CpuBusyPct));
        _chart.Series.Add(new ChartSeries("Busiest core", Color.FromArgb(150, 110, 230), s => s.MaxCoreBusyPct));
        _chart.Series.Add(new ChartSeries("CS2 main thread", Color.FromArgb(235, 130, 60), s => s.MainThreadPct));
        _chart.Series.Add(new ChartSeries("CS2 busiest thread", Color.FromArgb(235, 90, 110), s => s.TopThreadPct));
        _chart.Series.Add(new ChartSeries("GPU (NVML)", Color.FromArgb(110, 200, 120), s => s.Gpus.Length > 0 ? s.Gpus.Max(g => g.UtilPct ?? 0) : s.GpuEngineMaxPct));
        root.Controls.Add(_chart);

        var bottom = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0), WrapContents = false };
        var mark = MakeButton("Mark moment  (Ctrl+Alt+M)", (_, _) => AddMarker("button"));
        var open = MakeButton("Open logs folder", (_, _) => OpenLogs());
        var settingsBtn = MakeButton("Open settings file", (_, _) => OpenPath(MonitorSettings.FilePath));
        _footer.AutoSize = true;
        _footer.ForeColor = Dim;
        _footer.Margin = new Padding(12, 8, 0, 0);
        bottom.Controls.AddRange(new Control[] { mark, open, settingsBtn, _footer });
        root.Controls.Add(bottom);

        SetValue("FPS / frame time", "Phase 2 (PresentMon)", Dim);
        SetValue("Population", "Phase 3 (CS2 mod)", Dim);
        SetValue("Simulation speed", "Phase 3 (CS2 mod)", Dim);

        _uiTimer.Interval = settings.UiRefreshMs;
        _uiTimer.Tick += (_, _) => RefreshUi();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _hotkeyRegistered = Win32.RegisterHotKey(Handle, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.M);
        _uiTimer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _uiTimer.Stop();
        if (_hotkeyRegistered) Win32.UnregisterHotKey(Handle, HotkeyId);
        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam == HotkeyId) AddMarker("hotkey");
        base.WndProc(ref m);
    }

    private void AddMarker(string source)
    {
        _markerCount++;
        _sampler.AddMarker($"manual #{_markerCount} ({source})");
        SystemSounds.Asterisk.Play(); // audible confirmation while the game has focus
    }

    private void RefreshUi()
    {
        if (_gpuNames.Length == 0 && _sampler.GpuNames.Length > 0) BuildGpuRows(_sampler.GpuNames);

        var s = _sampler.Latest;
        if (s == null)
        {
            _status.Text = _sampler.Error ?? "Starting…";
            return;
        }

        if (s.GameRunning)
        {
            _status.Text = $"● CS2 running (PID {s.Pid}) — recording";
            _status.ForeColor = Good;
        }
        else
        {
            _status.Text = $"○ Waiting for {_settings.ProcessName}.exe — system values only, not recording";
            _status.ForeColor = Warn;
        }

        // Game
        SetValue("Process", s.GameRunning ? $"PID {s.Pid}" : "not running");
        SetValue("Version", s.GameRunning ? _sampler.GameVersion ?? "?" : "—");
        SetValue("CPU (cores busy)", s.ProcCoresBusy is double c ? $"{c:0.00} of {Environment.ProcessorCount}" : "—");
        SetValue("CPU (% of PC)", Pct(s.ProcCpuPct));
        SetValue("Main thread", Pct(s.MainThreadPct) + (s.MainThreadPct.HasValue ? " of one core" : ""), Level(s.MainThreadPct));
        SetValue("Busiest thread", s.TopThreadPct is double tp ? $"{tp:0}%  {s.TopThreadName ?? "tid " + s.TopThreadId}" : "—", Level(s.TopThreadPct));
        SetValue("2nd busiest", Pct(s.SecondThreadPct), Level(s.SecondThreadPct));
        SetValue("Threads ≥ 90%", s.ThreadsOver90?.ToString() ?? "—");
        SetValue("Thread count", s.ThreadCount?.ToString() ?? "—");
        SetValue("Working set", Gb(s.ProcWorkingSetMb));
        SetValue("Private bytes", Gb(s.ProcPrivateMb));
        SetValue("Page faults/s", s.ProcPageFaultsPerSec is double pf ? $"{pf:N0}" : "—");
        SetValue("Disk I/O", s.ProcIoReadMbS.HasValue ? $"R {s.ProcIoReadMbS:0.0}  W {s.ProcIoWriteMbS:0.0} MB/s" : "—");
        SetValue("GPU 3D (game)", Pct(s.ProcGpu3dPct));
        SetValue("VRAM (game)", Gb(s.ProcVramDedicatedMb) + (s.ProcVramSharedMb is double sh ? $"  (+{sh / 1024:0.0} GB shared)" : ""));
        SetValue("Responding", s.Hung switch { true => "NOT RESPONDING", false => "yes", null => "—" }, s.Hung == true ? Bad : null);

        // System
        SetValue("CPU busy", Pct(s.CpuBusyPct), Level(s.CpuBusyPct));
        SetValue("CPU utility", Pct(s.CpuUtilityPct) + (s.CpuUtilityPct.HasValue ? "  (Task Manager)" : ""));
        SetValue("CPU clock", s.CpuFreqMhz is double f ? $"{f / 1000:0.00} GHz" : "—");
        SetValue("Busiest core", Pct(s.MaxCoreBusyPct), Level(s.MaxCoreBusyPct));
        SetValue("RAM used", s.RamTotalMb.HasValue && s.RamAvailMb.HasValue
            ? $"{(s.RamTotalMb - s.RamAvailMb) / 1024:0.0} / {s.RamTotalMb / 1024:0.0} GB" : "—");
        SetValue("Memory load", Pct(s.MemoryLoadPct), Level(s.MemoryLoadPct));
        SetValue("Commit", s.CommitUsedMb.HasValue ? $"{s.CommitUsedMb / 1024:0.0} / {s.CommitLimitMb / 1024:0.0} GB" : "—");
        SetValue("Pagefile use", Pct(s.PagefileUsagePct));
        SetValue("Hard faults (pages in/s)", s.HardFaultPagesInPerSec is double hf ? $"{hf:N0}" : "—", s.HardFaultPagesInPerSec > 1000 ? Warn : null);
        SetValue("Disk", s.DiskReadMbS.HasValue ? $"R {s.DiskReadMbS:0.0}  W {s.DiskWriteMbS:0.0} MB/s" : "—");
        SetValue("Disk queue", s.DiskQueue is double dq ? $"{dq:0.00}" : "—");

        // GPU
        SetValue("All-process GPU", Pct(s.GpuEngineMaxPct) + (s.GpuEngineMaxPct.HasValue ? "  (Windows)" : ""));
        for (int i = 0; i < s.Gpus.Length && i < _gpuNames.Length; i++)
        {
            var g = s.Gpus[i];
            SetValue($"nv{i} load", Pct(g.UtilPct) + (g.MemCtrlUtilPct is double mu ? $"   mem ctrl {mu:0}%" : ""), Level(g.UtilPct));
            SetValue($"nv{i} VRAM", g.VramUsedMb.HasValue ? $"{g.VramUsedMb / 1024:0.00} / {g.VramTotalMb / 1024:0.0} GB" : "—",
                g.VramUsedMb / g.VramTotalMb > 0.92 ? Warn : null);
            SetValue($"nv{i} temp/clock", $"{g.TempC?.ToString("0") ?? "?"} °C   {g.CoreClockMhz?.ToString("0") ?? "?"} MHz");
            SetValue($"nv{i} power/limits", $"{(g.PowerW is double p ? p.ToString("0") + " W" : "?")}   {(g.ThrottleReasons is ulong r ? NvmlCollector.DescribeThrottle(r) : "?")}");
        }

        _cores.SetData(s.CoreBusyPct);
        _chart.SetData(_sampler.GetHistory());

        double rate = 1000.0 / Math.Max(1, s.IntervalMs);
        _footer.Text = $"{rate:0.0} samples/s · cost {s.CostMs:0.0} ms/sample · markers {_markerCount}"
            + (_hotkeyRegistered ? "" : " · hotkey unavailable (in use by another app)")
            + (_sampler.Error != null ? " · " + _sampler.Error : "")
            + (_sampler.SessionDirectory != null ? "\nLogging to " + _sampler.SessionDirectory : "");
    }

    private void BuildGpuRows(string[] names)
    {
        _gpuNames = names;
        _gpuTable.SuspendLayout();
        AddRow(_gpuTable, "All-process GPU");
        for (int i = 0; i < names.Length; i++)
        {
            var header = new Label { Text = $"nv{i}: {names[i]}", AutoSize = true, ForeColor = Fg, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 8, 0, 2) };
            _gpuTable.Controls.Add(header);
            _gpuTable.SetColumnSpan(header, 2);
            AddRow(_gpuTable, $"nv{i} load", "Load");
            AddRow(_gpuTable, $"nv{i} VRAM", "VRAM");
            AddRow(_gpuTable, $"nv{i} temp/clock", "Temp / clock");
            AddRow(_gpuTable, $"nv{i} power/limits", "Power / limits");
        }
        if (names.Length == 0 && _sampler.NvmlError != null)
        {
            var note = new Label { Text = "NVML: " + _sampler.NvmlError, AutoSize = true, ForeColor = Dim };
            _gpuTable.Controls.Add(note);
            _gpuTable.SetColumnSpan(note, 2);
        }
        _gpuTable.ResumeLayout();
    }

    // ---------- helpers ----------

    private static string Pct(double? v) => v is double d ? $"{d:0}%" : "—";
    private static string Gb(double? mb) => mb is double d ? $"{d / 1024:0.00} GB" : "—";
    private static Color? Level(double? pct) => pct >= 90 ? Bad : pct >= 75 ? Warn : null;

    private void SetValue(string key, string text, Color? color = null)
    {
        if (!_values.TryGetValue(key, out var label)) return;
        if (label.Text != text) label.Text = text;
        label.ForeColor = color ?? Fg;
    }

    private static TableLayoutPanel NewValueTable()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Margin = Padding.Empty };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    private void AddRow(TableLayoutPanel table, string key, string? caption = null)
    {
        table.Controls.Add(new Label { Text = caption ?? key, AutoSize = true, ForeColor = Dim, Margin = new Padding(0, 3, 10, 3) });
        var value = new Label { Text = "—", AutoSize = true, ForeColor = Fg, Margin = new Padding(0, 3, 0, 3) };
        table.Controls.Add(value);
        _values[key] = value;
    }

    private Control MakeGroup(string title, string[] rows)
    {
        var table = NewValueTable();
        foreach (var r in rows) AddRow(table, r);
        return WrapGroup(title, table);
    }

    private static Control WrapGroup(string title, Control content)
    {
        var box = new GroupBox { Text = title, Dock = DockStyle.Fill, ForeColor = Dim, BackColor = Panel, Padding = new Padding(8), Margin = new Padding(3) };
        content.BackColor = Panel;
        box.Controls.Add(content);
        return box;
    }

    private static Button MakeButton(string text, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(44, 48, 55), ForeColor = Fg,
            Padding = new Padding(8, 3, 8, 3), Margin = new Padding(0, 0, 8, 0),
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(70, 75, 85);
        b.Click += onClick;
        return b;
    }

    private void OpenLogs()
    {
        var dir = _sampler.SessionDirectory ?? _settings.LogDirectory;
        Directory.CreateDirectory(dir);
        OpenPath(dir);
    }

    private static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception e) { MessageBox.Show(e.Message, "Could not open", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}
