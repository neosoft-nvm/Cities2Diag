using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace Cs2Monitor.Collectors;

/// <summary>
/// Per-frame timing from Intel PresentMon (ETW). PresentMon runs as a separate (usually elevated) process and
/// writes its CSV into a named pipe owned by this class. It cannot write to a file we tail: PresentMon opens its
/// output with fopen_s, which locks the file until PresentMon exits. Lines are copied to frames.csv in the
/// session folder as raw data. Frames carry CPUStartQPCTimeInMs, the same QueryPerformanceCounter clock as Sample.QpcMs.
/// Poll/Fill run on the sampler thread; launching (UAC prompt blocks) and pipe reading run on workers.
/// </summary>
internal sealed class PresentMonSource : IDisposable
{
    private const string SessionName = "Cs2Monitor";
    private const double KeepFramesMs = 6 * 60 * 1000;

    private readonly string? _exe;
    private readonly bool _allowUac;
    private readonly bool _elevated;

    private Process? _process;
    private string? _file;
    private StreamWriter? _raw;
    private CancellationTokenSource? _pipeCts;
    private readonly ConcurrentQueue<string> _lines = new();
    private int _iQpc = -1, _iBetween = -1, _iCpuBusy = -1, _iGpuBusy = -1, _iSwap = -1;
    private bool _haveHeader;
    private volatile string _status;
    private long _generation;

    private readonly List<Frame> _frames = new();
    private readonly Dictionary<string, int> _swapIds = new();

    private readonly record struct Frame(double Qpc, double BetweenPresents, double CpuBusy, double GpuBusy, int SwapChain);

    public string Status => _status;
    public long FramesReceived { get; private set; }
    public long WindowsFilled { get; private set; }
    public string? LastSkipReason { get; private set; }
    public string? FramesFile => _file;
    public static bool IsElevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public PresentMonSource(MonitorSettings settings)
    {
        _allowUac = settings.PresentMonAllowUacPrompt;
        _elevated = IsElevated;
        if (!settings.FrameTimingEnabled)
        {
            _status = "off (disabled in settings)";
            return;
        }
        _exe = !string.IsNullOrWhiteSpace(settings.PresentMonPath) && File.Exists(settings.PresentMonPath)
            ? settings.PresentMonPath
            : FindPresentMon();
        _status = _exe == null
            ? "unavailable: PresentMon not found (put PresentMon-*.exe in the tools folder or set PresentMonPath)"
            : "waiting for game";
    }

