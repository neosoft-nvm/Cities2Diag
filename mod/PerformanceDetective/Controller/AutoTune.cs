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
    /// The thorough test stops early when the first round already answers the question: an option whose first
    /// comparison is far above the natural swings is kept without a second round, options that did not help at all are
    /// not retested, and only the unclear ones get a second comparison.
    ///
    /// A candidate is adopted only if every comparison improved simulation speed and the average gain is clear (or the
    /// single comparison was decisive); otherwise the previous settings are restored. Every number is a measured average.
    /// </summary>
    public sealed class AutoTune
    {
        public sealed class Candidate
        {
            public string Name;
            public Dictionary<string, float> Reductions = new Dictionary<string, float>();
            /// <summary>Takes effect within seconds and leaves no queued work behind (pathfinding threads).</summary>
            public bool FastSettle;
            public readonly List<double> Diffs = new List<double>();       // percentage points vs neighbouring baselines
            public readonly List<double> BacklogDiffs = new List<double>();
            /// <summary>First comparison so far above the natural swings that a second one is not needed.</summary>
            public bool Decisive;
            public string Note = "";
            public double MeanDiff => Diffs.Count > 0 ? Average(Diffs) : double.NaN;
            public bool Clear => Decisive || Diffs.Count >= 2 && MeanDiff >= MinClearGain && Diffs.TrueForAll(d => d > 0);
            /// <summary>One positive comparison of at least the clear-gain size: worth confirming, not yet proof.</summary>
            public bool Promising => !Clear && Diffs.Count > 0 && MeanDiff >= MinClearGain && Diffs.TrueForAll(d => d > 0);
        }

        private sealed class Block
        {
            public int Candidate;          // -1 = baseline
            public double Speed = double.NaN, Backlog = double.NaN, Fps = double.NaN;
        }

        private const double MinClearGain = 1.5;  // percentage points of simulation speed
        private const double DecisiveGain = 5.0;  // a single comparison at least this large (and ≥ 2× the swings) decides
        private const double MeasureSeconds = 90;
        // 90 s settling absorbs carry-over (e.g. households queued during a reduction searching all at once after it
        // ends); switching extra pathfinding threads on shows its full effect within ~30 s, so 45 s is enough there.
        private const double SettleSeconds = 90, FastSettleSeconds = 45;

        public bool Running { get; private set; }
        public bool Finished { get; private set; }
        public bool Thorough { get; private set; }
        public string Summary { get; private set; } = "";
        public readonly List<Candidate> Candidates = new List<Candidate>();

        private readonly List<int> m_Schedule = new List<int>();
        private readonly List<Block> m_Done = new List<Block>();
        private int m_Index;
        private bool m_SecondRoundPlanned;
        private double m_Elapsed;               // running time in the current block
        private float m_Speed;                  // selected speed the test runs at
        private double m_SumSpeed, m_SumBacklog, m_SumFps; private int m_N;

        public int BlockIndex => m_Index;
        /// <summary>Blocks planned so far (the thorough test may add a shorter second round after the first).</summary>
        public int BlockCount => m_Schedule.Count;
        /// <summary>True while the thorough test has not yet decided whether a second round is needed.</summary>
        public bool MayStopEarly => Running && Thorough && !m_SecondRoundPlanned;
        public string CurrentName => !Running ? "" : m_Schedule[m_Index] < 0 ? "Normal game (baseline)" : Candidates[m_Schedule[m_Index]].Name;
        public double BlockRemaining => Running ? Math.Max(0, BlockSeconds(m_Index) - m_Elapsed) : 0;
        /// <summary>Remaining time; while a second round is still possible this is the longest case.</summary>
        public double TotalRemaining
        {
            get
            {
                if (!Running) return 0;
                double t = BlockRemaining;
                for (int i = m_Index + 1; i < m_Schedule.Count; i++) t += BlockSeconds(i);
                if (MayStopEarly)
                    foreach (var c in Candidates) t += BlockSeconds(c) + SettleSeconds + MeasureSeconds;
                return t;
            }
        }
        public bool Settling => Running && m_Elapsed < SettleFor(m_Index);

        /// <summary>Reductions to apply right now, or null for the normal game.</summary>
        public Dictionary<string, float> CurrentReductions => !Running || m_Schedule[m_Index] < 0 ? null : Candidates[m_Schedule[m_Index]].Reductions;

        public void Start(bool thorough, IEnumerable<Candidate> candidates)
        {
            Candidates.Clear();
            Candidates.AddRange(candidates);
            m_Schedule.Clear();
            m_Done.Clear();
            Thorough = thorough;
            m_SecondRoundPlanned = false;
            m_Schedule.Add(-1);
            for (int c = 0; c < Candidates.Count; c++)
            {
                m_Schedule.Add(c);
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

        private double SettleFor(int index) => m_Schedule[index] >= 0 && Candidates[m_Schedule[index]].FastSettle ? FastSettleSeconds : SettleSeconds;
        private double BlockSeconds(int index) => SettleFor(index) + MeasureSeconds;
        private double BlockSeconds(Candidate c) => (c.FastSettle ? FastSettleSeconds : SettleSeconds) + MeasureSeconds;

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
            if (m_Elapsed > SettleFor(m_Index))
            {
                m_SumSpeed += s.AbsRatio * 100;
                m_SumBacklog += Math.Max(0, s.PathBacklog);
                m_SumFps += s.FrameMsAvg > 0 ? 1000.0 / s.FrameMsAvg : 0;
                m_N++;
            }
            if (m_Elapsed < BlockSeconds(m_Index)) return false;

            m_Done.Add(new Block
            {
                Candidate = m_Schedule[m_Index],
                Speed = m_N > 0 ? m_SumSpeed / m_N : double.NaN,
                Backlog = m_N > 0 ? m_SumBacklog / m_N : double.NaN,
                Fps = m_N > 0 ? m_SumFps / m_N : double.NaN,
            });
            Mod.Log.Info($"[SPC] Adaptive: Auto-Tune block {m_Index + 1}/{m_Schedule.Count} '{CurrentName}': speed {m_Done[m_Done.Count - 1].Speed:0.0}%, queue {m_Done[m_Done.Count - 1].Backlog:0}");
            Compare();
            m_Index++;
            ResetBlock();
            if (m_Index >= m_Schedule.Count && !(Thorough && !m_SecondRoundPlanned && PlanSecondRound())) Finish();
            return true;
        }

        /// <summary>After each baseline block that closes a candidate block: compare the candidate with both neighbours.</summary>
        private void Compare()
        {
            int n = m_Done.Count;
            if (n < 3 || m_Done[n - 1].Candidate >= 0 || m_Done[n - 2].Candidate < 0) return;
            var before = m_Done[n - 3]; var x = m_Done[n - 2]; var after = m_Done[n - 1];
            var c = Candidates[x.Candidate];
            c.Diffs.Add(x.Speed - (before.Speed + after.Speed) / 2);
            c.BacklogDiffs.Add(x.Backlog - (before.Backlog + after.Backlog) / 2);
        }

        /// <summary>Natural swing: average change between consecutive baseline blocks (percentage points).</summary>
        private double BaselineSwing()
        {
            double sum = 0; int n = 0; double prev = double.NaN;
            foreach (var b in m_Done)
            {
                if (b.Candidate >= 0 || double.IsNaN(b.Speed)) continue;
                if (!double.IsNaN(prev)) { sum += Math.Abs(b.Speed - prev); n++; }
                prev = b.Speed;
            }
            return n > 0 ? sum / n : double.NaN;
        }

        /// <returns>true if a second round was added; false when the first round already answered everything.</returns>
        private bool PlanSecondRound()
        {
            m_SecondRoundPlanned = true;
            double swing = BaselineSwing();
            double decisive = Math.Max(DecisiveGain, double.IsNaN(swing) ? 0 : 2 * swing);
            Candidate winner = null;
            foreach (var c in Candidates)
                if (c.Diffs.Count == 1 && c.Diffs[0] >= decisive && (winner == null || c.Diffs[0] > winner.Diffs[0])) winner = c;

            var retest = new List<int>();
            for (int i = 0; i < Candidates.Count; i++)
            {
                var c = Candidates[i];
                if (c == winner)
                {
                    c.Decisive = true;
                    c.Note = $"obvious after one comparison: more than {decisive:0.0} points, twice the natural swings";
                }
                else if (winner != null) c.Note = "not retested — " + winner.Name + " was clearly better";
                else if (c.Diffs.Count == 0 || !(c.Diffs[0] > 0)) c.Note = "no gain in the first comparison, not retested";
                else retest.Add(i);
            }
            Mod.Log.Info($"[SPC] Adaptive: Auto-Tune round 1 done — natural swings {swing:0.0} points; "
                         + (winner != null ? $"'{winner.Name}' is decisive, stopping early." : retest.Count > 0 ? $"retesting {retest.Count} option(s)." : "nothing worth retesting, stopping early."));
            if (retest.Count == 0) return false;
            // Second round in reverse order, so no candidate always follows the same one.
            for (int k = retest.Count - 1; k >= 0; k--)
            {
                m_Schedule.Add(retest[k]);
                m_Schedule.Add(-1);
            }
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
                sb.Append(c.Clear ? " (clear gain" : c.Promising ? " (promising — confirm with the thorough test" : " (no clear gain");
                sb.Append(c.Note.Length > 0 ? "; " + c.Note + "). " : "). ");
                // Prefer the option that changes less unless another is clearly (> 1 point) better.
                if (c.Clear && (best == null || c.MeanDiff > best.MeanDiff + 1.0
                                || (Math.Abs(c.MeanDiff - best.MeanDiff) <= 1.0 && c.Reductions.Count < best.Reductions.Count)))
                    best = c;
            }
            Best = best;
            sb.Append(best != null
                ? $"Kept: {best.Name}."
                : "None helped clearly, so your previous settings are back.");
            if (!Thorough) sb.Append(" Quick test: one comparison per option, so nothing is kept automatically — run the thorough test to confirm.");
            Summary = sb.ToString();
            Mod.Log.Info("[SPC] Adaptive: Auto-Tune finished — " + Summary);
        }

        public Candidate Best { get; private set; }

        private static double Average(List<double> v) { double s = 0; foreach (var x in v) s += x; return s / v.Count; }
        private static string Signed(double v, int d = 1) => double.IsNaN(v) ? "–" : (v >= 0 ? "+" : "") + v.ToString("F" + d);
    }
}
