using Cs2Monitor.Native;

namespace Cs2Monitor.Collectors;

/// <summary>
/// System-wide counters from PDH plus GlobalMemoryStatusEx.
/// Also resolves per-process GPU usage, because those values come from the same wildcard counters.
/// </summary>
internal sealed class SystemCollector : IDisposable
{
    private readonly PdhQuery _query = new();
    private readonly PdhCounter? _cpuBusy, _cpuUtility, _cpuPerf, _cpuNominalFreq, _coreBusy;
    private readonly PdhCounter? _pagesInput, _pageFaults, _pagefileUsage;
    private readonly PdhCounter? _diskRead, _diskWrite, _diskQueue;
    private readonly PdhCounter? _gpuEngine, _gpuProcDedicated, _gpuProcShared, _gpuAdapterDedicated;

    private readonly List<PdhItem> _items = new(512);
    private readonly Dictionary<string, double> _engineSum = new();
    private readonly Dictionary<string, double> _procEngineSum = new();
    private readonly int _coreCount = Environment.ProcessorCount;

    public List<string> Unavailable { get; } = new();

    public SystemCollector(bool gpuEngineCounters)
    {
        _cpuBusy = Add(@"\Processor Information(_Total)\% Processor Time");
        _cpuUtility = Add(@"\Processor Information(_Total)\% Processor Utility");
        _cpuPerf = Add(@"\Processor Information(_Total)\% Processor Performance");
        _cpuNominalFreq = Add(@"\Processor Information(_Total)\Processor Frequency");
        _coreBusy = Add(@"\Processor Information(*)\% Processor Time");

        _pagesInput = Add(@"\Memory\Pages Input/sec");
        _pageFaults = Add(@"\Memory\Page Faults/sec");
        _pagefileUsage = Add(@"\Paging File(_Total)\% Usage");

        _diskRead = Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        _diskWrite = Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
        _diskQueue = Add(@"\PhysicalDisk(_Total)\Avg. Disk Queue Length");

        if (gpuEngineCounters)
        {
            _gpuEngine = Add(@"\GPU Engine(*)\Utilization Percentage");
            _gpuProcDedicated = Add(@"\GPU Process Memory(*)\Dedicated Usage");
            _gpuProcShared = Add(@"\GPU Process Memory(*)\Shared Usage");
            _gpuAdapterDedicated = Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
        }

        _query.Collect(); // prime rate counters
    }

    private PdhCounter? Add(string path)
    {
        var c = _query.TryAdd(path);
        if (c == null) Unavailable.Add(path);
        return c;
    }

    public void Collect(Sample s, int? pid)
    {
        _query.Collect();

        s.CpuBusyPct = _cpuBusy?.Value();
        s.CpuUtilityPct = _cpuUtility?.Value();
        var perf = _cpuPerf?.Value();
        var nominal = _cpuNominalFreq?.Value();
        if (perf.HasValue && nominal.HasValue) s.CpuFreqMhz = nominal.Value * perf.Value / 100.0;

        s.CoreBusyPct = new double?[_coreCount];
        if (_coreBusy != null && _coreBusy.ReadArray(_items))
        {
            // Instances look like "0,5" (group,index); skip "_Total" and "0,_Total".
            foreach (var item in _items)
            {
                int comma = item.Instance.IndexOf(',');
                if (comma < 0) continue;
                if (!int.TryParse(item.Instance.AsSpan(0, comma), out int group)) continue;
                if (!int.TryParse(item.Instance.AsSpan(comma + 1), out int index)) continue;
                int slot = group * 64 + index;
                if (slot < _coreCount) s.CoreBusyPct[slot] = Math.Min(100, item.Value);
            }
        }

        var mem = new Win32.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MEMORYSTATUSEX>() };
        if (Win32.GlobalMemoryStatusEx(ref mem))
        {
            s.RamTotalMb = mem.ullTotalPhys / 1048576.0;
            s.RamAvailMb = mem.ullAvailPhys / 1048576.0;
            s.MemoryLoadPct = mem.dwMemoryLoad;
            s.CommitLimitMb = mem.ullTotalPageFile / 1048576.0;
            s.CommitUsedMb = (mem.ullTotalPageFile - mem.ullAvailPageFile) / 1048576.0;
        }
        s.HardFaultPagesInPerSec = _pagesInput?.Value();
        s.PageFaultsPerSec = _pageFaults?.Value();
        s.PagefileUsagePct = _pagefileUsage?.Value();

        s.DiskReadMbS = _diskRead?.Value() / 1048576.0;
        s.DiskWriteMbS = _diskWrite?.Value() / 1048576.0;
        s.DiskQueue = _diskQueue?.Value();

        CollectGpu(s, pid);
    }

    private void CollectGpu(Sample s, int? pid)
    {
        string? pidPrefix = pid.HasValue ? $"pid_{pid.Value}_" : null;

        // Engine instances: "pid_1234_luid_0x..._0x..._phys_0_eng_3_engtype_3D".
        // Task Manager's GPU % = busiest engine, where each engine's load is summed over all processes.
        if (_gpuEngine != null && _gpuEngine.ReadArray(_items))
        {
            _engineSum.Clear();
            _procEngineSum.Clear();
            foreach (var item in _items)
            {
                int luid = item.Instance.IndexOf("_luid_", StringComparison.Ordinal);
                if (luid < 0) continue;
                string engine = item.Instance.Substring(luid + 1);
                _engineSum[engine] = _engineSum.GetValueOrDefault(engine) + item.Value;
                if (pidPrefix != null && item.Instance.StartsWith(pidPrefix, StringComparison.Ordinal))
                    _procEngineSum[engine] = _procEngineSum.GetValueOrDefault(engine) + item.Value;
            }
            s.GpuEngineMaxPct = MaxOf(_engineSum, null);
            s.Gpu3dPct = MaxOf(_engineSum, "engtype_3D");
            if (pidPrefix != null)
            {
                s.ProcGpuMaxEnginePct = MaxOf(_procEngineSum, null) ?? 0;
                s.ProcGpu3dPct = MaxOf(_procEngineSum, "engtype_3D") ?? 0;
            }
        }

        if (pidPrefix != null)
        {
            s.ProcVramDedicatedMb = SumForPid(_gpuProcDedicated, pidPrefix);
            s.ProcVramSharedMb = SumForPid(_gpuProcShared, pidPrefix);
        }

        if (_gpuAdapterDedicated != null && _gpuAdapterDedicated.ReadArray(_items))
        {
            double max = 0;
            foreach (var item in _items) max = Math.Max(max, item.Value);
            s.GpuAdapterDedicatedMaxMb = max / 1048576.0;
        }
    }

    private static double? MaxOf(Dictionary<string, double> sums, string? engineTypeSuffix)
    {
        double? max = null;
        foreach (var (key, value) in sums)
        {
            if (engineTypeSuffix != null && !key.EndsWith(engineTypeSuffix, StringComparison.Ordinal)) continue;
            if (max == null || value > max) max = value;
        }
        return max.HasValue ? Math.Min(100, max.Value) : null;
    }

    private double? SumForPid(PdhCounter? counter, string pidPrefix)
    {
        if (counter == null || !counter.ReadArray(_items)) return null;
        double sum = 0;
        foreach (var item in _items)
            if (item.Instance.StartsWith(pidPrefix, StringComparison.Ordinal)) sum += item.Value;
        return sum / 1048576.0;
    }

    public void Dispose() => _query.Dispose();
}
