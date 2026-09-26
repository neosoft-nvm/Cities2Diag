using System.Collections.Concurrent;
using System.Diagnostics;
using Cs2Monitor.Collectors;
using Cs2Monitor.Logging;

namespace Cs2Monitor;

/// <summary>
/// Runs all collectors on one dedicated background thread at a fixed rate,
/// keeps a rolling history in memory and writes the session log while the game runs.
/// The UI only reads <see cref="Latest"/> and <see cref="GetHistory"/>.
/// </summary>
public sealed class Sampler : IDisposable
{
    private readonly MonitorSettings _settings;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly ConcurrentQueue<string> _pendingMarkers = new();

    private readonly Sample?[] _history;
    private int _historyNext;
    private int _historyCount;
    private readonly object _historyLock = new();

    private SystemCollector? _system;
    private NvmlCollector? _nvml;
    private ProcessCollector? _process;
    private SessionLogger? _logger;
    private readonly Stopwatch _processSearch = new();

    private volatile Sample? _latest;
    private volatile string? _sessionDirectory;
    private volatile string? _error;

    public Sample? Latest => _latest;
    public string? SessionDirectory => _sessionDirectory;
    public string? Error => _error;
    public string[] GpuNames { get; private set; } = Array.Empty<string>();
    public string? NvmlError { get; private set; }
    public IReadOnlyList<string> UnavailableCounters { get; private set; } = Array.Empty<string>();
    public string? GameExePath { get; private set; }
    public string? GameVersion { get; private set; }

    /// <summary>Set once the collectors are created (GPU names etc. are known).</summary>
    public ManualResetEventSlim Initialized { get; } = new(false);

    public Sampler(MonitorSettings settings)
    {
        _settings = settings;
        int capacity = Math.Max(10, settings.HistorySeconds * 1000 / settings.SampleIntervalMs);
        _history = new Sample?[capacity];
        _thread = new Thread(Run)
        {
            Name = "Cs2Monitor sampler",
            IsBackground = true,
            // Above normal so the monitor keeps sampling on time even when the game saturates every core;
            // otherwise the gaps in our data would line up exactly with the stalls we are trying to see.
            // Each sample costs only a few ms of CPU, so this does not take meaningful time from the game.
            Priority = ThreadPriority.AboveNormal,
        };
    }

    public void Start() => _thread.Start();

    public void AddMarker(string text) => _pendingMarkers.Enqueue(text);

    public Sample[] GetHistory()
    {
        lock (_historyLock)
        {
            var result = new Sample[_historyCount];
            int start = (_historyNext - _historyCount + _history.Length) % _history.Length;
            for (int i = 0; i < _historyCount; i++) result[i] = _history[(start + i) % _history.Length]!;
            return result;
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
            _logger = new SessionLogger(_settings.LogDirectory, Environment.ProcessorCount, GpuNames.Length);
        }
        catch (Exception e)
        {
            _error = "Collector initialisation failed: " + e.Message;
            Initialized.Set();
            return;
        }
        Initialized.Set();

        var clock = Stopwatch.StartNew();
        double interval = _settings.SampleIntervalMs;
        double next = 0;
        double last = 0;

        while (!_stop.IsSet)
        {
            double wait = next - clock.Elapsed.TotalMilliseconds;
            if (wait > 0 && _stop.Wait(TimeSpan.FromMilliseconds(wait))) break;

            double t = clock.Elapsed.TotalMilliseconds;
            var s = new Sample
            {
                Utc = DateTime.UtcNow,
                TMs = t,
                IntervalMs = last > 0 ? t - last : interval,
            };
            try
            {
                SampleOnce(s, (t - last) / 1000.0);
            }
            catch (Exception e)
            {
                _error = "Sampling error: " + e.Message; // keep running; one bad sample must not stop monitoring
            }
            s.CostMs = clock.Elapsed.TotalMilliseconds - t;
            last = t;

            try
            {
                if (_logger is { Active: true }) _logger.Write(s);
            }
            catch (Exception e)
            {
                _error = "Log write error: " + e.Message;
            }
            Publish(s);

            // Fixed schedule; if we fell behind (e.g. the whole system stalled), skip ahead instead of bursting.
            next += interval;
            if (next < clock.Elapsed.TotalMilliseconds) next = clock.Elapsed.TotalMilliseconds + interval;
        }

        _logger?.End();
        _process?.Dispose();
        _nvml?.Dispose();
        _system?.Dispose();
    }

    private void SampleOnce(Sample s, double intervalSec)
    {
        TrackGameProcess();

        _process!.Collect(s, intervalSec);
        _system!.Collect(s, s.GameRunning ? s.Pid : null);
        _nvml?.Collect(s);

        if (_pendingMarkers.TryDequeue(out var marker))
        {
            s.Marker = marker;
            while (_pendingMarkers.TryDequeue(out var more)) s.Marker += " | " + more;
        }
    }

    private void TrackGameProcess()
    {
        var process = _process!;
        if (process.Pid != 0)
        {
            if (!process.HasExited) return;
            _logger!.End();
            _sessionDirectory = null;
            process.Detach();
            GameExePath = null;
            GameVersion = null;
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
                });
                _sessionDirectory = _logger.SessionDirectory;
                break;
            }
        }
        finally
        {
            foreach (var p in candidates) p.Dispose();
        }
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
        if (_thread.IsAlive) _thread.Join(3000);
    }
}
