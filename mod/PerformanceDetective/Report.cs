using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PerformanceDetective
{
    /// <summary>Everything the report needs about the PC and the game, gathered once per session.</summary>
    internal sealed class SystemSnapshot
    {
        public string ModVersion;
        public string GameVersion;
        public string Os;
        public string Cpu;
        public int LogicalProcessors;
        public double RamTotalMb;
        public string Gpu;
        public int GpuMemoryMb;
        public string[] EnabledMods = new string[0];
        public string PerformancePreference;
    }

    /// <summary>
    /// Session summary as Markdown (for people and any AI) and JSON (for tools). Only measured facts:
    /// "observations" state what the data shows, never a cause.
    /// </summary>
    internal static class Report
    {
        public const int SchemaVersion = 1;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public const string AiPrompt =
            "I play Cities: Skylines II. Sometimes the simulation runs in slow motion for a few seconds and then speeds up " +
            "again (\"catch-up\"), often while FPS stays about the same. Below is data measured on my PC by the Performance " +
            "Detective mod. A stall means: simulation ticks per second below the stated threshold of this city's normal rate " +
            "for at least the stated time.\n\n" +
            "Please:\n" +
            "1. Say which explanations the data supports and which it argues against, quoting the numbers.\n" +
            "2. Suggest the cheapest experiments to confirm, one change at a time, and what result would confirm or rule out each.\n" +
            "3. Say what additional data would help.\n" +
            "Do not assume that low FPS means a GPU problem or that high CPU means a CPU bottleneck; do not claim a cause " +
            "the data does not support.";

        public static string Markdown(SystemSnapshot sys, IReadOnlyList<Sample> samples, IReadOnlyList<StallEvent> events,
            List<(double T, string Name)> saves, double thresholdRatio, double minStallSeconds)
        {
            var st = new Stats(samples, events, saves);
            var sb = new StringBuilder();
            sb.AppendLine("# Cities: Skylines II performance report (Performance Detective)");
            sb.AppendLine();
            sb.AppendLine("## Question for the AI");
            sb.AppendLine();
            sb.AppendLine(AiPrompt);
            sb.AppendLine();

            sb.AppendLine("## PC and game");
            sb.AppendLine($"- CPU: {sys.Cpu} ({sys.LogicalProcessors} logical processors)");
            sb.AppendLine($"- RAM: {Gb(sys.RamTotalMb)}");
            sb.AppendLine($"- GPU: {sys.Gpu} ({Gb(sys.GpuMemoryMb)})");
            sb.AppendLine($"- OS: {sys.Os}");
            sb.AppendLine($"- Game version: {sys.GameVersion}; mod version: {sys.ModVersion}");
            sb.AppendLine($"- Game performance preference setting: {sys.PerformancePreference}");
            sb.AppendLine($"- Enabled mods ({sys.EnabledMods.Length}): {(sys.EnabledMods.Length == 0 ? "none reported" : string.Join(", ", sys.EnabledMods))}");
            sb.AppendLine();

            sb.AppendLine("## Session");
            sb.AppendLine($"- Measured: {Min(st.TotalSeconds)} in total, of which {Min(st.RunningSeconds)} with the simulation running (not paused, not loading)");
            sb.AppendLine($"- Population: {(st.Population >= 0 ? st.Population.ToString("N0", Inv) : "unknown")}");
            sb.AppendLine($"- Selected speed(s) while running: {string.Join(", ", st.Speeds.Select(x => $"{x.Key.ToString("0.#", Inv)}× ({Pct(x.Value / Math.Max(1, st.RunningSeconds))})"))}");
            sb.AppendLine($"- Stall definition: simulation below {thresholdRatio * 100:0}% of this city's normal tick rate for ≥ {minStallSeconds:0.#} s");
            sb.AppendLine();

            sb.AppendLine("## Simulation stalls");
            if (st.Stalls.Count == 0)
            {
                sb.AppendLine("- No stalls detected.");
            }
            else
            {
                sb.AppendLine($"- Stalls: {st.Stalls.Count} ({F(st.StallsPerHour, 1)} per hour of running simulation)");
                sb.AppendLine($"- Duration: average {F(st.Stalls.Average(e => e.DurationSec), 1)} s, longest {F(st.Stalls.Max(e => e.DurationSec), 1)} s; {Pct(st.StallSeconds / Math.Max(1, st.RunningSeconds))} of running time");
                sb.AppendLine($"- Slowest point: simulation at {Pct(st.Stalls.Min(e => e.MinRatio))} of normal");
                int withCatchUp = st.Stalls.Count(e => e.CatchUpSeconds > 0);
                sb.AppendLine($"- Followed by catch-up (faster than normal): {withCatchUp} of {st.Stalls.Count}" +
                              (withCatchUp > 0 ? $", average {F(st.Stalls.Where(e => e.CatchUpSeconds > 0).Average(e => e.CatchUpSeconds), 1)} s at up to {F(st.Stalls.Where(e => e.CatchUpSeconds > 0).Max(e => e.CatchUpPeakRatio), 2)}× normal" : ""));
                sb.AppendLine($"- Confirmed by the player (capture key during a stall): {st.Stalls.Count(e => e.ConfirmedByUser)}");
                if (st.Periodicity != null) sb.AppendLine($"- Timing: {st.Periodicity}");
                sb.AppendLine($"- Save file written within 30 s of a stall: {st.Stalls.Count(e => e.SaveWrittenNearby != null)} of {st.Stalls.Count} (saves in session: {saves.Count})");
            }
            sb.AppendLine($"- Player captures (\"it's slow now\"): {st.Captures}");
            sb.AppendLine();

            sb.AppendLine("## Normal vs. during stalls");
            sb.AppendLine("| Measure | Normal | During stalls |");
            sb.AppendLine("|---|---|---|");
            Row(sb, "Simulation rate (vs normal)", st, s => s.Ratio, v => Pct(v));
            Row(sb, "Frame time (ms, avg)", st, s => s.FrameMsAvg, v => F(v, 1));
            Row(sb, "Frame time (ms, worst per 0.2 s)", st, s => s.FrameMsMax, v => F(v, 1));
            Row(sb, "Game CPU (logical processors busy)", st, s => s.GameCores, v => F(v, 2));
            Row(sb, "Whole-PC CPU busy", st, s => s.SystemCpuPct, v => F(v, 0) + "%");
            Row(sb, "Game page faults / s", st, s => s.PageFaultsPerSec, v => F(v, 0));
            Row(sb, "Free RAM", st, s => s.RamAvailMb, v => Gb(v));
            Row(sb, "Game private memory", st, s => s.PrivateMb, v => Gb(v));
            sb.AppendLine();

            sb.AppendLine("## Resources over the whole session");
            sb.AppendLine($"- Game private memory: up to {Gb(st.MaxPrivateMb)} on a PC with {Gb(sys.RamTotalMb)} RAM; lowest free RAM {Gb(st.MinRamAvailMb)}");
            sb.AppendLine($"- Commit (RAM + page file in use): up to {Gb(st.MaxCommitMb)} of {Gb(st.CommitLimitMb)}");
            sb.AppendLine($"- Whole-PC CPU ≥ 90% busy: {Pct(st.CpuSaturatedFraction)} of running time");
            sb.AppendLine($"- Frame time: average {F(st.FrameMsAvg, 1)} ms (≈ {F(1000 / Math.Max(1, st.FrameMsAvg), 0)} FPS), 95th percentile of per-sample worst {F(st.FrameMsP95, 1)} ms");
            sb.AppendLine();

            sb.AppendLine("## Observations (facts from the data, not conclusions)");
            foreach (var o in Observations(sys, st)) sb.AppendLine("- " + o);
            sb.AppendLine();

            if (st.Stalls.Count > 0)
            {
                sb.AppendLine("## Stall list");
                sb.AppendLine("| # | Time | Duration | Slowest | Catch-up | Save nearby | Confirmed |");
                sb.AppendLine("|---|---|---|---|---|---|---|");
                foreach (var e in st.Stalls.Take(50))
                    sb.AppendLine($"| {e.Id} | {e.StartUtc.ToLocalTime():HH:mm:ss} | {F(e.DurationSec, 1)} s | {Pct(e.MinRatio)} | {(e.CatchUpSeconds > 0 ? $"{F(e.CatchUpSeconds, 1)} s @ {F(e.CatchUpPeakRatio, 2)}×" : "–")} | {(e.SaveWrittenNearby != null ? "yes" : "–")} | {(e.ConfirmedByUser ? "yes" : "–")} |");
                sb.AppendLine();
            }

            sb.AppendLine($"_Report schema {SchemaVersion}. Generated {DateTime.Now:yyyy-MM-dd HH:mm}. All values measured on this PC; nothing was estimated._");
            return sb.ToString();
        }

        private static IEnumerable<string> Observations(SystemSnapshot sys, Stats st)
        {
            var list = new List<string>();
            if (!double.IsNaN(st.MaxPrivateMb) && sys.RamTotalMb > 0 && st.MaxPrivateMb > 0.85 * sys.RamTotalMb)
                list.Add($"The game's private memory ({Gb(st.MaxPrivateMb)}) reached {Pct(st.MaxPrivateMb / sys.RamTotalMb)} of installed RAM ({Gb(sys.RamTotalMb)}); lowest free RAM was {Gb(st.MinRamAvailMb)}.");
            if (st.CpuSaturatedFraction > 0.1)
                list.Add($"The whole PC's CPU was ≥ 90% busy for {Pct(st.CpuSaturatedFraction)} of running time.");
            if (st.Stalls.Count > 0)
            {
                double normalFaults = st.Mean(s => s.PageFaultsPerSec, false), stallFaults = st.Mean(s => s.PageFaultsPerSec, true);
                if (!double.IsNaN(normalFaults) && stallFaults > 2 * normalFaults && stallFaults - normalFaults > 500)
                    list.Add($"Game page faults were {F(stallFaults / Math.Max(1, normalFaults), 1)}× higher during stalls than normally.");
                double normalFrame = st.Mean(s => s.FrameMsAvg, false), stallFrame = st.Mean(s => s.FrameMsAvg, true);
                if (!double.IsNaN(normalFrame) && Math.Abs(stallFrame - normalFrame) < 0.15 * normalFrame)
                    list.Add("Frame time was about the same during stalls as normally: the stalls are in the simulation, not the rendering.");
                else if (!double.IsNaN(normalFrame) && stallFrame > 1.5 * normalFrame)
                    list.Add($"Frame time rose from {F(normalFrame, 1)} ms to {F(stallFrame, 1)} ms during stalls: rendering slowed down too.");
                int nearSave = st.Stalls.Count(e => e.SaveWrittenNearby != null);
                if (nearSave > 0 && nearSave >= st.Stalls.Count / 2)
                    list.Add($"{nearSave} of {st.Stalls.Count} stalls happened within 30 s of a save file being written.");
                if (st.Periodicity != null) list.Add("Stall timing: " + st.Periodicity);
            }
            if (list.Count == 0) list.Add("Nothing stood out beyond the numbers above.");
            return list;
        }

        public static string JsonSummary(SystemSnapshot sys, IReadOnlyList<Sample> samples, IReadOnlyList<StallEvent> events,
            List<(double T, string Name)> saves, double thresholdRatio, double minStallSeconds)
        {
            var st = new Stats(samples, events, saves);
            var j = new Json().BeginObject();
            j.Prop("schema", SchemaVersion).Prop("tool", "Performance Detective").Prop("modVersion", sys.ModVersion);
            j.Name("system").BeginObject()
                .Prop("cpu", sys.Cpu).Prop("logicalProcessors", sys.LogicalProcessors).Prop("ramTotalMb", sys.RamTotalMb, 0)
                .Prop("gpu", sys.Gpu).Prop("gpuMemoryMb", sys.GpuMemoryMb).Prop("os", sys.Os).Prop("gameVersion", sys.GameVersion)
                .Prop("performancePreference", sys.PerformancePreference);
            j.Name("enabledMods").BeginArray(); foreach (var m in sys.EnabledMods) j.Value(m); j.EndArray();
            j.EndObject();
            j.Name("session").BeginObject()
                .Prop("totalSeconds", st.TotalSeconds, 0).Prop("runningSeconds", st.RunningSeconds, 0).Prop("population", st.Population)
                .Prop("stallThresholdRatio", thresholdRatio).Prop("minStallSeconds", minStallSeconds)
                .Prop("stalls", st.Stalls.Count).Prop("stallsPerHour", st.StallsPerHour, 2).Prop("captures", st.Captures)
                .Prop("saves", saves.Count).Prop("periodicity", st.Periodicity)
                .Prop("maxPrivateMb", st.MaxPrivateMb, 0).Prop("minRamAvailMb", st.MinRamAvailMb, 0)
                .Prop("maxCommitMb", st.MaxCommitMb, 0).Prop("commitLimitMb", st.CommitLimitMb, 0)
                .Prop("cpuSaturatedFraction", st.CpuSaturatedFraction).Prop("frameMsAvg", st.FrameMsAvg, 2).Prop("frameMsP95", st.FrameMsP95, 2)
                .EndObject();
            j.Name("normalVsStall").BeginObject();
            foreach (var (name, f) in new (string, Func<Sample, double>)[]
            {
                ("ratio", s => s.Ratio), ("frameMsAvg", s => s.FrameMsAvg), ("frameMsMax", s => s.FrameMsMax), ("gameCores", s => s.GameCores),
                ("systemCpuPct", s => s.SystemCpuPct), ("pageFaultsPerSec", s => s.PageFaultsPerSec), ("ramAvailMb", s => s.RamAvailMb), ("privateMb", s => s.PrivateMb),
            })
                j.Name(name).BeginObject().Prop("normal", st.Mean(f, false)).Prop("stall", st.Mean(f, true)).EndObject();
            j.EndObject();
            j.Name("stalls").BeginArray();
            foreach (var e in st.Stalls)
                j.BeginObject().Prop("id", e.Id).Prop("startUtc", e.StartUtc.ToString("o", Inv)).Prop("durationSec", e.DurationSec, 2)
                    .Prop("minRatio", e.MinRatio).Prop("catchUpSeconds", e.CatchUpSeconds, 2).Prop("catchUpPeakRatio", e.CatchUpPeakRatio)
                    .Prop("selectedSpeed", e.SelectedSpeed).Prop("baselineTicksPerSec", e.BaselineTicksPerSec, 1)
                    .Prop("saveNearby", e.SaveWrittenNearby).Prop("confirmedByUser", e.ConfirmedByUser).EndObject();
            j.EndArray();
            return j.EndObject().ToString();
        }

        private static void Row(StringBuilder sb, string label, Stats st, Func<Sample, double> f, Func<double, string> fmt)
        {
            double n = st.Mean(f, false), s = st.Mean(f, true);
            sb.AppendLine($"| {label} | {(double.IsNaN(n) ? "–" : fmt(n))} | {(double.IsNaN(s) ? "–" : fmt(s))} |");
        }

        public static string F(double v, int d) => double.IsNaN(v) ? "–" : v.ToString("F" + d, Inv);
        public static string Pct(double v) => double.IsNaN(v) ? "–" : (v * 100).ToString("0", Inv) + "%";
        public static string Gb(double mb) => double.IsNaN(mb) || mb <= 0 ? "–" : (mb / 1024).ToString("0.0", Inv) + " GB";
        private static string Min(double sec) => sec >= 3600 ? $"{(int)(sec / 3600)} h {(int)(sec % 3600 / 60)} min" : $"{Math.Round(sec / 60)} min";

        /// <summary>Aggregates computed once per report.</summary>
        private sealed class Stats
        {
            private readonly IReadOnlyList<Sample> _samples;
            private readonly List<(double From, double To)> _stallSpans;
            public readonly List<StallEvent> Stalls;
            public readonly int Captures;
            public readonly double TotalSeconds, RunningSeconds, StallSeconds, StallsPerHour;
            public readonly int Population;
            public readonly Dictionary<float, double> Speeds = new Dictionary<float, double>();
            public readonly double MaxPrivateMb = double.NaN, MinRamAvailMb = double.NaN, MaxCommitMb = double.NaN, CommitLimitMb = double.NaN;
            public readonly double CpuSaturatedFraction, FrameMsAvg = double.NaN, FrameMsP95 = double.NaN;
            public readonly string Periodicity;

            public Stats(IReadOnlyList<Sample> samples, IReadOnlyList<StallEvent> events, List<(double T, string Name)> saves)
            {
                _samples = samples;
                Stalls = events.Where(e => e.Kind == "stall").OrderBy(e => e.StartT).ToList();
                Captures = events.Count(e => e.Kind == "capture") + Stalls.Count(e => e.ConfirmedByUser);
                _stallSpans = Stalls.Select(e => (e.StartT, e.EndT)).ToList();
                StallSeconds = Stalls.Sum(e => e.DurationSec);

                int saturated = 0, running = 0;
                var frameMax = new List<double>();
                double frameSum = 0; int frameN = 0;
                foreach (var s in samples)
                {
                    TotalSeconds += s.IntervalMs / 1000;
                    if (s.Population >= 0) Population = s.Population;
                    if (!double.IsNaN(s.PrivateMb)) MaxPrivateMb = double.IsNaN(MaxPrivateMb) ? s.PrivateMb : Math.Max(MaxPrivateMb, s.PrivateMb);
                    if (!double.IsNaN(s.RamAvailMb)) MinRamAvailMb = double.IsNaN(MinRamAvailMb) ? s.RamAvailMb : Math.Min(MinRamAvailMb, s.RamAvailMb);
                    if (!double.IsNaN(s.CommitUsedMb)) { MaxCommitMb = double.IsNaN(MaxCommitMb) ? s.CommitUsedMb : Math.Max(MaxCommitMb, s.CommitUsedMb); CommitLimitMb = s.CommitLimitMb; }
                    if (s.Loading || s.SelectedSpeed <= 0) continue;
                    running++;
                    RunningSeconds += s.IntervalMs / 1000;
                    Speeds[s.SelectedSpeed] = (Speeds.TryGetValue(s.SelectedSpeed, out var v) ? v : 0) + s.IntervalMs / 1000;
                    if (s.SystemCpuPct >= 90) saturated++;
                    if (s.Frames > 0) { frameSum += s.FrameMsAvg; frameN++; frameMax.Add(s.FrameMsMax); }
                }
                if (Population == 0) Population = -1;
                CpuSaturatedFraction = running > 0 ? (double)saturated / running : 0;
                if (frameN > 0)
                {
                    FrameMsAvg = frameSum / frameN;
                    frameMax.Sort();
                    FrameMsP95 = frameMax[(int)(frameMax.Count * 0.95)];
                }
                StallsPerHour = RunningSeconds > 60 ? Stalls.Count / (RunningSeconds / 3600) : double.NaN;
                Periodicity = FindPeriodicity(Stalls);
            }

            private bool InStall(Sample s)
            {
                foreach (var (from, to) in _stallSpans) if (s.T >= from && s.T <= to) return true;
                return false;
            }

            /// <summary>Mean over running samples, either outside stalls or during stalls.</summary>
            public double Mean(Func<Sample, double> f, bool duringStall)
            {
                double sum = 0; int n = 0;
                foreach (var s in _samples)
                {
                    if (s.Loading || s.SelectedSpeed <= 0 || InStall(s) != duringStall) continue;
                    double v = f(s);
                    if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                    sum += v; n++;
                }
                return n > 0 ? sum / n : double.NaN;
            }

            private static string FindPeriodicity(List<StallEvent> stalls)
            {
                if (stalls.Count < 4) return null;
                var gaps = new List<double>();
                for (int i = 1; i < stalls.Count; i++) gaps.Add(stalls[i].StartT - stalls[i - 1].StartT);
                gaps.Sort();
                double median = gaps[gaps.Count / 2];
                int close = gaps.Count(g => Math.Abs(g - median) <= Math.Max(3, 0.05 * median));
                if (close < 3 || close < 0.5 * gaps.Count) return null;
                return $"{close} of {gaps.Count} gaps between stalls are {F(median / 60, 2)} min (±{F(Math.Max(3, 0.05 * median), 0)} s) — a regular rhythm";
            }
        }
    }
}
