using System;
using System.Collections.Generic;
using System.Text;

namespace PerformanceDetective.Controller
{
    /// <summary>
    /// Runs the before/after testing by itself.
    ///
    /// A city's workload drifts over in-game time (commutes, home searches), so "measure A, then measure B" is unfair.
    /// Auto-Tune therefore interleaves baseline blocks with candidate blocks — A X1 A X2 A X3 A … — and compares each
    /// candidate block with the average of the baseline blocks directly before and after it. Each block first lets the
    /// pathfinding queue settle, then measures. Only running time at one fixed selected speed counts; pausing or
    /// changing speed restarts the current block.
    ///
    /// A candidate is adopted only if every comparison improved simulation speed and the average gain is clear;
    /// otherwise the previous settings are restored. Nothing is estimated: every number is a measured average.
    /// </summary>
    public sealed class AutoTune
    {
        public sealed class Candidate
        {
            public string Name;
            public Dictionary<string, float> Reductions = new Dictionary<string, float>();
            public readonly List<double> Diffs = new List<double>();       // percentage points vs neighbouring baselines
            public readonly List<double> BacklogDiffs = new List<double>();
            public double MeanDiff => Diffs.Count > 0 ? Average(Diffs) : double.NaN;
            public bool Clear => Diffs.Count > 0 && MeanDiff >= MinClearGain && Diffs.TrueForAll(d => d > 0);
        }

        private sealed class Block
        {
            public int Candidate;          // -1 = baseline
            public double Speed = double.NaN, Backlog = double.NaN, Fps = double.NaN;
        }

        private const double MinClearGain = 1.5; // percentage points of simulation speed

        public bool Running { get; private set; }
        public bool Finished { get; private set; }
        public string Summary { get; private set; } = "";
        public readonly List<Candidate> Candidates = new List<Candidate>();

        private readonly List<int> m_Schedule = new List<int>();
        private readonly List<Block> m_Done = new List<Block>();
        private int m_Index;
        private double m_BlockSeconds, m_SettleSeconds;
        private double m_Elapsed;               // running time in the current block
        private float m_Speed;                  // selected speed the test runs at
        private double m_SumSpeed, m_SumBacklog, m_SumFps; private int m_N;

        public int BlockIndex => m_Index;
        public int BlockCount => m_Schedule.Count;
        public string CurrentName => !Running ? "" : m_Schedule[m_Index] < 0 ? "Normal game (baseline)" : Candidates[m_Schedule[m_Index]].Name;
        public double BlockRemaining => Math.Max(0, m_BlockSeconds - m_Elapsed);
        public double TotalRemaining => BlockRemaining + (m_Schedule.Count - m_Index - 1) * m_BlockSeconds;
        public bool Settling => m_Elapsed < m_SettleSeconds;

        /// <summary>Reductions to apply right now, or null for the normal game.</summary>
        public Dictionary<string, float> CurrentReductions => !Running || m_Schedule[m_Index] < 0 ? null : Candidates[m_Schedule[m_Index]].Reductions;

        public void Start(bool thorough, IEnumerable<Candidate> candidates)
        {
            Candidates.Clear();
            Candidates.AddRange(candidates);
            m_Schedule.Clear();
            m_Done.Clear();
            int rounds = thorough ? 2 : 1;
            m_BlockSeconds = thorough ? 150 : 120;
            m_SettleSeconds = 40;
            m_Schedule.Add(-1);
            for (int r = 0; r < rounds; r++)
                for (int c = 0; c < Candidates.Count; c++)
                {
                    // Second round in reverse order, so no candidate always follows the same one.
                    m_Schedule.Add(r % 2 == 0 ? c : Candidates.Count - 1 - c);
                    m_Schedule.Add(-1);
                }
            m_Index = 0;
            m_Speed = 0;
            ResetBlock();
            Running = true;
            Finished = false;
            Summary = "";
        }

        public void Cancel()
        {
            Running = false;
            Summary = "Auto-Tune was stopped; your previous settings are back.";
        }

