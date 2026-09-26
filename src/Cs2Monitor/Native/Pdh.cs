using System.Runtime.InteropServices;

namespace Cs2Monitor.Native;

/// <summary>
/// Thin wrapper over the Windows Performance Data Helper (pdh.dll).
/// English counter paths are used so the tool works on non-English Windows.
/// </summary>
internal static class PdhNative
{
    public const uint PDH_MORE_DATA = 0x800007D2;
    public const uint PDH_FMT_DOUBLE = 0x00000200;
    public const uint PDH_FMT_NOCAP100 = 0x00008000;
    public const uint PDH_CSTATUS_VALID_DATA = 0;
    public const uint PDH_CSTATUS_NEW_DATA = 1;

    // x64 layout: DWORD CStatus, 4 bytes padding, 8-byte union.
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    public struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double doubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    public static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    public static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    public static extern uint PdhCloseQuery(IntPtr query);
}

internal readonly record struct PdhItem(string Instance, double Value);

internal sealed class PdhQuery : IDisposable
{
    private IntPtr _query;

    public PdhQuery()
    {
        uint r = PdhNative.PdhOpenQueryW(null, IntPtr.Zero, out _query);
        if (r != 0) throw new InvalidOperationException($"PdhOpenQuery failed: 0x{r:X8}");
    }

    /// <summary>Returns null when the counter does not exist on this machine.</summary>
    public PdhCounter? TryAdd(string path)
    {
        uint r = PdhNative.PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var handle);
        return r == 0 ? new PdhCounter(handle, path) : null;
    }

    public bool Collect() => PdhNative.PdhCollectQueryData(_query) == 0;

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            PdhNative.PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }
}

internal sealed class PdhCounter
{
    private const uint Format = PdhNative.PDH_FMT_DOUBLE | PdhNative.PDH_FMT_NOCAP100;
    private const int ItemSize = 24; // x64 PDH_FMT_COUNTERVALUE_ITEM_W: name pointer + 16-byte value

    private readonly IntPtr _handle;
    private IntPtr _buffer;
    private uint _bufferSize;

    public string Path { get; }

    public PdhCounter(IntPtr handle, string path)
    {
        _handle = handle;
        Path = path;
    }

    /// <summary>Null when the value is not valid yet (rate counters need two collections).</summary>
    public double? Value()
    {
        uint r = PdhNative.PdhGetFormattedCounterValue(_handle, Format, out _, out var v);
        if (r != 0) return null;
        return v.CStatus is PdhNative.PDH_CSTATUS_VALID_DATA or PdhNative.PDH_CSTATUS_NEW_DATA ? v.doubleValue : null;
    }

    /// <summary>Reads all instances of a wildcard counter into <paramref name="into"/> (cleared first).</summary>
    public bool ReadArray(List<PdhItem> into)
    {
        into.Clear();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint size = _bufferSize;
            uint r = PdhNative.PdhGetFormattedCounterArrayW(_handle, Format, ref size, out uint count, _buffer);
            if (r == PdhNative.PDH_MORE_DATA)
            {
                if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
                _bufferSize = size + 4096; // headroom: instances come and go
                _buffer = Marshal.AllocHGlobal((int)_bufferSize);
                continue;
            }
            if (r != 0) return false;

            for (int i = 0; i < count; i++)
            {
                int off = i * ItemSize;
                uint status = (uint)Marshal.ReadInt32(_buffer, off + 8);
                if (status is not (PdhNative.PDH_CSTATUS_VALID_DATA or PdhNative.PDH_CSTATUS_NEW_DATA)) continue;
                string? name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(_buffer, off));
                double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(_buffer, off + 16));
                if (name != null) into.Add(new PdhItem(name, value));
            }
            return true;
        }
        return false;
    }
}
