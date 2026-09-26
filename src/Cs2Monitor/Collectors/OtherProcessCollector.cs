using System.Diagnostics;
using Cs2Monitor.Native;

namespace Cs2Monitor.Collectors;

/// <summary>
/// Which *other* processes use CPU and cause hard page faults — to see whether something outside the game
/// (antivirus, updates, browser, …) competes with it during a stall. Measured once per second
/// (one process snapshot costs a few ms); samples in between leave these fields empty.
/// </summary>
internal sealed class OtherProcessCollector
{
    private const int IntervalMs = 1000;
    private const int TopCount = 3;

    private readonly Stopwatch _timer = new();
    private readonly List<Win32.ProcessEntry> _snapshot = new(512);
    private Dictionary<int, Win32.ProcessEntry> _last = new();
    private readonly int _ownPid = Environment.ProcessId;

    public void Collect(Sample s, int? gamePid)
    {
        if (_timer.IsRunning && _timer.ElapsedMilliseconds < IntervalMs) return;
        double seconds = _timer.IsRunning ? _timer.Elapsed.TotalSeconds : 0;
        _timer.Restart();
        if (!Win32.GetProcesses(_snapshot)) return;

        var current = new Dictionary<int, Win32.ProcessEntry>(_snapshot.Count);
        foreach (var p in _snapshot) current[p.Pid] = p;
        var previous = _last;
        _last = current;
        if (seconds <= 0) return;

        var cpu = new List<(string Name, double Cores)>();
        var faults = new List<(string Name, double PerSec)>();
        double total = 0;
        foreach (var p in _snapshot)
        {
            if (p.Pid == 0 || p.Pid == gamePid) continue; // Idle, the game
            if (!previous.TryGetValue(p.Pid, out var before) || before.CreateTime != p.CreateTime) continue; // new / reused pid
            double cores = (p.CpuTime - before.CpuTime) / 1e7 / seconds;
            if (cores > 0)
            {
                total += cores;
                cpu.Add((p.Pid == _ownPid ? p.Name + " (this monitor)" : p.Name, cores));
            }
            double hf = (p.HardFaults - before.HardFaults) / seconds;
            if (hf > 0) faults.Add((p.Name, hf));
        }

        s.OtherCpuCores = total;
        s.OtherTopCpu = cpu.OrderByDescending(x => x.Cores).Take(TopCount).ToArray();
        s.OtherTopHardFaults = faults.OrderByDescending(x => x.PerSec).Take(TopCount).ToArray();
        if (gamePid is int gp && previous.TryGetValue(gp, out var gb) && current.TryGetValue(gp, out var ga) && gb.CreateTime == ga.CreateTime)
            s.GameHardFaultsPerSec = (ga.HardFaults - gb.HardFaults) / seconds;
    }
}
