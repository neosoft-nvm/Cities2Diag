namespace Cs2Monitor;

/// <summary>
/// Named, derived values that detection rules, event summaries and charts can refer to by key.
/// </summary>
public sealed record Metric(
    string Key,
    string Label,
    string Unit,
    Func<Sample, double?> Get,
    double NotableAbs,   // an event observation is reported only if |during − before| ≥ NotableAbs …
    double NotableRel);  // … and ≥ NotableRel × |before|

public static class Metrics
{
    public static readonly Metric[] All =
    {
        new("gpu_util", "GPU load", "%", GpuUtil, 15, 0.15),
        new("gpu_3d_game", "GPU 3D (game)", "%", s => s.ProcGpu3dPct, 15, 0.15),
        new("fps", "FPS", "fps", s => s.Fps, 5, 0.10),
        new("frametime_avg_ms", "Frame time (avg)", "ms", s => s.FrameTimeAvgMs, 3, 0.15),
        new("frametime_max_ms", "Frame time (max)", "ms", s => s.FrameTimeMaxMs, 5, 0.25),
        new("frame_cpu_busy_ms", "Frame CPU busy", "ms", s => s.FrameCpuBusyAvgMs, 2, 0.15),
        new("frame_gpu_busy_ms", "Frame GPU busy", "ms", s => s.FrameGpuBusyAvgMs, 2, 0.15),
        new("cpu_busy", "System CPU", "%", s => s.CpuBusyPct, 10, 0.15),
        new("max_core_busy", "Busiest core", "%", s => s.MaxCoreBusyPct, 10, 0.10),
        new("cpu_freq_mhz", "CPU clock", "MHz", s => s.CpuFreqMhz, 200, 0.05),
        new("game_cores_busy", "Game CPU", "cores", s => s.ProcCoresBusy, 0.5, 0.15),
        new("main_thread_pct", "Game main thread", "%", s => s.MainThreadPct, 15, 0.15),
        new("top_thread_pct", "Game busiest thread", "%", s => s.TopThreadPct, 15, 0.15),
        new("threads_over_90", "Game threads ≥ 90%", "", s => s.ThreadsOver90, 1, 0.5),
        new("ram_used_mb", "RAM used", "MB", s => s.RamTotalMb - s.RamAvailMb, 300, 0.02),
        new("commit_used_mb", "Commit", "MB", s => s.CommitUsedMb, 300, 0.02),
        new("hard_faults", "Hard faults (pages in/s)", "/s", s => s.HardFaultPagesInPerSec, 200, 1.0),
        new("page_faults", "Page faults", "/s", s => s.PageFaultsPerSec, 5000, 0.5),
        new("game_private_mb", "Game private bytes", "MB", s => s.ProcPrivateMb, 200, 0.02),
        new("game_working_set_mb", "Game working set", "MB", s => s.ProcWorkingSetMb, 200, 0.02),
        new("game_io_read_mb_s", "Game disk read", "MB/s", s => s.ProcIoReadMbS, 5, 0.5),
        new("disk_queue", "Disk queue", "", s => s.DiskQueue, 1, 0.5),
        new("vram_used_mb", "VRAM used", "MB", VramUsed, 200, 0.03),
        new("game_vram_mb", "VRAM (game)", "MB", s => s.ProcVramDedicatedMb, 200, 0.03),
        new("gpu_clock_mhz", "GPU clock", "MHz", s => s.Gpus.Length > 0 ? s.Gpus.Max(g => g.CoreClockMhz ?? 0) : null, 150, 0.1),
        new("gpu_temp_c", "GPU temperature", "°C", s => s.Gpus.Length > 0 ? s.Gpus.Max(g => g.TempC ?? 0) : null, 5, 0.05),
        new("interval_ms", "Monitor sample interval", "ms", s => s.IntervalMs, 100, 0.5),
        new("hung", "Game not responding", "", s => s.Hung switch { true => 1, false => 0, null => null }, 1, 0),
    };

    private static readonly Dictionary<string, Metric> ByKey = All.ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);

    public static Metric? Find(string key) => ByKey.GetValueOrDefault(key);

    /// <summary>NVIDIA hardware load of the busiest GPU; Windows engine counter when NVML is unavailable.</summary>
    public static double? GpuUtil(Sample s)
    {
        double? best = null;
        foreach (var g in s.Gpus)
            if (g.UtilPct.HasValue && (best == null || g.UtilPct > best)) best = g.UtilPct;
        return best ?? s.GpuEngineMaxPct;
    }

    private static double? VramUsed(Sample s)
    {
        double? best = null;
        foreach (var g in s.Gpus)
            if (g.VramUsedMb.HasValue && (best == null || g.VramUsedMb > best)) best = g.VramUsedMb;
        return best ?? s.GpuAdapterDedicatedMaxMb;
    }
}
