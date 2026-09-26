using System.Text.Json;

namespace Cs2Monitor;

/// <summary>
/// User settings, stored as JSON in %LOCALAPPDATA%\CS2StallInvestigator\settings.json.
/// The file is created with defaults on first run and can be edited by hand.
/// </summary>
public sealed class MonitorSettings
{
    /// <summary>Bumped when stored settings need migrating (see Migrate).</summary>
    public const int CurrentVersion = 2;

    /// <summary>
    /// Local, never-synced location. Documents is often redirected into OneDrive; there every log write made
    /// OneDrive and explorer.exe use 2–3 CPU cores for seconds — the monitor disturbing what it measures.
    /// </summary>
    public static readonly string RootDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CS2StallInvestigator");

    /// <summary>Where version 0.1–0.2 kept settings and logs.</summary>
    public static readonly string LegacyRootDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CS2StallInvestigator");

    public static string FilePath => Path.Combine(RootDirectory, "settings.json");

    /// <summary>0 = file written before versioning existed.</summary>
    public int SettingsVersion { get; set; }

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

    /// <summary>Global key for "Capture event", e.g. "Ctrl+Alt+M", "Shift+F9", "Pause" (names from System.Windows.Forms.Keys).</summary>
    public string CaptureHotkey { get; set; } = "Ctrl+Alt+M";

    /// <summary>Two-tone beep when a capture is registered.</summary>
    public bool CaptureSound { get; set; } = true;

    /// <summary>Small "Captured" notice in the top-right corner (not visible over exclusive fullscreen).</summary>
    public bool CaptureToast { get; set; } = true;

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

    /// <summary>
    /// Loads the user's settings file (migrating it from the old Documents location if needed, and rewriting it
    /// with any new keys), or a given file read-only.
    /// </summary>
    public static MonitorSettings Load(string? path = null)
    {
        MonitorSettings settings;
        try
        {
            string file = path ?? FilePath;
            string legacy = Path.Combine(LegacyRootDirectory, "settings.json");
            if (path == null && !File.Exists(file) && File.Exists(legacy)) file = legacy;
            settings = File.Exists(file)
                ? JsonSerializer.Deserialize<MonitorSettings>(File.ReadAllText(file)) ?? new MonitorSettings { SettingsVersion = CurrentVersion }
                : new MonitorSettings { SettingsVersion = CurrentVersion };
        }
        catch (Exception)
        {
            settings = new MonitorSettings(); // unreadable file: run with defaults, keep the user's file untouched
            return settings.Clamp();
        }

        settings.Migrate();
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

    /// <summary>Upgrades settings written by older versions. Only touches values that still have the old default.</summary>
    private void Migrate()
    {
        if (SettingsVersion >= CurrentVersion) return;

        // v2: logs out of Documents (often OneDrive-synced) into %LOCALAPPDATA%.
        var oldDefaultLogs = Path.Combine(LegacyRootDirectory, "Sessions");
        if (string.Equals(Path.GetFullPath(LogDirectory), Path.GetFullPath(oldDefaultLogs), StringComparison.OrdinalIgnoreCase))
            LogDirectory = Path.Combine(RootDirectory, "Sessions");

        // v2: "Game CPU rise" produced mostly false stalls (baseline learned while the game was paused/idle,
        // then normal play looked like a rise). Keep it as context only.
        foreach (var r in Detection?.Rules ?? new List<DetectionRule>())
            if (r.Metric == "game_cores_busy" && r.Name == "Game CPU rise") r.Role = "Context";

        // v2: new context rule, if the file predates it.
        if (Detection?.Rules != null && !Detection.Rules.Any(r => r.Metric == "other_cpu_cores"))
            Detection.Rules.Add(DetectionSettings.DefaultRules().First(r => r.Metric == "other_cpu_cores"));

        SettingsVersion = CurrentVersion;
    }

    /// <summary>True when the path is inside a OneDrive folder (writes there trigger sync activity).</summary>
    public static bool IsInOneDrive(string path)
    {
        foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            var root = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(root) && Path.GetFullPath(path).StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
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
