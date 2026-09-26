using System.Globalization;
using System.Text;
using System.Text.Json;
using Cs2Monitor.Logging;

namespace Cs2Monitor.Detection;

/// <summary>Builds and saves CS2_Event_NNNNN.json/.csv. Runs on the sampler thread.</summary>
internal sealed class EventWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] SummaryKeys =
    {
        "fps", "frametime_avg_ms", "frametime_max_ms", "frame_cpu_busy_ms", "frame_gpu_busy_ms",
        "gpu_util", "gpu_3d_game", "cpu_busy", "max_core_busy", "cpu_freq_mhz", "game_cores_busy", "main_thread_pct",
        "top_thread_pct", "threads_over_90", "other_cpu_cores", "ram_used_mb", "commit_used_mb", "hard_faults", "game_hard_faults",
        "game_private_mb", "vram_used_mb",
        "game_io_read_mb_s", "gpu_clock_mhz", "interval_ms",
    };

    private readonly string _logRoot;
    private readonly List<CsvSchema.Column> _columns;
    private int _nextId;

    public EventWriter(string logRoot, List<CsvSchema.Column> columns)
    {
        _logRoot = logRoot;
        _columns = columns;
        _nextId = 1 + ExistingMaxId(logRoot);
    }

    /// <summary>Event ids are unique across all sessions in the log folder.</summary>
    private static int ExistingMaxId(string root)
    {
        int max = 0;
        try
        {
            if (!Directory.Exists(root)) return 0;
            foreach (var f in Directory.EnumerateFiles(root, "CS2_Event_*.json", SearchOption.AllDirectories))
            {
                var stem = Path.GetFileNameWithoutExtension(f);
                if (int.TryParse(stem.AsSpan("CS2_Event_".Length), out int id)) max = Math.Max(max, id);
            }
        }
        catch (Exception) { /* unreadable folder: start at 1 */ }
        return max;
    }

    /// <summary>
    /// <paramref name="samples"/> must be in time order. The event window is split into
    /// before = [.., duringFrom), during = [duringFrom, duringTo], after = (duringTo, ..].
    /// </summary>
    public (EventRecord Record, string JsonPath) Write(EventRecord record, IReadOnlyList<Sample> samples,
        double originTMs, double duringFrom, double duringTo, string sessionDirectory)
    {
        record.Id = _nextId++;
        record.Name = $"CS2_Event_{record.Id:00000}";

        string Period(Sample s) => s.TMs < duringFrom ? "before" : s.TMs <= duringTo ? "during" : "after";

        foreach (var key in SummaryKeys)
        {
            var m = Metrics.Find(key)!;
            var summary = new MetricSummary
            {
                Key = key, Label = m.Label, Unit = m.Unit,
                Before = Mean(samples, m, s => Period(s) == "before"),
                During = Mean(samples, m, s => Period(s) == "during"),
                After = Mean(samples, m, s => Period(s) == "after"),
            };
            if (summary.Before == null && summary.During == null && summary.After == null) continue;
            record.Summary.Add(summary);
            if (Observation(m, summary) is string obs) record.Observations.Add(obs);
        }
        record.OtherProcesses = OtherProcesses(samples, Period);
        foreach (var p in record.OtherProcesses)
        {
            if (p.DuringCores - p.BeforeCores >= 0.5)
                record.Observations.Add($"Other process {p.Name}: {p.BeforeCores:0.00} cores before → {p.DuringCores:0.00} during → {p.AfterCores:0.00} after");
        }
        if (record.Observations.Count == 0)
            record.Observations.Add("No measured value changed notably between 'before' and 'during'.");

        var t = record.Timeline;
        foreach (var m in Metrics.All) t.Series[m.Key] = new List<double?>(samples.Count);
        foreach (var s in samples)
        {
            t.TSec.Add(Math.Round((s.TMs - originTMs) / 1000.0, 3));
            t.Period.Add(Period(s));
            t.Abnormal.Add(s.Abnormal);
            t.Rules.Add(s.RulesFired);
            t.Markers.Add(s.Marker);
            foreach (var m in Metrics.All)
            {
                var v = m.Get(s);
                t.Series[m.Key].Add(v.HasValue && double.IsFinite(v.Value) ? Math.Round(v.Value, 3) : null);
            }
        }

        var dir = Path.Combine(sessionDirectory, "Events");
        Directory.CreateDirectory(dir);
        var jsonPath = Path.Combine(dir, record.Name + ".json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(record, JsonOptions));

        var sb = new StringBuilder();
        sb.Append("event_t_s,event_period,").AppendJoin(',', _columns.Select(c => c.Header)).AppendLine();
        foreach (var s in samples)
        {
            sb.Append(((s.TMs - originTMs) / 1000.0).ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
              .Append(Period(s)).Append(',');
            CsvSchema.AppendRow(sb, _columns, s);
            sb.AppendLine();
        }
        File.WriteAllText(Path.Combine(dir, record.Name + ".csv"), sb.ToString(), new UTF8Encoding(false));
        return (record, jsonPath);
    }

    /// <summary>
    /// Average CPU of each non-game process per period, over the samples where processes were measured
    /// (once per second). A process missing from a measured sample's top list counts as ~0 there.
    /// </summary>
    private static List<ProcessUse> OtherProcesses(IReadOnlyList<Sample> samples, Func<Sample, string> period)
    {
        var sums = new Dictionary<string, double[]>();
        var measured = new Dictionary<string, int> { ["before"] = 0, ["during"] = 0, ["after"] = 0 };
        foreach (var s in samples)
        {
            if (s.OtherTopCpu == null) continue;
            string p = period(s);
            measured[p]++;
            int idx = p == "before" ? 0 : p == "during" ? 1 : 2;
            foreach (var (name, cores) in s.OtherTopCpu)
            {
                if (!sums.TryGetValue(name, out var arr)) sums[name] = arr = new double[3];
                arr[idx] += cores;
            }
        }
        double Avg(double sum, string p) => measured[p] > 0 ? Math.Round(sum / measured[p], 3) : 0;
        return sums
            .Select(kv => new ProcessUse
            {
                Name = kv.Key,
                BeforeCores = Avg(kv.Value[0], "before"),
                DuringCores = Avg(kv.Value[1], "during"),
                AfterCores = Avg(kv.Value[2], "after"),
            })
            .OrderByDescending(p => Math.Max(p.DuringCores, p.BeforeCores))
            .Take(8)
            .ToList();
    }

    private static double? Mean(IReadOnlyList<Sample> samples, Metric m, Func<Sample, bool> include)
    {
        double sum = 0;
        int n = 0;
        foreach (var s in samples)
        {
            if (!include(s)) continue;
            var v = m.Get(s);
            if (v.HasValue && double.IsFinite(v.Value)) { sum += v.Value; n++; }
        }
        return n > 0 ? sum / n : null;
    }

    private static string? Observation(Metric m, MetricSummary s)
    {
        if (s.Before is not double before || s.During is not double during) return null;
        double delta = during - before;
        if (Math.Abs(delta) < m.NotableAbs || Math.Abs(delta) < m.NotableRel * Math.Abs(before)) return null;
        string after = s.After is double a ? $" → {Fmt(a, m.Unit)} after" : "";
        return $"{m.Label}: {Fmt(before, m.Unit)} before → {Fmt(during, m.Unit)} during{after} ({(delta > 0 ? "+" : "")}{Fmt(delta, m.Unit)})";
    }

    public static string Fmt(double v, string unit) => unit switch
    {
        "%" => $"{v:0}%",
        "ms" => $"{v:0.0} ms",
        "MB" => Math.Abs(v) >= 1024 ? $"{v / 1024:0.00} GB" : $"{v:0} MB",
        "cores" => $"{v:0.00} cores",
        "fps" => $"{v:0.0} fps",
        "" => $"{v:0.##}",
        _ => $"{v:0.#} {unit}",
    };
}
