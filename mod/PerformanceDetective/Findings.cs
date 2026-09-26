using System;
using System.Collections.Generic;
using System.Globalization;

namespace PerformanceDetective
{
    /// <summary>
    /// Plain-language diagnosis from the last two minutes of measurements. Each finding states measured facts and,
    /// where the mechanism is known from the game's code, what it means. Nothing is guessed: no data, no finding.
    /// </summary>
    public static class Findings
    {
        public sealed class Finding
        {
            public string Level;   // "problem", "info", "good"
            public string Text;
        }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static List<Finding> Build(DetectiveSystem d)
        {
            var list = new List<Finding>();
            if (d == null || !d.InSession) return list;
            var recent = d.RecentSamples(120);
            int running = 0, pathLimited = 0, cpuLimited = 0;
            double speed = 0, fps = 0, stepsPerFrame = 0; int nFps = 0, nSteps = 0;
            float selected = 0;
            Sample last = null;
            foreach (var s in recent)
            {
                if (s.Loading || s.SelectedSpeed <= 0 || double.IsNaN(s.AbsRatio)) continue;
                running++;
                selected = s.SelectedSpeed;
                speed += s.AbsRatio;
                if (s.Limiter == "pathfinding") pathLimited++;
                else if (s.Limiter == "cpu") cpuLimited++;
                if (s.FrameMsAvg > 0) { fps += 1000 / s.FrameMsAvg; nFps++; }
                if (!double.IsNaN(s.StepsPerFrame)) { stepsPerFrame += s.StepsPerFrame; nSteps++; }
                last = s;
            }
            if (running < 50)
            {
                list.Add(new Finding { Level = "info", Text = "Collecting data — unpause and let the city run for a minute." });
                return list;
            }
            speed /= running;
            fps = nFps > 0 ? fps / nFps : double.NaN;
            double pathShare = (double)pathLimited / running, cpuShare = (double)cpuLimited / running;

            if (speed >= 0.9)
                list.Add(new Finding { Level = "good", Text = $"The simulation keeps up: {P(speed)} of the {selected:0}× speed you selected." });
            else
                list.Add(new Finding { Level = "problem", Text = $"The simulation runs at {P(speed)} of the {selected:0}× speed you selected (last 2 minutes)." });

            if (pathShare >= 0.3)
            {
                list.Add(new Finding
                {
                    Level = "problem",
                    Text = $"Pathfinding holds the simulation back {P(pathShare)} of the time: the game waits for routes to be calculated before it continues.",
                });
                double cpuSum = 0; int cpuN = 0;
                foreach (var s in recent) if (!double.IsNaN(s.SystemCpuPct)) { cpuSum += s.SystemCpuPct; cpuN++; }
                var threads = Mod.Manager?.Threads;
                if (cpuN > 0 && cpuSum / cpuN < 85 && threads != null && threads.Available && threads.Current < threads.Workers)
                    list.Add(new Finding
                    {
                        Level = "info",
                        Text = $"Your CPU is only {cpuSum / cpuN:0}% busy while pathfinding is the bottleneck. The game uses {threads.Current} of {threads.Workers} worker threads for pathfinding — extra pathfinding threads may help (Auto-Tune tests this).",
                    });
                var top = d.Sources.Top;
                if (top.Count > 0 && top[0].WorkShare >= 0.2)
                {
                    var src = top[0];
                    string text = $"Biggest source of route searches: {src.Name} ({P(src.WorkShare)} of the search work, {src.PerMinute.ToString("N0", Inv)} per minute).";
                    if (src.Tip != null) text += " " + src.Tip;
                    list.Add(new Finding { Level = "problem", Text = text });
                }
            }
            else if (cpuShare >= 0.3)
            {
                list.Add(new Finding
                {
                    Level = "problem",
                    Text = $"CPU time per simulation step limits the speed {P(cpuShare)} of the time (about {last.StepMs:0.0} ms per step)."
                           + (d.PerformancePreference != "SimulationSpeed" ? $" The game's Performance Preference ({d.PerformancePreference}) gives FPS priority over simulation speed." : ""),
                });
            }

            // At 1× the game runs at most round(speed × 2) simulation steps per rendered frame (max 8), so FPS caps speed.
            if (!double.IsNaN(fps) && fps > 0 && selected > 0)
            {
                int maxSteps = Math.Max(1, Math.Min(8, (int)Math.Round(selected * 2)));
                double cap = maxSteps * fps / (60.0 * selected);
                if (cap < 1.0)
                    list.Add(new Finding
                    {
                        Level = "problem",
                        Text = $"Low FPS also limits the simulation: at {fps:0} FPS and {selected:0}× the game can run at most {maxSteps} steps per frame, so the simulation cannot exceed {P(cap)} of full speed. Higher FPS (lower graphics settings) raises this limit.",
                    });
            }

            if (last != null && !double.IsNaN(last.PrivateMb) && !double.IsNaN(last.RamTotalMb) && last.PrivateMb > 0.85 * last.RamTotalMb)
                list.Add(new Finding
                {
                    Level = "problem",
                    Text = $"The game uses {last.PrivateMb / 1024:0.0} GB of memory on a PC with {last.RamTotalMb / 1024:0.0} GB RAM. Windows has to move data to disk, which slows everything down. No setting in this mod can change that.",
                });
            return list;
        }

        private static string P(double v) => (v * 100).ToString("0", Inv) + "%";
    }
}
