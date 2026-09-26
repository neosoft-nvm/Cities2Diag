using System.Diagnostics;
using System.Media;
using Cs2Monitor.Collectors;
using Cs2Monitor.Native;

namespace Cs2Monitor.UI;

internal sealed class MainForm : Form
{
    private readonly MonitorSettings _settings;
    private readonly Sampler _sampler;
    private readonly System.Windows.Forms.Timer _uiTimer = new();
    private readonly Dictionary<string, Label> _values = new();

    private readonly Label _status = new();
    private readonly Label _detect = new();
    private readonly Label _detectDetail = new();
    private readonly Button _viewEvent;
    private readonly Label _footer = new();
    private readonly TimelineChart _chart = new();
    private readonly CoreBars _cores = new();
    private readonly TableLayoutPanel _gpuTable = NewValueTable();
    private EventViewerForm? _viewer;
    private int _captureCount;
    private int _seenEvents;
    private CaptureHotkey? _hotkey;
    private readonly CaptureToast _toast = new();
    private readonly Label _captureInfo = new();
    private string _lastCapture = "none yet";
    private Sample? _lastOther;
    private readonly bool _logsInOneDrive;
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
        _logsInOneDrive = MonitorSettings.IsInOneDrive(settings.LogDirectory);

        Text = "CS2 Stall Investigator — Monitor" + (Collectors.PresentMonSource.IsElevated ? " (administrator)" : "");
        BackColor = Bg;
        ForeColor = Fg;
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(1180, 900);
        MinimumSize = new Size(980, 720);
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 220));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        _status.AutoSize = true;
        _status.Font = new Font("Segoe UI Semibold", 12f);
        _status.Margin = new Padding(0, 0, 0, 6);
        root.Controls.Add(_status);

        // Detector status strip
        var detectPanel = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Panel, Padding = new Padding(10, 6, 10, 6), Margin = new Padding(3, 0, 3, 6) };
        detectPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
        detectPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        detectPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _detect.AutoSize = true;
        _detect.Font = new Font("Segoe UI Semibold", 15f);
        _detect.Text = "STATUS: —";
        _detectDetail.AutoSize = true;
        _detectDetail.ForeColor = Dim;
        _detectDetail.Font = new Font("Consolas", 9.5f);
        _viewEvent = MakeButton("View event", (_, _) => OpenViewer(_sampler.LastEvent?.JsonPath));
        _viewEvent.Visible = false;
        detectPanel.Controls.Add(_detect, 0, 0);
        detectPanel.Controls.Add(_detectDetail, 1, 0);
        detectPanel.Controls.Add(_viewEvent, 2, 0);
        root.Controls.Add(detectPanel);

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

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        right.Controls.Add(WrapGroup("GPU", _gpuTable), 0, 0);
        right.Controls.Add(MakeGroup($"Frames (≈{settings.FrameSettleMs / 1000.0:0.#} s behind)", new[] { "FPS", "Frame time", "Frame CPU busy", "Frame GPU busy", "PresentMon" }), 0, 1);
        right.Controls.Add(MakeGroup("Not measured yet", new[] { "Population", "Simulation speed" }), 0, 2);
        columns.Controls.Add(right, 2, 0);
        root.Controls.Add(columns);

        _cores.Dock = DockStyle.Fill;
        _cores.Margin = new Padding(3, 6, 3, 6);
        root.Controls.Add(WrapGroup("Logical processors (% busy)", _cores));

        _chart.Dock = DockStyle.Fill;
        _chart.XMin = -settings.HistorySeconds;
        _chart.XMax = 0;
        root.Controls.Add(_chart);

        var bottom = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0), WrapContents = false };
        var capture = MakeButton($"Capture event  ({settings.CaptureHotkey})", (_, _) => CaptureEvent("button"));
        var events = MakeButton("Events…", (_, _) => OpenViewer(null));
        var open = MakeButton("Open logs folder", (_, _) => OpenLogs());
        var settingsBtn = MakeButton("Open settings file", (_, _) => OpenPath(MonitorSettings.FilePath));
        _captureInfo.AutoSize = true;
        _captureInfo.Font = new Font("Segoe UI Semibold", 10f);
        _captureInfo.Margin = new Padding(12, 5, 0, 0);
        _footer.AutoSize = true;
        _footer.ForeColor = Dim;
        _footer.Margin = new Padding(12, 2, 0, 0);
        bottom.Controls.AddRange(new Control[] { capture, events, open, settingsBtn, _captureInfo });
        root.Controls.Add(bottom);
        root.RowCount++;
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_footer);

        SetValue("Population", "Phase 3 (CS2 mod)", Dim);
        SetValue("Simulation speed", "Phase 3 (CS2 mod)", Dim);

        _uiTimer.Interval = settings.UiRefreshMs;
        _uiTimer.Tick += (_, _) => RefreshUi();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // The hook callback arrives on this (UI) thread; defer the work so the callback returns immediately.
        _hotkey = new CaptureHotkey(_settings.CaptureHotkey, () => BeginInvoke(() => CaptureEvent("hotkey")));
        UpdateCaptureInfo();
        _uiTimer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _uiTimer.Stop();
        _hotkey?.Dispose();
        _toast.Dispose();
        base.OnFormClosed(e);
    }

    private void CaptureEvent(string source)
    {
        _captureCount++;
        _sampler.Capture(source);
        _lastCapture = $"#{_captureCount} at {DateTime.Now:HH:mm:ss} ({source})";
        if (_settings.CaptureSound) CaptureFeedback.Beep();
        if (_settings.CaptureToast) _toast.ShowMessage($"● Captured #{_captureCount}");
        UpdateCaptureInfo();
    }

    private void UpdateCaptureInfo()
    {
        if (_hotkey is { Active: true })
        {
            _captureInfo.Text = $"Key {_hotkey.Description}: active ✓   Last capture: {_lastCapture}";
            _captureInfo.ForeColor = Good;
        }
        else
        {
            _captureInfo.Text = $"Key {_settings.CaptureHotkey}: NOT WORKING — {_hotkey?.Error ?? "not set up"}   Last capture: {_lastCapture}";
            _captureInfo.ForeColor = Bad;
        }
    }

    private void OpenViewer(string? selectPath)
    {
        if (_viewer == null || _viewer.IsDisposed)
        {
            _viewer = new EventViewerForm(_settings.LogDirectory, selectPath);
            _viewer.Show(this);
        }
        else
        {
            _viewer.Reload(selectPath);
            _viewer.Activate();
        }
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

        // Frames (from the latest settled sample)
        var settled = _sampler.LatestSettled;
        bool haveFrames = settled?.Frames != null;
        SetValue("FPS", haveFrames ? $"{settled!.Fps:0.0}" : "—");
        SetValue("Frame time", haveFrames && settled!.FrameTimeAvgMs.HasValue ? $"{settled.FrameTimeAvgMs:0.0} ms avg · {settled.FrameTimeMaxMs:0.0} ms max" : "—");
        SetValue("Frame CPU busy", settled?.FrameCpuBusyAvgMs is double fc ? $"{fc:0.0} ms" : "—");
        SetValue("Frame GPU busy", settled?.FrameGpuBusyAvgMs is double fg ? $"{fg:0.0} ms" : "—");
        var pm = _sampler.FrameTimingStatus;
        SetValue("PresentMon", pm, pm.StartsWith("unavailable") ? Warn : pm == "running" ? Good : Dim);

        if (s.OtherCpuCores != null) _lastOther = s; // measured once per second
        RefreshDetector();
        _cores.SetData(s.CoreBusyPct);
        RefreshChart(s);

        double rate = 1000.0 / Math.Max(1, s.IntervalMs);
        _footer.Text = $"{rate:0.0} samples/s · cost {s.CostMs:0.0} ms/sample · captures {_captureCount} · events saved {_sampler.EventCount}"
            + (_lastOther?.OtherCpuCores is double oc ? $" · other processes {oc:0.00} cores" + (_lastOther.OtherTopCpu is { Length: > 0 } top ? $" (top: {top[0].Name} {top[0].Cores:0.00})" : "") : "")
            + (_logsInOneDrive ? " · ⚠ LogDirectory is inside OneDrive: every log write triggers sync CPU load" : "")
            + (_sampler.RuleProblems.Count > 0 ? " · settings: " + string.Join("; ", _sampler.RuleProblems) : "")
            + (_sampler.Error != null ? " · " + _sampler.Error : "")
            + (_sampler.SessionDirectory != null ? "\nLogging to " + _sampler.SessionDirectory : "");
    }

    private void RefreshDetector()
    {
        var d = _sampler.Detector;
        if (d == null)
        {
            _detect.Text = "STATUS: —";
            _detect.ForeColor = Dim;
            return;
        }

        (_detect.Text, _detect.ForeColor) = d.Phase switch
        {
            "stall" => ($"⚠ STALL DETECTED  {d.StallSeconds:0.0} s", Bad),
            "recovery" => ($"RECOVERY  (stall {d.StallSeconds:0.0} s)", Warn),
            "warning" => ("WARNING", Warn),
            "normal" => ("STATUS: NORMAL", Good),
            "warmup" => ("STATUS: LEARNING BASELINE", Dim),
            _ => ("STATUS: IDLE (game not running)", Dim),
        };

        var lines = new List<string>();
        foreach (var (label, unit, baseline, now) in d.Compare)
        {
            if (baseline == null && now == null) continue;
            string b = baseline is double bv ? Detection.EventWriter.Fmt(bv, unit) : "—";
            string n = now is double nv ? Detection.EventWriter.Fmt(nv, unit) : "—";
            lines.Add($"{label,-20} baseline {b,10}   now {n,10}");
        }
        if (d.Rules != null) lines.Add("Rules firing: " + d.Rules);
        _detectDetail.Text = string.Join("\n", lines);

        int count = _sampler.EventCount;
        if (count != _seenEvents && _sampler.LastEvent is { } ev)
        {
            _seenEvents = count;
            _viewEvent.Text = $"View event #{ev.Id} ({ev.Type}{(ev.Type == "auto" ? $", {ev.DurationSec:0.0} s" : "")})";
            _viewEvent.Visible = true;
            if (_viewer is { IsDisposed: false }) _viewer.Reload(ev.JsonPath);
        }
    }

    private static readonly Color StallBand = Color.FromArgb(70, 200, 60, 60);
    private static readonly Color WarningBand = Color.FromArgb(50, 230, 170, 60);
    private static readonly Color RecoveryBand = Color.FromArgb(40, 80, 160, 230);

    private void RefreshChart(Sample latest)
    {
        var history = _sampler.GetHistory(latest.TMs - _settings.HistorySeconds * 1000.0);
        int n = history.Length;
        var x = new double[n];
        var gpu = new double?[n];
        var cpu = new double?[n];
        var core = new double?[n];
        var main = new double?[n];
        var top = new double?[n];
        var bands = new List<ChartBand>();
        var marks = new List<ChartMark>();
        string? bandPhase = null;
        double bandStart = 0;
        for (int i = 0; i < n; i++)
        {
            var h = history[i];
            x[i] = (h.TMs - latest.TMs) / 1000.0;
            gpu[i] = Metrics.GpuUtil(h);
            cpu[i] = h.CpuBusyPct;
            core[i] = h.MaxCoreBusyPct;
            main[i] = h.MainThreadPct;
            top[i] = h.TopThreadPct;
            if (h.Marker != null) marks.Add(new ChartMark(x[i], h.Marker.Contains("capture") ? "capture" : "mark"));

            string? phase = h.Phase is "stall" or "warning" or "recovery" ? h.Phase : null;
            if (phase != bandPhase)
            {
                if (bandPhase != null) bands.Add(new ChartBand(bandStart, x[i], BandColor(bandPhase)));
                bandPhase = phase;
                bandStart = x[i];
            }
        }
        if (bandPhase != null && n > 0) bands.Add(new ChartBand(bandStart, x[^1], BandColor(bandPhase)));

        _chart.SetData(x, new[]
        {
            new ChartSeries("GPU load", Color.FromArgb(110, 200, 120), gpu),
            new ChartSeries("System CPU", Color.FromArgb(80, 160, 230), cpu),
            new ChartSeries("Busiest core", Color.FromArgb(150, 110, 230), core),
            new ChartSeries("CS2 main thread", Color.FromArgb(235, 130, 60), main),
            new ChartSeries("CS2 busiest thread", Color.FromArgb(235, 90, 110), top),
        }, bands, marks);
    }

    private static Color BandColor(string phase) => phase switch
    {
        "stall" => StallBand,
        "warning" => WarningBand,
        _ => RecoveryBand,
    };

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
