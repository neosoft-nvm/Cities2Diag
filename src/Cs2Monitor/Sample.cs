namespace Cs2Monitor;

/// <summary>
/// One snapshot of everything the monitor measured at one instant.
/// A null value means "not measured / not available", never "zero".
/// </summary>
public sealed class Sample
{
    // Timing of the sampler itself
    public DateTime Utc;
    public double QpcMs;        // QueryPerformanceCounter in ms: same clock as PresentMon's CPUStartQPCTimeInMs
    public double TMs;          // ms since the monitor started (monotonic)
    public double IntervalMs;   // actual time since the previous sample; large values = the monitor itself was delayed
    public double CostMs;       // time spent collecting this sample

    // Game process
    public bool GameRunning;
    public int Pid;
    public bool? Hung;          // IsHungAppWindow: window has not pumped messages for ~5 s

    public double? ProcCoresBusy;   // CPU time / wall time, 1.0 = one logical core fully used
    public double? ProcCpuPct;      // same, as % of the whole machine (Task Manager "CPU" column)

    public int? ThreadCount;
    public double? MainThreadPct;   // % of one core used by the process's first (oldest) thread
    public double? TopThreadPct;    // % of one core used by the busiest thread
    public int? TopThreadId;
    public string? TopThreadName;
    public double? SecondThreadPct;
    public int? ThreadsOver90;

    public double? ProcWorkingSetMb;
    public double? ProcPrivateMb;
    public double? ProcPageFaultsPerSec;
    public double? ProcIoReadMbS;
    public double? ProcIoWriteMbS;

    public double? ProcGpu3dPct;        // Windows "GPU Engine" counters, 3D engine, this process
    public double? ProcGpuMaxEnginePct; // busiest engine for this process
    public double? ProcVramDedicatedMb;
    public double? ProcVramSharedMb;

    // System CPU
    public double? CpuBusyPct;      // % Processor Time (classic busy time, max 100)
    public double? CpuUtilityPct;   // % Processor Utility (Task Manager's value, frequency-scaled, can exceed 100)
    public double? CpuFreqMhz;      // nominal frequency x % Processor Performance
    public double?[] CoreBusyPct = Array.Empty<double?>();

    // System memory
    public double? RamTotalMb;
    public double? RamAvailMb;
    public double? MemoryLoadPct;
    public double? CommitUsedMb;
    public double? CommitLimitMb;
    public double? PagefileUsagePct;
    public double? HardFaultPagesInPerSec; // \Memory\Pages Input/sec: pages read from disk to resolve faults
    public double? PageFaultsPerSec;

    // System disk
    public double? DiskReadMbS;
    public double? DiskWriteMbS;
    public double? DiskQueue;

    // System GPU (Windows counters, all processes)
    public double? GpuEngineMaxPct; // like Task Manager's overall GPU %
    public double? Gpu3dPct;
    public double? GpuAdapterDedicatedMaxMb;

    // Per NVIDIA GPU (NVML)
    public GpuSample[] Gpus = Array.Empty<GpuSample>();

    // Frame timing (PresentMon) for the frames whose CPU work started within this sample's interval.
    // Filled in shortly after the sample is taken ("settling"), once PresentMon has delivered those frames.
    public int? Frames;
    public double? Fps;
    public double? FrameTimeAvgMs;     // MsBetweenPresents
    public double? FrameTimeMaxMs;
    public double? FrameCpuBusyAvgMs;  // MsCPUBusy: CPU time the app spent on the frame
    public double? FrameGpuBusyAvgMs;  // MsGPUBusy: time the GPU was working on the frame

    // Stall detector output
    public string? Phase;              // idle, warmup, normal, warning, stall, recovery
    public bool Abnormal;
    public string? RulesFired;         // trigger rules
    public string? ContextFired;       // context rules (recorded only)

    public string? Marker;

    public double? MaxCoreBusyPct
    {
        get
        {
            double? max = null;
            foreach (var c in CoreBusyPct)
                if (c.HasValue && (max == null || c > max)) max = c;
            return max;
        }
    }
}

public struct GpuSample
{
    public double? UtilPct;
    public double? MemCtrlUtilPct;
    public double? VramUsedMb;
    public double? VramTotalMb;
    public double? TempC;
    public double? CoreClockMhz;
    public double? MemClockMhz;
    public double? PowerW;
    public ulong? ThrottleReasons;
}
