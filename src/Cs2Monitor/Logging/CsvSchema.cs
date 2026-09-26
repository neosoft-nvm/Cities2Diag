using System.Globalization;

namespace Cs2Monitor.Logging;

/// <summary>Column definitions for samples.csv. Empty cell = not measured.</summary>
internal static class CsvSchema
{
    public sealed record Column(string Header, Func<Sample, string> Get);

    public static List<Column> Build(int coreCount, int gpuCount)
    {
        var cols = new List<Column>
        {
            new("utc", s => s.Utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)),
            new("t_ms", s => F(s.TMs)),
            new("interval_ms", s => F(s.IntervalMs)),
            new("sample_cost_ms", s => F(s.CostMs)),
            new("game_running", s => s.GameRunning ? "1" : "0"),
            new("pid", s => s.GameRunning ? s.Pid.ToString(CultureInfo.InvariantCulture) : ""),
            new("game_hung", s => s.Hung switch { true => "1", false => "0", null => "" }),

            new("game_cores_busy", s => F(s.ProcCoresBusy)),
            new("game_cpu_pct", s => F(s.ProcCpuPct)),
            new("game_threads", s => I(s.ThreadCount)),
            new("game_main_thread_pct", s => F(s.MainThreadPct)),
            new("game_top_thread_pct", s => F(s.TopThreadPct)),
            new("game_top_thread_id", s => I(s.TopThreadId)),
            new("game_top_thread_name", s => Text(s.TopThreadName)),
            new("game_2nd_thread_pct", s => F(s.SecondThreadPct)),
            new("game_threads_over_90pct", s => I(s.ThreadsOver90)),
            new("game_working_set_mb", s => F(s.ProcWorkingSetMb)),
            new("game_private_mb", s => F(s.ProcPrivateMb)),
            new("game_page_faults_per_s", s => F(s.ProcPageFaultsPerSec)),
            new("game_io_read_mb_s", s => F(s.ProcIoReadMbS)),
            new("game_io_write_mb_s", s => F(s.ProcIoWriteMbS)),
            new("game_gpu_3d_pct", s => F(s.ProcGpu3dPct)),
            new("game_gpu_max_engine_pct", s => F(s.ProcGpuMaxEnginePct)),
            new("game_vram_dedicated_mb", s => F(s.ProcVramDedicatedMb)),
            new("game_vram_shared_mb", s => F(s.ProcVramSharedMb)),

            new("cpu_busy_pct", s => F(s.CpuBusyPct)),
            new("cpu_utility_pct", s => F(s.CpuUtilityPct)),
            new("cpu_freq_mhz", s => F(s.CpuFreqMhz)),
            new("cpu_max_core_busy_pct", s => F(s.MaxCoreBusyPct)),
        };

        for (int i = 0; i < coreCount; i++)
        {
            int idx = i;
            cols.Add(new($"core{idx}_busy_pct", s => idx < s.CoreBusyPct.Length ? F(s.CoreBusyPct[idx]) : ""));
        }

        cols.AddRange(new Column[]
        {
            new("ram_total_mb", s => F(s.RamTotalMb)),
            new("ram_avail_mb", s => F(s.RamAvailMb)),
            new("memory_load_pct", s => F(s.MemoryLoadPct)),
            new("commit_used_mb", s => F(s.CommitUsedMb)),
            new("commit_limit_mb", s => F(s.CommitLimitMb)),
            new("pagefile_usage_pct", s => F(s.PagefileUsagePct)),
            new("hard_fault_pages_in_per_s", s => F(s.HardFaultPagesInPerSec)),
            new("page_faults_per_s", s => F(s.PageFaultsPerSec)),
            new("disk_read_mb_s", s => F(s.DiskReadMbS)),
            new("disk_write_mb_s", s => F(s.DiskWriteMbS)),
            new("disk_queue", s => F(s.DiskQueue)),
            new("gpu_engine_max_pct", s => F(s.GpuEngineMaxPct)),
            new("gpu_3d_pct", s => F(s.Gpu3dPct)),
            new("gpu_adapter_dedicated_max_mb", s => F(s.GpuAdapterDedicatedMaxMb)),
        });

        for (int i = 0; i < gpuCount; i++)
        {
            int g = i;
            GpuSample? G(Sample s) => g < s.Gpus.Length ? s.Gpus[g] : null;
            cols.Add(new($"nv{g}_util_pct", s => F(G(s)?.UtilPct)));
            cols.Add(new($"nv{g}_memctrl_util_pct", s => F(G(s)?.MemCtrlUtilPct)));
            cols.Add(new($"nv{g}_vram_used_mb", s => F(G(s)?.VramUsedMb)));
            cols.Add(new($"nv{g}_temp_c", s => F(G(s)?.TempC)));
            cols.Add(new($"nv{g}_core_clock_mhz", s => F(G(s)?.CoreClockMhz)));
            cols.Add(new($"nv{g}_mem_clock_mhz", s => F(G(s)?.MemClockMhz)));
            cols.Add(new($"nv{g}_power_w", s => F(G(s)?.PowerW)));
            cols.Add(new($"nv{g}_throttle_reasons", s => G(s)?.ThrottleReasons is ulong r ? "0x" + r.ToString("X", CultureInfo.InvariantCulture) : ""));
        }

        cols.Add(new("marker", s => Text(s.Marker)));
        return cols;
    }

    private static string F(double? v) => v.HasValue && double.IsFinite(v.Value) ? v.Value.ToString("0.###", CultureInfo.InvariantCulture) : "";
    private static string I(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static string Text(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        return v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}
