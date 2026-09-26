using System.Collections.Concurrent;
using System.Diagnostics;
using Cs2Monitor.Collectors;
using Cs2Monitor.Detection;
using Cs2Monitor.Logging;

namespace Cs2Monitor;

/// <summary>What the UI shows about the detector. Immutable snapshot.</summary>
public sealed record DetectorStatus(
    string Phase,
    double? StallSeconds,
    string? Rules,
    IReadOnlyList<(string Label, string Unit, double? Baseline, double? Now)> Compare,
    double SettledAgeMs);

public sealed record SavedEvent(int Id, string Name, string Type, double DurationSec, string JsonPath, DateTime StartUtc, string Headline);

/// <summary>
/// Runs all collectors on one dedicated background thread at a fixed rate.
///
/// Each sample goes through two steps:
///   1. taken     → published immediately to the live UI (Latest) and the in-memory history;
///   2. settled   → FrameSettleMs later: frame timing filled in, stall detector run, row written to samples.csv,
///                  events saved when complete.
/// The UI only reads the volatile snapshots and GetHistory().
/// </summary>
public sealed class Sampler : IDisposable
{
    private static readonly string[] CompareKeys = { "gpu_util", "cpu_busy", "game_cores_busy", "main_thread_pct", "fps", "frametime_avg_ms" };

    private readonly MonitorSettings _settings;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly ConcurrentQueue<string> _pendingMarkers = new();
    private readonly ConcurrentQueue<(double TMs, DateTime Utc)> _captureRequests = new();

    private readonly Sample?[] _history;
    private int _historyNext;
    private int _historyCount;
    private readonly object _historyLock = new();

    private readonly Queue<Sample> _unsettled = new();
    private readonly List<(double TMs, DateTime Utc)> _pendingCaptures = new();
    private readonly List<(double TMs, DateTime Utc)> _manualEvents = new();

    private SystemCollector? _system;
    private NvmlCollector? _nvml;
    private ProcessCollector? _process;
    private PresentMonSource? _presentMon;
    private SessionLogger? _logger;
    private StallDetector? _detector;
    private EventWriter? _events;
    private readonly Stopwatch _processSearch = new();
    private Stopwatch _clock = new();

    private volatile Sample? _latest;
    private volatile Sample? _latestSettled;
    private volatile DetectorStatus? _detectorStatus;
    private volatile SavedEvent? _lastEvent;
    private volatile string? _sessionDirectory;
    private volatile string? _error;
    private int _eventCount;

    public Sample? Latest => _latest;
    public Sample? LatestSettled => _latestSettled;
    public DetectorStatus? Detector => _detectorStatus;
    public SavedEvent? LastEvent => _lastEvent;
    public int EventCount => Volatile.Read(ref _eventCount);
    public string? SessionDirectory => _sessionDirectory;
    public string? Error => _error;
    public string FrameTimingStatus => _presentMon?.Status ?? "starting";
    public IReadOnlyList<string> RuleProblems => _detector?.RuleProblems ?? Array.Empty<string>();
    public string[] GpuNames { get; private set; } = Array.Empty<string>();
    public string? NvmlError { get; private set; }
    public IReadOnlyList<string> UnavailableCounters { get; private set; } = Array.Empty<string>();
    public string? GameExePath { get; private set; }
    public string? GameVersion { get; private set; }

    public Sampler(MonitorSettings settings)
    {
        _settings = settings;
        var d = settings.Detection;
        // History must hold a whole event: pre + longest stall + post (+ settle delay and slack).
        int seconds = Math.Max(settings.HistorySeconds, d.PreEventSeconds + d.MaxStallSeconds + d.PostEventSeconds + settings.FrameSettleMs / 1000 + 30);
        _history = new Sample?[Math.Max(10, seconds * 1000 / settings.SampleIntervalMs)];
        _thread = new Thread(Run)
        {
            Name = "Cs2Monitor sampler",
            IsBackground = true,
            // Above normal so the monitor keeps sampling on time even when the game saturates every core;
            // otherwise the gaps in our data would line up exactly with the stalls we are trying to see.
            // Each sample costs only a few ms, so this does not take meaningful time from the game.
            Priority = ThreadPriority.AboveNormal,
        };
    }

