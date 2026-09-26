using System.Text;
using Cs2Monitor.Native;

namespace Cs2Monitor.Collectors;

/// <summary>Hardware-level NVIDIA GPU values via NVML. Silently unavailable on non-NVIDIA systems.</summary>
internal sealed class NvmlCollector : IDisposable
{
    private readonly IntPtr[] _devices = Array.Empty<IntPtr>();
    private bool _throttleSupported = true;
    private bool _powerSupported = true;
    private const int SlowRefreshMs = 1000;
    private readonly System.Diagnostics.Stopwatch _slowTimer = new();
    private GpuSample[] _slow = Array.Empty<GpuSample>();

    public bool Available { get; }
    public string? Error { get; }
    public string[] Names { get; } = Array.Empty<string>();
    public double[] VramTotalMb { get; } = Array.Empty<double>();

    public NvmlCollector()
    {
        try
        {
            int r = NvmlNative.nvmlInit_v2();
            if (r != 0) { Error = $"nvmlInit failed ({r})"; return; }
            if (NvmlNative.nvmlDeviceGetCount_v2(out uint count) != 0) { Error = "nvmlDeviceGetCount failed"; return; }

            _devices = new IntPtr[count];
            _slow = new GpuSample[count];
            Names = new string[count];
            VramTotalMb = new double[count];
            for (uint i = 0; i < count; i++)
            {
                NvmlNative.nvmlDeviceGetHandleByIndex_v2(i, out _devices[i]);
                var buf = new byte[96];
                Names[i] = NvmlNative.nvmlDeviceGetName(_devices[i], buf, (uint)buf.Length) == 0
                    ? Encoding.ASCII.GetString(buf).TrimEnd('\0')
                    : $"GPU {i}";
                if (NvmlNative.nvmlDeviceGetMemoryInfo(_devices[i], out var mem) == 0)
                    VramTotalMb[i] = mem.Total / 1048576.0;
            }
            Available = count > 0;
        }
        catch (DllNotFoundException)
        {
            Error = "nvml.dll not found (no NVIDIA driver)";
        }
        catch (EntryPointNotFoundException e)
        {
            Error = "NVML too old: " + e.Message;
        }
    }

    public void Collect(Sample s)
    {
        if (!Available) return;

        // Each NVML call blocks for ~0.3–3 ms in the driver. Utilisation and VRAM are read every sample;
        // temperature, clocks, power and throttle reasons change slowly and are refreshed once per second
        // (values in between repeat the last reading).
        bool slow = !_slowTimer.IsRunning || _slowTimer.ElapsedMilliseconds >= SlowRefreshMs;
        if (slow) _slowTimer.Restart();

        var gpus = new GpuSample[_devices.Length];
        for (int i = 0; i < _devices.Length; i++)
        {
            var d = _devices[i];
            ref var g = ref gpus[i];
            if (NvmlNative.nvmlDeviceGetUtilizationRates(d, out var u) == 0)
            {
                g.UtilPct = u.Gpu;
                g.MemCtrlUtilPct = u.Memory;
            }
            if (NvmlNative.nvmlDeviceGetMemoryInfo(d, out var m) == 0)
            {
                g.VramUsedMb = m.Used / 1048576.0;
                g.VramTotalMb = m.Total / 1048576.0;
            }
            if (slow) ReadSlow(d, ref _slow[i]);
            g.TempC = _slow[i].TempC;
            g.CoreClockMhz = _slow[i].CoreClockMhz;
            g.MemClockMhz = _slow[i].MemClockMhz;
            g.PowerW = _slow[i].PowerW;
            g.ThrottleReasons = _slow[i].ThrottleReasons;
        }
        s.Gpus = gpus;
    }

    private void ReadSlow(IntPtr d, ref GpuSample g)
    {
        g = default;
        if (NvmlNative.nvmlDeviceGetTemperature(d, NvmlNative.NVML_TEMPERATURE_GPU, out uint t) == 0) g.TempC = t;
        if (NvmlNative.nvmlDeviceGetClockInfo(d, NvmlNative.NVML_CLOCK_GRAPHICS, out uint gc) == 0) g.CoreClockMhz = gc;
        if (NvmlNative.nvmlDeviceGetClockInfo(d, NvmlNative.NVML_CLOCK_MEM, out uint mc) == 0) g.MemClockMhz = mc;
        if (_powerSupported)
        {
            try { if (NvmlNative.nvmlDeviceGetPowerUsage(d, out uint mw) == 0) g.PowerW = mw / 1000.0; }
            catch (EntryPointNotFoundException) { _powerSupported = false; }
        }
        if (_throttleSupported)
        {
            try { if (NvmlNative.nvmlDeviceGetCurrentClocksThrottleReasons(d, out ulong reasons) == 0) g.ThrottleReasons = reasons; }
            catch (EntryPointNotFoundException) { _throttleSupported = false; }
        }
    }

    /// <summary>Human-readable NVML clock throttle reasons (bitmask from nvml.h).</summary>
    public static string DescribeThrottle(ulong reasons)
    {
        if (reasons == 0) return "none";
        var parts = new List<string>();
        if ((reasons & 0x01) != 0) parts.Add("idle");
        if ((reasons & 0x02) != 0) parts.Add("app clocks");
        if ((reasons & 0x04) != 0) parts.Add("power cap");
        if ((reasons & 0x08) != 0) parts.Add("HW slowdown");
        if ((reasons & 0x10) != 0) parts.Add("sync boost");
        if ((reasons & 0x20) != 0) parts.Add("SW thermal");
        if ((reasons & 0x40) != 0) parts.Add("HW thermal");
        if ((reasons & 0x80) != 0) parts.Add("HW power brake");
        if ((reasons & 0x100) != 0) parts.Add("display clocks");
        return parts.Count > 0 ? string.Join(", ", parts) : $"0x{reasons:X}";
    }

    public void Dispose()
    {
        if (Available)
        {
            try { NvmlNative.nvmlShutdown(); } catch { /* ignore */ }
        }
    }
}