        /// <returns>true when the block (and therefore the reductions to apply) changed.</returns>
        public bool OnSample(Sample s)
        {
            if (!Running) return false;
            bool running = !s.Loading && s.SelectedSpeed > 0 && !double.IsNaN(s.AbsRatio);
            if (!running) return false; // paused/loading time does not count
            if (m_Speed == 0) m_Speed = s.SelectedSpeed;
            if (s.SelectedSpeed != m_Speed)
            {
                // Speed changed: results would not be comparable. Restart this block at the new speed.
                m_Speed = s.SelectedSpeed;
                ResetBlock();
                return false;
            }

            m_Elapsed += s.IntervalMs / 1000.0;
            if (m_Elapsed > m_SettleSeconds)
            {
                m_SumSpeed += s.AbsRatio * 100;
                m_SumBacklog += Math.Max(0, s.PathBacklog);
                m_SumFps += s.FrameMsAvg > 0 ? 1000.0 / s.FrameMsAvg : 0;
                m_N++;
            }
            if (m_Elapsed < m_BlockSeconds) return false;

            m_Done.Add(new Block
            {
                Candidate = m_Schedule[m_Index],
                Speed = m_N > 0 ? m_SumSpeed / m_N : double.NaN,
                Backlog = m_N > 0 ? m_SumBacklog / m_N : double.NaN,
                Fps = m_N > 0 ? m_SumFps / m_N : double.NaN,
            });
            Mod.Log.Info($"[SPC] Adaptive: Auto-Tune block {m_Index + 1}/{m_Schedule.Count} '{CurrentName}': speed {m_Done[m_Done.Count - 1].Speed:0.0}%, queue {m_Done[m_Done.Count - 1].Backlog:0}");
            m_Index++;
            ResetBlock();
            if (m_Index >= m_Schedule.Count) Finish();
            return true;
        }

        private void ResetBlock()
        {
            m_Elapsed = 0;
            m_SumSpeed = m_SumBacklog = m_SumFps = 0;
            m_N = 0;
        }

        private void Finish()
        {
            Running = false;
            Finished = true;
            for (int i = 1; i + 1 < m_Done.Count; i += 2)
            {
                var before = m_Done[i - 1]; var x = m_Done[i]; var after = m_Done[i + 1];
                if (x.Candidate < 0) continue;
                var c = Candidates[x.Candidate];
                c.Diffs.Add(x.Speed - (before.Speed + after.Speed) / 2);
                c.BacklogDiffs.Add(x.Backlog - (before.Backlog + after.Backlog) / 2);
            }

            var sb = new StringBuilder();
            double baseSpeed = 0; int nb = 0;
            foreach (var b in m_Done) if (b.Candidate < 0 && !double.IsNaN(b.Speed)) { baseSpeed += b.Speed; nb++; }
            baseSpeed = nb > 0 ? baseSpeed / nb : double.NaN;
            sb.Append($"Normal game: {baseSpeed:0.0}% simulation speed on average. ");
            Candidate best = null;
            foreach (var c in Candidates)
            {
                sb.Append($"{c.Name}: {Signed(c.MeanDiff)} points");
                if (c.BacklogDiffs.Count > 0) sb.Append($", pathfinding queue {Signed(Average(c.BacklogDiffs), 0)}");
                sb.Append(c.Clear ? " (clear gain). " : " (no clear gain). ");
                // Prefer the option that changes less unless another is clearly (> 1 point) better.
                if (c.Clear && (best == null || c.MeanDiff > best.MeanDiff + 1.0
                                || (Math.Abs(c.MeanDiff - best.MeanDiff) <= 1.0 && c.Reductions.Count < best.Reductions.Count)))
                    best = c;
            }
            Best = best;
            sb.Append(best != null
                ? $"Kept: {best.Name}."
                : "None helped clearly, so your previous settings are back.");
            if (m_Schedule.Count < 2 + 2 * Candidates.Count * 2) sb.Append(" Quick test: one comparison per option — run the thorough test to confirm.");
            Summary = sb.ToString();
            Mod.Log.Info("[SPC] Adaptive: Auto-Tune finished — " + Summary);
        }

        public Candidate Best { get; private set; }

        private static double Average(List<double> v) { double s = 0; foreach (var x in v) s += x; return s / v.Count; }
        private static string Signed(double v, int d = 1) => double.IsNaN(v) ? "–" : (v >= 0 ? "+" : "") + v.ToString("F" + d);
    }
}