    /// <summary>Looks for tools\PresentMon*.exe in the exe folder and its parents (works from bin\Release\… in the repo).</summary>
    private static string? FindPresentMon()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var tools = Path.Combine(dir.FullName, "tools");
            if (!Directory.Exists(tools)) continue;
            var exe = Directory.GetFiles(tools, "PresentMon*.exe").OrderByDescending(f => f).FirstOrDefault();
            if (exe != null) return exe;
        }
        return null;
    }

    public void Start(int pid, string sessionDirectory)
    {
        Stop();
        if (_exe == null) return;
        if (!_elevated && !_allowUac)
        {
            _status = "unavailable: needs admin (run the monitor as administrator, or enable PresentMonAllowUacPrompt)";
            return;
        }

        _file = Path.Combine(sessionDirectory, "frames.csv");
        try
        {
            _raw = new StreamWriter(new FileStream(_file, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false), 1 << 16);
        }
        catch (Exception e)
        {
            _status = "unavailable: cannot create frames.csv: " + e.Message;
            return;
        }

        string pipeName = $"Cs2Monitor_frames_{pid}_{Environment.TickCount64}";
        _pipeCts = new CancellationTokenSource();
        StartPipeReader(pipeName, _pipeCts.Token);

        string args = $"--process_id {pid} --output_file \"\\\\.\\pipe\\{pipeName}\" --no_console_stats --qpc_time_ms " +
                      $"--session_name {SessionName} --stop_existing_session --terminate_on_proc_exit";
        long generation = ++_generation;
        _status = _elevated ? "starting" : "starting (approve the UAC prompt for PresentMon)";

        string exe = _exe;
        bool elevated = _elevated;
        Task.Run(() =>
        {
            try
            {
                var psi = elevated
                    ? new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true }
                    : new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                var p = Process.Start(psi);
                if (Interlocked.Read(ref _generation) != generation) { TryKill(p); return; }
                _process = p;
                _status = "running, waiting for frames";
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223)
            {
                _status = "unavailable: UAC prompt was declined";
            }
            catch (Exception e)
            {
                _status = "unavailable: " + e.Message;
            }
        });
    }

    /// <summary>Accepts PresentMon's connection and queues complete CSV lines for the sampler thread.</summary>
    private void StartPipeReader(string pipeName, CancellationToken token)
    {
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1 << 16, 1 << 16);
        Task.Run(async () =>
        {
            try
            {
                await pipe.WaitForConnectionAsync(token);
                var buffer = new byte[1 << 16];
                var partial = new StringBuilder();
                int n;
                while ((n = await pipe.ReadAsync(buffer, token)) > 0)
                {
                    partial.Append(Encoding.UTF8.GetString(buffer, 0, n));
                    int start = 0;
                    for (int i = 0; i < partial.Length; i++)
                    {
                        if (partial[i] != '\n') continue;
                        _lines.Enqueue(partial.ToString(start, i - start).TrimEnd('\r'));
                        start = i + 1;
                    }
                    partial.Remove(0, start);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { _status = "pipe error: " + e.Message; }
            finally { pipe.Dispose(); }
        });
    }

    public void Stop()
    {
        Interlocked.Increment(ref _generation);
        TryKill(_process);
        _process = null;
        _pipeCts?.Cancel();
        _pipeCts = null;
        Poll(); // keep whatever already arrived in frames.csv
        _raw?.Dispose();
        _raw = null;
        _file = null;
        _lines.Clear();
        _haveHeader = false;
        _frames.Clear();
        _swapIds.Clear();
        FramesReceived = 0;
        WindowsFilled = 0;
        LastSkipReason = null;
        if (_exe != null) _status = "waiting for game";
    }

    private static void TryKill(Process? p)
    {
        if (p == null) return;
        // An elevated PresentMon started from a non-elevated monitor cannot be killed from here;
        // it exits by itself when the game exits (--terminate_on_proc_exit).
        try { if (!p.HasExited) p.Kill(); } catch { /* access denied or already gone */ }
        p.Dispose();
    }

    /// <summary>Takes in the frames PresentMon delivered since the last call.</summary>
    public void Poll()
    {
        if (_raw == null) return;
        bool any = false;
        while (_lines.TryDequeue(out var line))
        {
            _raw.WriteLine(line);
            ParseLine(line);
            any = true;
        }
        if (any) _raw.Flush();

        if (FramesReceived == 0 && _process != null)
        {
            try
            {
                if (_process.HasExited) _status = $"unavailable: PresentMon exited (code {_process.ExitCode}) without delivering frames";
            }
            catch { /* no access to the elevated process */ }
        }

        if (_frames.Count > 0)
        {
            double cutoff = _frames[^1].Qpc - KeepFramesMs;
            int drop = 0;
            while (drop < _frames.Count && _frames[drop].Qpc < cutoff) drop++;
            if (drop > 1000) _frames.RemoveRange(0, drop); // batch removals
        }
    }

    private void ParseLine(string line)
    {
        if (line.Length == 0) return;
        var cols = line.Split(',');
        if (!_haveHeader)
        {
            _iQpc = Array.IndexOf(cols, "CPUStartQPCTimeInMs");
            _iBetween = Array.IndexOf(cols, "MsBetweenPresents");
            _iCpuBusy = Array.IndexOf(cols, "MsCPUBusy");
            _iGpuBusy = Array.IndexOf(cols, "MsGPUBusy");
            _iSwap = Array.IndexOf(cols, "SwapChainAddress");
            _haveHeader = true;
            if (_iQpc < 0 || _iBetween < 0) _status = "unavailable: unexpected PresentMon CSV format";
            return;
        }
        if (_iQpc < 0 || _iBetween < 0 || cols.Length <= Math.Max(_iQpc, _iBetween)) return;

        double qpc = Num(cols[_iQpc]);
        if (double.IsNaN(qpc)) return;
        string swap = _iSwap >= 0 && _iSwap < cols.Length ? cols[_iSwap] : "";
        if (!_swapIds.TryGetValue(swap, out int swapId)) _swapIds[swap] = swapId = _swapIds.Count;

        _frames.Add(new Frame(qpc, Num(cols[_iBetween]), Col(cols, _iCpuBusy), Col(cols, _iGpuBusy), swapId));
        FramesReceived++;
        if (FramesReceived == 1) _status = "running";
    }

    private static double Col(string[] cols, int i) => i >= 0 && i < cols.Length ? Num(cols[i]) : double.NaN;

    private static double Num(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;

    /// <summary>
    /// Fills frame fields of <paramref name="s"/> from frames whose CPU start lies in (from, to].
    /// Leaves them null when the data for that window has not arrived (or never will).
    /// </summary>
    public void Fill(Sample s, double fromQpc, double toQpc)
    {
        if (_frames.Count == 0) { LastSkipReason = "no frames yet"; return; }
        if (_frames[0].Qpc > fromQpc) { LastSkipReason = "window before first frame"; return; }
        if (_frames[^1].Qpc < toQpc)
        {
            // Not all frames of this window delivered yet (or the game stopped presenting).
            LastSkipReason = $"frames only up to {toQpc - _frames[^1].Qpc:0} ms before window end";
            return;
        }
        WindowsFilled++;

        int lo = LowerBound(fromQpc);
        // The busiest swap chain in the window is the game's main one.
        Span<int> counts = stackalloc int[Math.Min(_swapIds.Count, 64)];
        for (int i = lo; i < _frames.Count && _frames[i].Qpc <= toQpc; i++)
            if (_frames[i].Qpc > fromQpc && _frames[i].SwapChain < counts.Length) counts[_frames[i].SwapChain]++;
        int main = 0;
        for (int i = 1; i < counts.Length; i++) if (counts[i] > counts[main]) main = i;

        int n = 0, nCpu = 0, nGpu = 0;
        double sum = 0, max = 0, cpu = 0, gpu = 0;
        for (int i = lo; i < _frames.Count && _frames[i].Qpc <= toQpc; i++)
        {
            var f = _frames[i];
            if (f.Qpc <= fromQpc || f.SwapChain != main) continue;
            if (!double.IsNaN(f.BetweenPresents)) { n++; sum += f.BetweenPresents; max = Math.Max(max, f.BetweenPresents); }
            if (!double.IsNaN(f.CpuBusy)) { nCpu++; cpu += f.CpuBusy; }
            if (!double.IsNaN(f.GpuBusy)) { nGpu++; gpu += f.GpuBusy; }
        }

        s.Frames = n;
        s.Fps = n / ((toQpc - fromQpc) / 1000.0);
        if (n > 0) { s.FrameTimeAvgMs = sum / n; s.FrameTimeMaxMs = max; }
        if (nCpu > 0) s.FrameCpuBusyAvgMs = cpu / nCpu;
        if (nGpu > 0) s.FrameGpuBusyAvgMs = gpu / nGpu;
    }

    private int LowerBound(double qpc)
    {
        int lo = 0, hi = _frames.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_frames[mid].Qpc <= qpc) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    public void Dispose() => Stop();
}
