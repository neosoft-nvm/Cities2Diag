using System;
using System.Runtime.InteropServices;

namespace PerformanceDetective
{
    /// <summary>Win32 calls used to measure the game process and the system from inside the game.</summary>
    internal static class Native
    {
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

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        public static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll")]
        public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        [DllImport("psapi.dll")]
        public static extern bool GetProcessMemoryInfo(IntPtr process, out PROCESS_MEMORY_COUNTERS_EX counters, uint cb);

        [DllImport("kernel32.dll")]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

        [DllImport("winmm.dll")]
        private static extern bool PlaySound(byte[] sound, IntPtr module, uint flags);

        private const uint SND_ASYNC = 0x0001, SND_MEMORY = 0x0004, SND_NODEFAULT = 0x0002;

        public static bool IsWindows =>
            Environment.OSVersion.Platform == PlatformID.Win32NT;

        public static bool TryGetMemory(out MEMORYSTATUSEX status)
        {
            status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            return IsWindows && GlobalMemoryStatusEx(ref status);
        }

        private static byte[] s_Beep;
        // PlaySound with SND_ASYNC keeps using the buffer after returning, so it must stay referenced.
        private static byte[] s_Playing;

        /// <summary>Two short rising tones, generated in memory (independent of the Windows sound scheme).</summary>
        public static void Beep()
        {
            if (!IsWindows) return;
            try
            {
                if (s_Beep == null) s_Beep = MakeBeep();
                s_Playing = s_Beep;
                PlaySound(s_Playing, IntPtr.Zero, SND_ASYNC | SND_MEMORY | SND_NODEFAULT);
            }
            catch (Exception)
            {
                // no audio device / winmm unavailable
            }
        }

        private static byte[] MakeBeep()
        {
            const int rate = 22050;
            var samples = new System.Collections.Generic.List<short>();
            void Note(double hz, int ms)
            {
                int n = rate * ms / 1000;
                for (int i = 0; i < n; i++)
                {
                    double env = Math.Min(1.0, Math.Min(i, n - i) / (rate * 0.005));
                    samples.Add((short)(Math.Sin(2 * Math.PI * hz * i / rate) * env * 9000));
                }
            }
            Note(880, 90);
            Note(0, 40);
            Note(1320, 110);

            var ms2 = new System.IO.MemoryStream();
            var w = new System.IO.BinaryWriter(ms2);
            int dataBytes = samples.Count * 2;
            w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' }); w.Write(36 + dataBytes);
            w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
            w.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' }); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' }); w.Write(dataBytes);
            foreach (var s in samples) w.Write(s);
            return ms2.ToArray();
        }
    }
}