    public void Start() => _thread.Start();

    public void AddMarker(string text) => _pendingMarkers.Enqueue(text);

    /// <summary>Manual "Capture Event": confirms an ongoing automatic stall, or records a manual event around now.</summary>
    public void Capture(string source)
    {
        _captureRequests.Enqueue((_clock.Elapsed.TotalMilliseconds, DateTime.UtcNow));
        _pendingMarkers.Enqueue("capture (" + source + ")");
    }

    public Sample[] GetHistory(double? sinceTMs = null)
    {
        lock (_historyLock)
        {
            var result = new List<Sample>(_historyCount);
            int start = (_historyNext - _historyCount + _history.Length) % _history.Length;
            for (int i = 0; i < _historyCount; i++)
            {
                var s = _history[(start + i) % _history.Length]!;
                if (sinceTMs == null || s.TMs >= sinceTMs) result.Add(s);
            }
            return result.ToArray();
        }
    }

    private void Run()
    {
        try
        {
            _system = new SystemCollector(_settings.EnableGpuEngineCounters);
            UnavailableCounters = _system.Unavailable.ToArray();
            if (_settings.EnableNvml)
            {
                _nvml = new NvmlCollector();
                GpuNames = _nvml.Names;
                NvmlError = _nvml.Error;
            }
            else NvmlError = "disabled in settings";
            _process = new ProcessCollector(_settings.EnableThreadSampling, _settings.ThreadListRefreshMs);
            _presentMon = new PresentMonSource(_settings);
            var columns = CsvSchema.Build(Environment.ProcessorCount, GpuNames.Length);
            _logger = new SessionLogger(_settings.LogDirectory, columns);
            _events = new EventWriter(_settings.LogDirectory, columns);
            _detector = new StallDetector(_settings.Detection);
        }
        catch (Exception e)
        {
            _error = "Collector initialisation failed: " + e.Message;
            return;
        }

        _clock = Stopwatch.StartNew();
        double interval = _settings.SampleIntervalMs;
        double next = 0;
        double last = 0;

        while (!_stop.IsSet)
        {
            double wait = next - _clock.Elapsed.TotalMilliseconds;
            if (wait > 0 && _stop.Wait(TimeSpan.FromMilliseconds(wait))) break;

            double t = _clock.Elapsed.TotalMilliseconds;
            var s = new Sample
            {
                Utc = DateTime.UtcNow,
                QpcMs = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency,
                TMs = t,
                IntervalMs = last > 0 ? t - last : interval,
            };
            try
            {
                TrackGameProcess();
                _process!.Collect(s, (t - last) / 1000.0);
                _system!.Collect(s, s.GameRunning ? s.Pid : null);
                _nvml?.Collect(s);
                if (_pendingMarkers.TryDequeue(out var marker))
                {
                    s.Marker = marker;
                    while (_pendingMarkers.TryDequeue(out var more)) s.Marker += " | " + more;
                }
            }
            catch (Exception e)
            {
                _error = "Sampling error: " + e.Message; // keep running; one bad sample must not stop monitoring
            }
            s.CostMs = _clock.Elapsed.TotalMilliseconds - t;
            last = t;

            Publish(s);
            _unsettled.Enqueue(s);

            try
            {
                _presentMon!.Poll();
                SettleUpTo(t - _settings.FrameSettleMs);
            }
            catch (Exception e)
            {
                _error = "Processing error: " + e.Message;
            }

            // Fixed schedule; if we fell behind (e.g. the whole system stalled), skip ahead instead of bursting.
            next += interval;
            if (next < _clock.Elapsed.TotalMilliseconds) next = _clock.Elapsed.TotalMilliseconds + interval;
        }

        EndGameSession("monitor closed");
        _process?.Dispose();
        _nvml?.Dispose();
        _system?.Dispose();
    }

    private void SettleUpTo(double tMs)
    {
        while (_unsettled.Count > 0 && _unsettled.Peek().TMs <= tMs)
            Settle(_unsettled.Dequeue());
    }

