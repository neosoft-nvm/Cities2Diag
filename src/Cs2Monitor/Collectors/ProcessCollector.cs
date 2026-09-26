using System.Diagnostics;
using Cs2Monitor.Native;

namespace Cs2Monitor.Collectors;

/// <summary>
/// Measures one process (the game) through direct Win32 calls on a cached handle,
/// which is much cheaper than System.Diagnostics.Process.Refresh().
/// </summary>
internal sealed class ProcessCollector : IDisposable
{
    private readonly bool _threadSampling;
    private readonly int _threadRefreshMs;
    private readonly int _cpuCount = Environment.ProcessorCount;

    private IntPtr _handle;
    private long _lastCpuTicks = -1;
    private uint _lastPageFaults;
    private ulong _lastIoRead, _lastIoWrite;
    private bool _haveLast;

    private IntPtr _window;
    private readonly Stopwatch _windowLookup = new();

    private readonly Dictionary<uint, ThreadState> _threads = new();
    private readonly Stopwatch _threadRefresh = new();
    private readonly List<uint> _threadIds = new();
    private uint _mainThreadId;

    public int Pid { get; private set; }
    public string? ImagePath { get; private set; }
    public string? FileVersion { get; private set; }
    public DateTime? StartTime { get; private set; }

    private sealed class ThreadState
    {
        public IntPtr Handle;
        public long LastTicks = -1;
        public string? Name;
        public long CreationTime;
    }

    public ProcessCollector(bool threadSampling, int threadRefreshMs)
    {
        _threadSampling = threadSampling;
        _threadRefreshMs = threadRefreshMs;
    }

