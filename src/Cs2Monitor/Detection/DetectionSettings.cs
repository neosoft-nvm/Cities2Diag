namespace Cs2Monitor;

/// <summary>
/// Stall detector configuration (the "Detection" section of settings.json).
/// Every rule compares one metric (see Metrics.cs for keys) with its rolling baseline:
/// the median of that metric over the last BaselineSeconds of normal samples.
/// </summary>
public sealed class DetectionSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Rolling window used to compute "normal" for each metric.</summary>
    public int BaselineSeconds { get; set; } = 60;

    /// <summary>Normal samples needed after the game starts before detection is armed.</summary>
    public int WarmupSeconds { get; set; } = 30;

    /// <summary>Abnormal behaviour must last this long to count as a stall (ignores one-frame spikes).</summary>
    public int MinStallMs { get; set; } = 2000;

    /// <summary>Share of samples that must be abnormal while a stall builds up (tolerates brief gaps).</summary>
    public double MinAbnormalFraction { get; set; } = 0.7;

    /// <summary>A stall ends after this long without abnormal samples.</summary>
    public int EndHoldMs { get; set; } = 1000;

    /// <summary>Longer stalls are cut off and saved (e.g. game paused in a menu).</summary>
    public int MaxStallSeconds { get; set; } = 120;

    public int PreEventSeconds { get; set; } = 20;
    public int PostEventSeconds { get; set; } = 20;

    public List<DetectionRule> Rules { get; set; } = DefaultRules();

    public static List<DetectionRule> DefaultRules() => new()
    {
        new() { Name = "GPU load drop", Metric = "gpu_util", Direction = "Below", Delta = 30 },
        new() { Name = "Frame time rise", Metric = "frametime_avg_ms", Direction = "Above", Factor = 1.5, Delta = 5 },
        new() { Name = "Game main thread saturated", Metric = "main_thread_pct", Direction = "Above", Absolute = 95, Delta = 15 },
        new() { Name = "Game CPU rise", Metric = "game_cores_busy", Direction = "Above", Delta = 1.0 },
        // Paging comes in bursts on a busy PC; as a trigger it creates/extends false stalls. Recorded as context instead.
        new() { Name = "Hard page faults", Metric = "hard_faults", Direction = "Above", Absolute = 1000, Role = "Context" },
        new() { Name = "Game not responding", Metric = "hung", Direction = "Above", Absolute = 0.5 },
        new() { Name = "Monitor itself delayed", Metric = "interval_ms", Direction = "Above", Factor = 2.0 },
    };

    internal void Clamp()
    {
        BaselineSeconds = Math.Clamp(BaselineSeconds, 10, 600);
        WarmupSeconds = Math.Clamp(WarmupSeconds, 5, 600);
        MinStallMs = Math.Clamp(MinStallMs, 200, 60000);
        MinAbnormalFraction = Math.Clamp(MinAbnormalFraction, 0.1, 1.0);
        EndHoldMs = Math.Clamp(EndHoldMs, 200, 30000);
        MaxStallSeconds = Math.Clamp(MaxStallSeconds, 10, 600);
        PreEventSeconds = Math.Clamp(PreEventSeconds, 5, 120);
        PostEventSeconds = Math.Clamp(PostEventSeconds, 5, 120);
        Rules ??= DefaultRules();
    }
}

/// <summary>
/// Fires when the metric is on the given side of its baseline. Every condition that is set must hold:
///   Above: value ≥ Absolute,  value ≥ baseline + Delta,  value ≥ baseline × Factor
///   Below: value ≤ Absolute,  value ≤ baseline − Delta,  value ≤ baseline × Factor
/// </summary>
public sealed class DetectionRule
{
    public string Name { get; set; } = "";
    public string Metric { get; set; } = "";
    public string Direction { get; set; } = "Above";
    public double? Absolute { get; set; }
    public double? Delta { get; set; }
    public double? Factor { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// "Trigger": makes a sample abnormal (starts / extends stalls).
    /// "Context": only recorded (per sample and in events), never starts or extends a stall.
    /// </summary>
    public string Role { get; set; } = "Trigger";

    public bool IsContext => Role.Equals("Context", StringComparison.OrdinalIgnoreCase);
}
