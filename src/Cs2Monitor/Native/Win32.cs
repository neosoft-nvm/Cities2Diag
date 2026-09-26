using System.Runtime.InteropServices;
using System.Text;

namespace Cs2Monitor.Native;

internal static class Win32
{
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint SYNCHRONIZE = 0x00100000;
    public const uint THREAD_QUERY_LIMITED_INFORMATION = 0x0800;
    public const uint WAIT_OBJECT_0 = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_MEMORY_COUNTERS_EX
    {
        public uint cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivateUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenThread(uint access, bool inherit, uint tid);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll")]
    public static extern uint WaitForSingleObject(IntPtr h, uint ms);

    // FILETIMEs are marshalled as 64-bit counts of 100 ns units.
    [DllImport("kernel32.dll")]
    public static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    public static extern bool GetThreadTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

    [DllImport("psapi.dll", SetLastError = true)]
    public static extern bool GetProcessMemoryInfo(IntPtr h, out PROCESS_MEMORY_COUNTERS_EX counters, uint cb);

    [DllImport("kernel32.dll")]
    public static extern bool GetProcessIoCounters(IntPtr h, out IO_COUNTERS counters);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    // Returns an HRESULT; the string must be released with LocalFree.
    [DllImport("kernel32.dll")]
    public static extern int GetThreadDescription(IntPtr thread, out IntPtr description);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr mem);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder name, ref uint size);

    [DllImport("user32.dll")]
    public static extern bool IsHungAppWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    private const int SystemProcessInformation = 5;
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    // x64 layouts of SYSTEM_PROCESS_INFORMATION / SYSTEM_THREAD_INFORMATION
    private const int ProcUniqueProcessIdOffset = 0x50;
    private const int ProcThreadsOffset = 0x100;
    private const int ThreadInfoSize = 0x50;
    private const int ThreadUniqueThreadOffset = 0x30;

    private static IntPtr _sysInfoBuffer;
    private static int _sysInfoSize;

    /// <summary>
    /// Thread ids of one process via NtQuerySystemInformation (what Task Manager uses).
    /// Toolhelp's Thread32Next is ~20x slower on a system with thousands of threads.
    /// Not thread-safe: call from the sampler thread only.
    /// </summary>
    public static bool GetThreadIds(int pid, List<uint> into)
    {
        into.Clear();
        if (_sysInfoBuffer == IntPtr.Zero)
        {
            _sysInfoSize = 1 << 20;
            _sysInfoBuffer = Marshal.AllocHGlobal(_sysInfoSize);
        }
        int status;
        while ((status = NtQuerySystemInformation(SystemProcessInformation, _sysInfoBuffer, _sysInfoSize, out int needed)) == STATUS_INFO_LENGTH_MISMATCH)
        {
            Marshal.FreeHGlobal(_sysInfoBuffer);
            _sysInfoSize = Math.Max(needed, _sysInfoSize * 2) + 65536;
            _sysInfoBuffer = Marshal.AllocHGlobal(_sysInfoSize);
        }
        if (status < 0) return false;

        int offset = 0;
        while (true)
        {
            int next = Marshal.ReadInt32(_sysInfoBuffer, offset);
            if (Marshal.ReadInt64(_sysInfoBuffer, offset + ProcUniqueProcessIdOffset) == pid)
            {
                int threads = Marshal.ReadInt32(_sysInfoBuffer, offset + 4);
                for (int i = 0; i < threads; i++)
                    into.Add((uint)Marshal.ReadInt64(_sysInfoBuffer, offset + ProcThreadsOffset + i * ThreadInfoSize + ThreadUniqueThreadOffset));
                return true;
            }
            if (next == 0) return true;
            offset += next;
        }
    }

    public static string? GetProcessImagePath(IntPtr process)
    {
        var sb = new StringBuilder(1024);
        uint size = (uint)sb.Capacity;
        return QueryFullProcessImageNameW(process, 0, sb, ref size) ? sb.ToString() : null;
    }

    public static string? GetThreadName(IntPtr thread)
    {
        try
        {
            if (GetThreadDescription(thread, out var ptr) < 0 || ptr == IntPtr.Zero) return null;
            var name = Marshal.PtrToStringUni(ptr);
            LocalFree(ptr);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (EntryPointNotFoundException)
        {
            return null; // Windows older than 10 1607
        }
    }
}