    private void Settle(Sample s)
    {
        if (s.GameRunning) _presentMon!.Fill(s, s.QpcMs - s.IntervalMs, s.QpcMs);

        while (_captureRequests.TryDequeue(out var req)) _pendingCaptures.Add(req);
        for (int i = _pendingCaptures.Count - 1; i >= 0; i--)
        {
            var c = _pendingCaptures[i];
            if (c.TMs > s.TMs) continue;
            _pendingCaptures.RemoveAt(i);
            if (!s.GameRunning) continue;
            if (_detector!.Current != null) _detector.ConfirmByUser();
            else _manualEvents.Add(c);
        }

        var finished = _detector!.Process(s);
        if (_logger!.Active) _logger.Write(s);
        if (finished != null) SaveAutoEvent(finished);

        var post = _settings.Detection.PostEventSeconds * 1000.0;
        for (int i = _manualEvents.Count - 1; i >= 0; i--)
        {
            if (s.TMs - _manualEvents[i].TMs < post) continue;
            SaveManualEvent(_manualEvents[i]);
            _manualEvents.RemoveAt(i);
        }

        _latestSettled = s;
        UpdateDetectorStatus(s);
    }

    private void UpdateDetectorStatus(Sample s)
    {
        var d = _detector!;
        var compare = new List<(string, string, double?, double?)>();
        foreach (var key in CompareKeys)
        {
            var m = Metrics.Find(key)!;
            compare.Add((m.Label, m.Unit, d.Baseline(m), m.Get(s)));
        }
        double? stallSec = d.Current is { } c ? ((double.IsNaN(c.EndTMs) ? s.TMs : c.EndTMs) - c.StartTMs) / 1000.0 : null;
        _detectorStatus = new DetectorStatus(s.Phase ?? "idle", stallSec, s.RulesFired, compare, _clock.Elapsed.TotalMilliseconds - s.TMs);
    }

    private void SaveAutoEvent(DetectedStall stall)
    {
        var pre = _settings.Detection.PreEventSeconds * 1000.0;
        var post = _settings.Detection.PostEventSeconds * 1000.0;
        var record = new EventRecord
        {
            Type = "auto",
            ConfirmedByUser = stall.ConfirmedByUser,
            StartUtc = stall.StartUtc,
            EndUtc = stall.StartUtc.AddMilliseconds(stall.DurationMs),
            DurationSec = Math.Round(stall.DurationMs / 1000.0, 2),
            Truncated = stall.Truncated,
            EndReason = stall.EndReason,
            PeriodDefinition = $"before = {pre / 1000:0} s before the stall; during = detected stall; after = up to {post / 1000:0} s after it",
            BaselinesAtDetection = stall.Baselines,
            RulesFired = stall.RuleCounts.OrderByDescending(kv => kv.Value).Select(kv => new RuleHit { Name = kv.Key, Samples = kv.Value }).ToList(),
        };
        SaveEvent(record, stall.StartTMs - pre, stall.EndTMs + post, stall.StartTMs, stall.StartTMs, stall.EndTMs);
    }

    private void SaveManualEvent((double TMs, DateTime Utc) c)
    {
        var pre = _settings.Detection.PreEventSeconds * 1000.0;
        var post = _settings.Detection.PostEventSeconds * 1000.0;
        var record = new EventRecord
        {
            Type = "manual",
            ConfirmedByUser = true,
            StartUtc = c.Utc,
            PeriodDefinition = "manual capture: during = 5 s before to 1 s after the key press (you press after noticing); before/after = the rest of the window",
        };
        SaveEvent(record, c.TMs - pre, c.TMs + post, c.TMs, c.TMs - 5000, c.TMs + 1000);
    }