    public bool Attach(int pid)
    {
        Detach();
        _handle = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION | Win32.SYNCHRONIZE, false, pid);
        if (_handle == IntPtr.Zero) return false;
        Pid = pid;
        ImagePath = Win32.GetProcessImagePath(_handle);
        try { if (ImagePath != null) FileVersion = FileVersionInfo.GetVersionInfo(ImagePath).FileVersion; } catch { /* optional */ }
        if (Win32.GetProcessTimes(_handle, out long created, out _, out _, out _)) StartTime = DateTime.FromFileTimeUtc(created);
        return true;
    }

    public bool HasExited => _handle == IntPtr.Zero || Win32.WaitForSingleObject(_handle, 0) == Win32.WAIT_OBJECT_0;

    public void Collect(Sample s, double intervalSec)
    {
        if (_handle == IntPtr.Zero) return;
        s.GameRunning = true;
        s.Pid = Pid;

        long cpuTicks = -1;
        if (Win32.GetProcessTimes(_handle, out _, out _, out long kernel, out long user)) cpuTicks = kernel + user;

        Win32.PROCESS_MEMORY_COUNTERS_EX mem = default;
        bool haveMem = Win32.GetProcessMemoryInfo(_handle, out mem, (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.PROCESS_MEMORY_COUNTERS_EX>());
        bool haveIo = Win32.GetProcessIoCounters(_handle, out var io);

        if (haveMem)
        {
            s.ProcWorkingSetMb = (ulong)mem.WorkingSetSize / 1048576.0;
            s.ProcPrivateMb = (ulong)mem.PrivateUsage / 1048576.0;
        }

        if (_haveLast && intervalSec > 0)
        {
            if (cpuTicks >= 0 && _lastCpuTicks >= 0)
            {
                double cores = (cpuTicks - _lastCpuTicks) / 1e7 / intervalSec;
                s.ProcCoresBusy = cores;
                s.ProcCpuPct = Math.Min(100, cores / _cpuCount * 100);
            }
            if (haveMem) s.ProcPageFaultsPerSec = (mem.PageFaultCount - _lastPageFaults) / intervalSec;
            if (haveIo)
            {
                s.ProcIoReadMbS = (io.ReadTransferCount - _lastIoRead) / 1048576.0 / intervalSec;
                s.ProcIoWriteMbS = (io.WriteTransferCount - _lastIoWrite) / 1048576.0 / intervalSec;
            }
        }
        _lastCpuTicks = cpuTicks;
        if (haveMem) _lastPageFaults = mem.PageFaultCount;
        if (haveIo) { _lastIoRead = io.ReadTransferCount; _lastIoWrite = io.WriteTransferCount; }
        _haveLast = true;

        CollectHung(s);
        if (_threadSampling) CollectThreads(s, intervalSec);
    }

    private void CollectHung(Sample s)
    {
        if (_window == IntPtr.Zero && (!_windowLookup.IsRunning || _windowLookup.ElapsedMilliseconds > 5000))
        {
            _windowLookup.Restart();
            try { using var p = Process.GetProcessById(Pid); _window = p.MainWindowHandle; } catch { /* exited */ }
        }
        if (_window != IntPtr.Zero) s.Hung = Win32.IsHungAppWindow(_window);
    }

    private void CollectThreads(Sample s, double intervalSec)
    {
        if (!_threadRefresh.IsRunning || _threadRefresh.ElapsedMilliseconds >= _threadRefreshMs)
        {
            _threadRefresh.Restart();
            RefreshThreadList();
        }

        s.ThreadCount = _threads.Count;
        double top = -1, second = -1;
        uint topId = 0;
        int over90 = 0;
        foreach (var (tid, t) in _threads)
        {
            if (!Win32.GetThreadTimes(t.Handle, out _, out _, out long k, out long u)) continue;
            long ticks = k + u;
            if (t.LastTicks >= 0 && intervalSec > 0)
            {
                double pct = (ticks - t.LastTicks) / 1e7 / intervalSec * 100;
                if (tid == _mainThreadId) s.MainThreadPct = pct;
                if (pct >= 90) over90++;
                if (pct > top) { second = top; top = pct; topId = tid; }
                else if (pct > second) second = pct;
            }
            t.LastTicks = ticks;
        }
        if (top >= 0)
        {
            s.TopThreadPct = top;
            s.TopThreadId = (int)topId;
            s.TopThreadName = _threads.TryGetValue(topId, out var tt) ? tt.Name : null;
            s.SecondThreadPct = second >= 0 ? second : null;
            s.ThreadsOver90 = over90;
        }
    }

    private void RefreshThreadList()
    {
        if (!Win32.GetThreadIds(Pid, _threadIds)) return;
        var alive = new HashSet<uint>(_threadIds);
        foreach (uint tid in _threadIds)
        {
            if (_threads.ContainsKey(tid)) continue;
            IntPtr h = Win32.OpenThread(Win32.THREAD_QUERY_LIMITED_INFORMATION, false, tid);
            if (h == IntPtr.Zero) continue;
            var state = new ThreadState { Handle = h, Name = Win32.GetThreadName(h) };
            if (Win32.GetThreadTimes(h, out long created, out _, out _, out _)) state.CreationTime = created;
            _threads[tid] = state;
        }

        foreach (var tid in _threads.Keys.Where(t => !alive.Contains(t)).ToList())
        {
            Win32.CloseHandle(_threads[tid].Handle);
            _threads.Remove(tid);
        }

        // The oldest thread is the one the process started on: Unity's main thread.
        if (!_threads.ContainsKey(_mainThreadId) && _threads.Count > 0)
            _mainThreadId = _threads.MinBy(kv => kv.Value.CreationTime).Key;
    }

    public void Detach()
    {
        foreach (var t in _threads.Values) Win32.CloseHandle(t.Handle);
        _threads.Clear();
        _mainThreadId = 0;
        if (_handle != IntPtr.Zero) Win32.CloseHandle(_handle);
        _handle = IntPtr.Zero;
        _window = IntPtr.Zero;
        _haveLast = false;
        _lastCpuTicks = -1;
        Pid = 0;
        ImagePath = null;
        FileVersion = null;
        StartTime = null;
    }

    public void Dispose() => Detach();
}
