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

    /// <summary>Time span shown in the live chart. (Memory history is sized automatically to fit event capture.)</summary>
    public int HistorySeconds { get; set; } = 120;

    /// <summary>Frame timing via PresentMon (needs admin rights; see README).</summary>
    public bool FrameTimingEnabled { get; set; } = true;

    /// <summary>Path to PresentMon's console exe. Empty = look for tools\PresentMon*.exe next to or above the monitor.</summary>
    public string PresentMonPath { get; set; } = "";

    /// <summary>When the monitor is not running as admin, start PresentMon with a UAC prompt when the game starts.</summary>
    public bool PresentMonAllowUacPrompt { get; set; } = true;

    /// <summary>
    /// Samples are finalised (frame stats filled in, stall detection run, row written) this long after they are taken,
    /// because PresentMon delivers frames with a delay.
    /// </summary>
    public int FrameSettleMs { get; set; } = 2500;

    public DetectionSettings Detection { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Loads the user's settings file (and rewrites it with any new keys), or a given file read-only.</summary>
    public static MonitorSettings Load(string? path = null)
    {
        MonitorSettings settings;
        try
        {
            string file = path ?? FilePath;
            settings = File.Exists(file)
                ? JsonSerializer.Deserialize<MonitorSettings>(File.ReadAllText(file)) ?? new MonitorSettings()
                : new MonitorSettings();
        }
        catch (Exception)
        {
            settings = new MonitorSettings(); // unreadable file: run with defaults, keep the user's file untouched
            return settings.Clamp();
        }

        settings.Clamp();
        if (path != null) return settings;
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
        FrameSettleMs = Math.Clamp(FrameSettleMs, 0, 10000);
        Detection ??= new DetectionSettings();
        Detection.Clamp();
        if (string.IsNullOrWhiteSpace(ProcessName)) ProcessName = "Cities2";
        if (ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) ProcessName = ProcessName[..^4];
        return this;
    }
}
