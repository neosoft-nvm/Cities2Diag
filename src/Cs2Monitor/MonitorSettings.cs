using System.Text.Json;

namespace Cs2Monitor;

/// <summary>
/// User settings, stored as JSON in Documents\CS2StallInvestigator\settings.json.
/// The file is created with defaults on first run and can be edited by hand.
/// </summary>
public sealed class MonitorSettings
{
    public static readonly string RootDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CS2StallInvestigator");

    public static string FilePath => Path.Combine(RootDirectory, "settings.json");

    /// <summary>Process name without ".exe".</summary>
    public string ProcessName { get; set; } = "Cities2";

    /// <summary>Time between samples. 200 ms = 5 samples/s.</summary>
    public int SampleIntervalMs { get; set; } = 200;

    public string LogDirectory { get; set; } = Path.Combine(RootDirectory, "Sessions");

    /// <summary>Windows "GPU Engine" counters. Accurate per process, but the most expensive collector.</summary>
    public bool EnableGpuEngineCounters { get; set; } = true;

    public bool EnableNvml { get; set; } = true;

    /// <summary>Per-thread CPU of the game (main thread, busiest thread).</summary>
    public bool EnableThreadSampling { get; set; } = true;

    public int ThreadListRefreshMs { get; set; } = 2000;

    public int UiRefreshMs { get; set; } = 500;

    /// <summary>How much history is kept in memory (for the chart now, for event capture in Phase 2).</summary>
    public int HistorySeconds { get; set; } = 120;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static MonitorSettings Load()
    {
        MonitorSettings settings;
        try
        {
            settings = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<MonitorSettings>(File.ReadAllText(FilePath)) ?? new MonitorSettings()
                : new MonitorSettings();
        }
        catch (Exception)
        {
            settings = new MonitorSettings(); // unreadable file: run with defaults, keep the user's file untouched
            return settings.Clamp();
        }

        settings.Clamp();
        try
        {
            Directory.CreateDirectory(RootDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions)); // also adds new keys
        }
        catch (Exception) { /* read-only location: still usable */ }
        return settings;
    }

    private MonitorSettings Clamp()
    {
        SampleIntervalMs = Math.Clamp(SampleIntervalMs, 50, 5000);
        ThreadListRefreshMs = Math.Clamp(ThreadListRefreshMs, 500, 30000);
        UiRefreshMs = Math.Clamp(UiRefreshMs, 100, 5000);
        HistorySeconds = Math.Clamp(HistorySeconds, 10, 3600);
        if (string.IsNullOrWhiteSpace(ProcessName)) ProcessName = "Cities2";
        if (ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) ProcessName = ProcessName[..^4];
        return this;
    }
}
