using System.Runtime.InteropServices;

namespace Cs2Monitor.Native;

/// <summary>
/// NVIDIA Management Library. Ships with the NVIDIA driver (System32\nvml.dll).
/// Every call returns an nvmlReturn_t; 0 = success.
/// </summary>
internal static class NvmlNative
{
    private const string Dll = "nvml.dll";

    public const int NVML_TEMPERATURE_GPU = 0;
    public const int NVML_CLOCK_GRAPHICS = 0;
    public const int NVML_CLOCK_MEM = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct NvmlUtilization
    {
        public uint Gpu;    // % of time a kernel was executing over the driver's last sample period
        public uint Memory; // % of time device memory was being read/written
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [DllImport(Dll)] public static extern int nvmlInit_v2();
    [DllImport(Dll)] public static extern int nvmlShutdown();
    [DllImport(Dll)] public static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport(Dll)] public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport(Dll)] public static extern int nvmlDeviceGetName(IntPtr device, byte[] name, uint length);
    [DllImport(Dll)] public static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);
    [DllImport(Dll)] public static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);
    [DllImport(Dll)] public static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint celsius);
    [DllImport(Dll)] public static extern int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint mhz);
    [DllImport(Dll)] public static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
    [DllImport(Dll)] public static extern int nvmlDeviceGetCurrentClocksThrottleReasons(IntPtr device, out ulong reasons);
}