    private void SaveEvent(EventRecord record, double fromTMs, double toTMs, double originTMs, double duringFrom, double duringTo)
    {
        if (_logger?.SessionDirectory is not string sessionDir) return;
        var samples = GetHistory(fromTMs).Where(x => x.TMs <= toTMs && x.GameRunning).ToList();
        if (samples.Count == 0) return;

        record.SessionId = _logger.SessionId ?? Path.GetFileName(sessionDir);
        record.GameVersion = GameVersion;
        record.FrameTiming = "PresentMon: " + _presentMon!.Status;
        try
        {
            var (saved, path) = _events!.Write(record, samples, originTMs, duringFrom, duringTo, sessionDir);
            _logger.AddEvent(saved.Name);
            var headline = saved.Observations.FirstOrDefault() ?? "";
            _lastEvent = new SavedEvent(saved.Id, saved.Name, saved.Type, saved.DurationSec, path, saved.StartUtc, headline);
            Interlocked.Increment(ref _eventCount);
        }
        catch (Exception e)
        {
            _error = "Could not save event: " + e.Message;
        }
    }

    private void TrackGameProcess()
    {
        var process = _process!;
        if (process.Pid != 0)
        {
            if (!process.HasExited) return;
            EndGameSession("game exited");
        }

        if (_processSearch.IsRunning && _processSearch.ElapsedMilliseconds < 2000) return;
        _processSearch.Restart();

        var candidates = Process.GetProcessesByName(_settings.ProcessName);
        try
        {
            foreach (var p in candidates)
            {
                if (!process.Attach(p.Id)) continue;
                GameExePath = process.ImagePath;
                GameVersion = process.FileVersion;

                var mem = new Native.Win32.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.Win32.MEMORYSTATUSEX>() };
                Native.Win32.GlobalMemoryStatusEx(ref mem);
                _logger!.Start(new SessionInfo
                {
                    StartedUtc = DateTime.UtcNow,
                    StartedLocal = DateTime.Now,
                    GamePid = p.Id,
                    GameExePath = process.ImagePath,
                    GameFileVersion = process.FileVersion,
                    GameProcessStartUtc = process.StartTime,
                    System = SystemInfo.Gather(mem.ullTotalPhys / 1048576.0, _nvml?.Names ?? Array.Empty<string>(), _nvml?.VramTotalMb ?? Array.Empty<double>(), NvmlError),
                    Settings = _settings,
                    UnavailableCounters = UnavailableCounters.ToList(),
                    FrameTiming = PresentMonSource.IsElevated ? "PresentMon (monitor elevated)" : "PresentMon via UAC prompt",
                });
                _sessionDirectory = _logger.SessionDirectory;
                _presentMon!.Start(p.Id, _logger.SessionDirectory!);
                break;
            }
        }
        finally
        {
            foreach (var p in candidates) p.Dispose();
        }
    }

    /// <summary>Finalises everything that belongs to the current game session.</summary>
    private void EndGameSession(string reason)
    {
        if (_process == null || _process.Pid == 0) return;
        try
        {
            _presentMon?.Poll();
            SettleUpTo(double.MaxValue);
            double lastT = _latestSettled?.TMs ?? 0;
            if (_detector?.Abort(reason, lastT) is { } stall) SaveAutoEvent(stall);
            foreach (var c in _manualEvents) SaveManualEvent(c);
            _manualEvents.Clear();
            _pendingCaptures.Clear();
        }
        catch (Exception e)
        {
            _error = "Error while closing session: " + e.Message;
        }
        if (_presentMon != null)
            _logger?.SetFrameTiming($"PresentMon: {_presentMon.Status}; frames received {_presentMon.FramesReceived}; " +
                                    $"sample windows filled {_presentMon.WindowsFilled}; last skip: {_presentMon.LastSkipReason ?? "none"}; " +
                                    $"last error: {_error ?? "none"}");
        _logger?.End();
        _presentMon?.Stop();
        _sessionDirectory = null;
        _process.Detach();
        GameExePath = null;
        GameVersion = null;
    }

    private void Publish(Sample s)
    {
        lock (_historyLock)
        {
            _history[_historyNext] = s;
            _historyNext = (_historyNext + 1) % _history.Length;
            if (_historyCount < _history.Length) _historyCount++;
        }
        _latest = s;
    }

    public void Dispose()
    {
        _stop.Set();
        if (_thread.IsAlive) _thread.Join(5000);
    }
}
