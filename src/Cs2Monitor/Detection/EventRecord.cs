namespace Cs2Monitor.Detection;

/// <summary>Contents of CS2_Event_NNNNN.json. Everything in it is measured; nothing is inferred.</summary>
public sealed class EventRecord
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "auto";            // auto | manual
    public bool ConfirmedByUser { get; set; }             // user pressed Capture during an automatic stall
    public string? SessionId { get; set; }
    public DateTime StartUtc { get; set; }                // auto: stall start; manual: key press
    public DateTime? EndUtc { get; set; }
    public double DurationSec { get; set; }               // auto: stall duration; manual: 0
    public bool Truncated { get; set; }
    public string? EndReason { get; set; }
    public string PeriodDefinition { get; set; } = "";

    public string? GameVersion { get; set; }
    public long? Population { get; set; }                 // requires the CS2 mod (Phase 3)
    public string SimulationState { get; set; } = "not measured (requires CS2 mod, Phase 3)";
    public string FrameTiming { get; set; } = "";

    public string Classification { get; set; } = "UNKNOWN / INSUFFICIENT DATA";
    public string ClassificationNote { get; set; } = "Automatic classification is not implemented yet (Phase 5). See Observations and the timeline.";
    public double? Confidence { get; set; }

    public List<RuleHit> RulesFired { get; set; } = new();
    public Dictionary<string, double> BaselinesAtDetection { get; set; } = new();
    public List<MetricSummary> Summary { get; set; } = new();
    public List<string> Observations { get; set; } = new();

    /// <summary>Busiest non-game processes, average CPU (cores) before and during the event.</summary>
    public List<ProcessUse> OtherProcesses { get; set; } = new();
    public Timeline Timeline { get; set; } = new();
}

public sealed class ProcessUse
{
    public string Name { get; set; } = "";
    public double BeforeCores { get; set; }
    public double DuringCores { get; set; }
    public double AfterCores { get; set; }
}

public sealed class RuleHit
{
    public string Name { get; set; } = "";
    public int Samples { get; set; }
}

public sealed class MetricSummary
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Unit { get; set; } = "";
    public double? Before { get; set; }
    public double? During { get; set; }
    public double? After { get; set; }
}

public sealed class Timeline
{
    /// <summary>Seconds relative to StartUtc.</summary>
    public List<double> TSec { get; set; } = new();
    public List<string> Period { get; set; } = new();     // before | during | after
    public List<bool> Abnormal { get; set; } = new();
    public List<string?> Rules { get; set; } = new();
    public List<string?> Markers { get; set; } = new();
    public Dictionary<string, List<double?>> Series { get; set; } = new();
}
