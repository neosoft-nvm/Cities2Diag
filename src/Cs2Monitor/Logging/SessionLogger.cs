using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Cs2Monitor.Logging;

/// <summary>
/// One session = one run of the game. Writes
///   Sessions\Session_yyyyMMdd_HHmmss\samples.csv   (every sample)
///   Sessions\Session_yyyyMMdd_HHmmss\session.json  (system info, markers, summary)
/// Only called from the sampler thread.
/// </summary>
internal sealed class SessionLogger : IDisposable
{
    private readonly string _root;
    private readonly List<CsvSchema.Column> _columns;
    private readonly StringBuilder _line = new(1024);
    private StreamWriter? _csv;
    private SessionInfo? _info;
    private DateTime _lastFlush;

    public string? SessionDirectory { get; private set; }
    public bool Active => _csv != null;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public SessionLogger(string root, int coreCount, int gpuCount)
    {
        _root = root;
        _columns = CsvSchema.Build(coreCount, gpuCount);
    }

    public void Start(SessionInfo info)
    {
        End();
        Directory.CreateDirectory(_root);
        var name = "Session_" + info.StartedLocal.ToString("yyyyMMdd_HHmmss");
        SessionDirectory = Path.Combine(_root, name);
        Directory.CreateDirectory(SessionDirectory);
        info.SessionId = name;
        _info = info;

        _csv = new StreamWriter(Path.Combine(SessionDirectory, "samples.csv"), false, new UTF8Encoding(false), 1 << 16);
        _csv.WriteLine(string.Join(",", _columns.Select(c => c.Header)));
        _lastFlush = DateTime.UtcNow;
        WriteJson();
    }

    public void Write(Sample s)
    {
        if (_csv == null || _info == null) return;
        _line.Clear();
        for (int i = 0; i < _columns.Count; i++)
        {
            if (i > 0) _line.Append(',');
            _line.Append(_columns[i].Get(s));
        }
        _csv.WriteLine(_line);
        _info.SampleCount++;

        if (s.Marker != null)
        {
            _info.Markers.Add(new MarkerInfo { Utc = s.Utc, TMs = s.TMs, Text = s.Marker });
            _csv.Flush();
            WriteJson();
        }
        else if ((DateTime.UtcNow - _lastFlush).TotalSeconds >= 2)
        {
            _csv.Flush(); // bounded data loss on crash, without a disk write per sample
            _lastFlush = DateTime.UtcNow;
        }
    }

    public void End()
    {
        if (_csv == null || _info == null) return;
        _info.EndedUtc = DateTime.UtcNow;
        _csv.Flush();
        _csv.Dispose();
        _csv = null;
        WriteJson();
        _info = null;
    }

    private void WriteJson()
    {
        if (SessionDirectory == null || _info == null) return;
        try
        {
            File.WriteAllText(Path.Combine(SessionDirectory, "session.json"), JsonSerializer.Serialize(_info, JsonOptions));
        }
        catch (IOException) { /* next write will retry */ }
    }

    public void Dispose() => End();
}

public sealed class SessionInfo
{
    public string SessionId { get; set; } = "";
    public string Tool { get; set; } = "Cs2Monitor " + typeof(SessionInfo).Assembly.GetName().Version;
    public DateTime StartedUtc { get; set; }
    public DateTime StartedLocal { get; set; }
    public DateTime? EndedUtc { get; set; }
    public long SampleCount { get; set; }

    public int GamePid { get; set; }
    public string? GameExePath { get; set; }
    public string? GameFileVersion { get; set; }
    public DateTime? GameProcessStartUtc { get; set; }

    public SystemInfo System { get; set; } = new();
    public MonitorSettings Settings { get; set; } = new();
    public List<string> UnavailableCounters { get; set; } = new();
    public List<MarkerInfo> Markers { get; set; } = new();
}

public sealed class MarkerInfo
{
    public DateTime Utc { get; set; }
    public double TMs { get; set; }
    public string Text { get; set; } = "";
}

public sealed class SystemInfo
{
    public string? MachineName { get; set; }
    public string? Os { get; set; }
    public string? CpuName { get; set; }
    public int LogicalProcessors { get; set; }
    public double RamTotalMb { get; set; }
    public List<string> NvidiaGpus { get; set; } = new();
    public string? NvmlStatus { get; set; }

    public static SystemInfo Gather(double ramTotalMb, string[] gpuNames, double[] gpuVramMb, string? nvmlError)
    {
        var info = new SystemInfo
        {
            MachineName = Environment.MachineName,
            LogicalProcessors = Environment.ProcessorCount,
            RamTotalMb = Math.Round(ramTotalMb),
            NvmlStatus = nvmlError ?? "ok",
        };
        try
        {
            using var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            info.CpuName = (cpu?.GetValue("ProcessorNameString") as string)?.Trim();
            using var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = nt?.GetValue("ProductName") as string ?? "Windows";
            var build = nt?.GetValue("CurrentBuild") as string;
            // Windows 11 still reports "Windows 10" in ProductName.
            if (int.TryParse(build, out int b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
            info.Os = $"{product} {nt?.GetValue("DisplayVersion")} build {build}.{nt?.GetValue("UBR")}";
        }
        catch (Exception) { /* optional */ }
        for (int i = 0; i < gpuNames.Length; i++)
            info.NvidiaGpus.Add($"nv{i}: {gpuNames[i]} ({gpuVramMb[i]:0} MB)");
        return info;
    }
}
