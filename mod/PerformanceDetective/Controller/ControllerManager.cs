using System;
using System.Collections.Generic;

namespace PerformanceDetective.Controller
{
    /// <summary>Measured summary of one capture window (before/after comparison).</summary>
    public sealed class CaptureSummary
    {
        public string Label;
        public string Profile;
        public float Quality;
        public double Seconds;
        public double SpeedPct = double.NaN;      // simulation speed as % of the selected speed
        public double Fps = double.NaN;
        public double StepMs = double.NaN;        // CPU time per simulation step
        public double PeakStepMs = double.NaN;
        public double Backlog = double.NaN;       // pending pathfinding requests
        public double PathLimitedPct = double.NaN;
        public double GameCores = double.NaN;
        public int Population = -1;
    }

    /// <summary>
    /// Owns the controller state: profile, adaptive quality, per-category reductions, before/after captures, and the
    /// 1-per-second history used by the in-game graph. Fed by DetectiveSystem with every sample (main thread).
    /// </summary>
    public sealed class ControllerManager
    {
        public const int HistorySeconds = 120;
        private const double EvalSeconds = 5, WindowSeconds = 15, DegradeCooldown = 15, RecoveryDelay = 30;
        private const float QualityStep = 10;
        public const double CaptureSeconds = 60;

        public readonly SimulationController Controller = new SimulationController();
        public readonly AutoTune Tune = new AutoTune();

        // Settings before Auto-Tune started, restored if nothing helps or the test is stopped.
        private (bool Enabled, int Profile, bool Adaptive, Dictionary<string, int> Custom)? m_BeforeTune;

        public float Quality { get; private set; } = 100;
        public string Stability { get; private set; } = "unknown";
        public double SmoothedSpeed { get; private set; } = double.NaN;
        public string AdaptiveNote { get; private set; } = "";

        // adaptive state
        private double m_LastEval, m_LastChange = -1000;
        private int m_BelowCount, m_AboveCount;
        private readonly Queue<(double T, double Ratio)> m_Window = new Queue<(double, double)>();

        // history (1 Hz)
        public readonly float[] HistSpeed = new float[HistorySeconds];
        public readonly float[] HistFps = new float[HistorySeconds];
        public readonly float[] HistBacklog = new float[HistorySeconds];
        public readonly float[] HistQuality = new float[HistorySeconds];
        public readonly bool[] HistStall = new bool[HistorySeconds];
        public int HistCount;
        private double m_HistSecond = -1;
        private double m_AccSpeed, m_AccFps, m_AccBacklog; private int m_AccN; private bool m_AccStall;

        // comparison
        public CaptureSummary Baseline, Optimized;
        public string CaptureKind;              // null, "baseline", "optimized"
        public double CaptureRemaining;
        private readonly List<Sample> m_Capture = new List<Sample>();

        public void OnSessionStart()
        {
            Controller.ResetSafety();
            m_Window.Clear();
            m_BelowCount = m_AboveCount = 0;
            m_LastChange = -1000;
            HistCount = 0;
            m_HistSecond = -1;
            CaptureKind = null;
            m_Capture.Clear();
            Quality = MaxQuality();
            Apply();
        }

        public void OnSessionEnd()
        {
            if (Tune.Running) CancelAutoTune();
            Controller.ReleaseAll("left the city");
        }

        // ---------------- Auto-Tune ----------------

        public void StartAutoTune(bool thorough)
        {
            if (Tune.Running) return;
            var st = Mod.Settings;
            var custom = new Dictionary<string, int>();
            foreach (var t in ControlTargets.All) custom[t.Key] = st.GetCustomReduction(t.Key);
            m_BeforeTune = (st.ControllerEnabled, st.ControllerProfile, st.AdaptiveMode, custom);

            // Candidates aimed at the pathfinding sources players and the game's statistics point at.
            var performance = new Dictionary<string, float>();
            foreach (var t in ControlTargets.All)
                if (t.Key != "taxi" && t.Key != "homeSearch") performance[t.Key] = QualityCurve.Reduction(t.Priority, Profiles.Quality(Profile.Performance));
            var all = new Dictionary<string, float>(performance) { ["taxi"] = 0.75f, ["homeSearch"] = 0.75f };
            Tune.Start(thorough, new[]
            {
                new AutoTune.Candidate { Name = "Taxi dispatch 75% less often", Reductions = { ["taxi"] = 0.75f } },
                new AutoTune.Candidate { Name = "Home searches 75% less often", Reductions = { ["homeSearch"] = 0.75f } },
                new AutoTune.Candidate { Name = "Citizen updates (Performance profile)", Reductions = performance },
                new AutoTune.Candidate { Name = "All of the above", Reductions = all },
            });
            Mod.Log.Info($"[SPC] Adaptive: Auto-Tune started ({(thorough ? "thorough" : "quick")}, {Tune.BlockCount} blocks)");
            Apply();
        }

        public void CancelAutoTune()
        {
            if (!Tune.Running) return;
            Tune.Cancel();
            RestoreBeforeTune();
        }

        private void RestoreBeforeTune()
        {
            if (m_BeforeTune is not { } b) return;
            var st = Mod.Settings;
            st.ControllerEnabled = b.Enabled;
            st.ControllerProfile = b.Profile;
            st.AdaptiveMode = b.Adaptive;
            foreach (var kv in b.Custom) st.SetCustomReduction(kv.Key, kv.Value);
            st.ApplyAndSave();
            m_BeforeTune = null;
            Apply();
        }

        private void OnAutoTuneFinished()
        {
            var best = Tune.Best;
            if (best == null)
            {
                RestoreBeforeTune();
                return;
            }
            var st = Mod.Settings;
            foreach (var t in ControlTargets.All)
                st.SetCustomReduction(t.Key, best.Reductions.TryGetValue(t.Key, out var r) ? (int)Math.Round(r * 100) : 0);
            st.ControllerEnabled = true;
            st.ControllerProfile = (int)Profile.Custom;
            st.AdaptiveMode = false;
            st.ApplyAndSave();
            m_BeforeTune = null;
            Apply();
        }

        private static float MaxQuality() => Profiles.Quality((Profile)Mod.Settings.ControllerProfile);

        /// <summary>Recompute reductions from the current settings (profile change, custom sliders, master switch).</summary>
        public void Apply()
        {
            var st = Mod.Settings;
            var profile = (Profile)st.ControllerProfile;
            if (!st.AdaptiveMode || profile == Profile.Custom) Quality = profile == Profile.Custom ? float.NaN : Profiles.Quality(profile);
            else Quality = Math.Max(Math.Min(Quality, MaxQuality()), st.MinimumQuality);

            var tune = Tune.Running ? Tune.CurrentReductions : null;
            foreach (var t in ControlTargets.All)
            {
                float r = 0;
                if (Tune.Running) r = tune != null && tune.TryGetValue(t.Key, out var tr) ? tr : 0; // baseline blocks = normal game
                else if (st.ControllerEnabled)
                    r = profile == Profile.Custom ? st.GetCustomReduction(t.Key) / 100f : QualityCurve.Reduction(t.Priority, Quality);
                Controller.SetReduction(t.Key, r);
            }
        }

        public void OnSample(Sample s)
        {
            bool running = !s.Loading && s.SelectedSpeed > 0 && !double.IsNaN(s.AbsRatio);
            if (running)
            {
                m_Window.Enqueue((s.T, s.AbsRatio));
                while (m_Window.Count > 0 && m_Window.Peek().T < s.T - WindowSeconds) m_Window.Dequeue();
                double sum = 0;
                foreach (var w in m_Window) sum += w.Ratio;
                SmoothedSpeed = m_Window.Count > 0 ? sum / m_Window.Count : double.NaN;
                Stability = SmoothedSpeed >= 0.85 ? "stable" : SmoothedSpeed >= 0.5 ? "elevated" : "overloaded";
            }
            else Stability = s.Loading ? "loading" : "paused";

            if (Tune.Running)
            {
                if (Tune.OnSample(s))
                {
                    if (Tune.Finished) OnAutoTuneFinished();
                    else Apply();
                }
            }
            else if (running && s.T - m_LastEval >= EvalSeconds)
            {
                m_LastEval = s.T;
                EvaluateAdaptive(s.T);
            }

            UpdateHistory(s, running);

            if (CaptureKind != null && running)
            {
                m_Capture.Add(s);
                CaptureRemaining = Math.Max(0, CaptureSeconds - CaptureElapsed());
                if (CaptureRemaining <= 0) FinishCapture();
            }
        }

        private void EvaluateAdaptive(double t)
        {
            var st = Mod.Settings;
            var profile = (Profile)st.ControllerProfile;
            if (!st.ControllerEnabled || !st.AdaptiveMode || profile == Profile.Custom || double.IsNaN(SmoothedSpeed))
            {
                AdaptiveNote = !st.AdaptiveMode ? "" : profile == Profile.Custom ? "Adaptive mode works with the presets, not Custom." : "";
                return;
            }
            double target = st.TargetSpeedPercent / 100.0;
            float max = MaxQuality(), min = Math.Min(st.MinimumQuality, max);

            if (SmoothedSpeed < target) { m_BelowCount++; m_AboveCount = 0; }
            else if (SmoothedSpeed >= target + 0.1) { m_AboveCount++; m_BelowCount = 0; }
            else { m_BelowCount = 0; m_AboveCount = 0; }

            if (m_BelowCount >= 2 && t - m_LastChange >= DegradeCooldown && Quality > min)
            {
                Quality = Math.Max(min, Quality - QualityStep);
                m_LastChange = t;
                m_BelowCount = 0;
                Apply();
                Mod.Log.Info($"[SPC] Adaptive: speed {SmoothedSpeed:P0} < target {target:P0} → quality {Quality:0}%");
            }
            else if (m_AboveCount >= 3 && t - m_LastChange >= RecoveryDelay && Quality < max)
            {
                Quality = Math.Min(max, Quality + QualityStep);
                m_LastChange = t;
                m_AboveCount = 0;
                Apply();
                Mod.Log.Info($"[SPC] Adaptive: speed {SmoothedSpeed:P0} recovered → quality {Quality:0}%");
            }

            AdaptiveNote = Quality <= min && SmoothedSpeed < target
                ? "At the minimum quality you allowed; the target speed is not reached."
                : Quality >= max ? "Running at the profile's full quality." : "Adjusting to hold the target speed.";
        }

        private void UpdateHistory(Sample s, bool running)
        {
            double second = Math.Floor(s.T);
            if (m_HistSecond < 0) m_HistSecond = second;
            if (second > m_HistSecond)
            {
                Push(m_AccN > 0 ? (float)(m_AccSpeed / m_AccN) : float.NaN,
                     m_AccN > 0 ? (float)(m_AccFps / m_AccN) : float.NaN,
                     m_AccN > 0 ? (float)(m_AccBacklog / m_AccN) : float.NaN,
                     float.IsNaN(Quality) ? 100 : Quality, m_AccStall);
                m_AccSpeed = m_AccFps = m_AccBacklog = 0; m_AccN = 0; m_AccStall = false;
                m_HistSecond = second;
            }
            if (running)
            {
                m_AccSpeed += s.AbsRatio;
                m_AccFps += s.FrameMsAvg > 0 ? 1000.0 / s.FrameMsAvg : 0;
                m_AccBacklog += s.PathBacklog >= 0 ? s.PathBacklog : 0;
                m_AccN++;
            }
            if (s.State == "stall" || s.State == "slow") m_AccStall = true;
        }

        private void Push(float speed, float fps, float backlog, float quality, bool stall)
        {
            if (HistCount < HistorySeconds) HistCount++;
            Array.Copy(HistSpeed, 1, HistSpeed, 0, HistorySeconds - 1); HistSpeed[HistorySeconds - 1] = speed;
            Array.Copy(HistFps, 1, HistFps, 0, HistorySeconds - 1); HistFps[HistorySeconds - 1] = fps;
            Array.Copy(HistBacklog, 1, HistBacklog, 0, HistorySeconds - 1); HistBacklog[HistorySeconds - 1] = backlog;
            Array.Copy(HistQuality, 1, HistQuality, 0, HistorySeconds - 1); HistQuality[HistorySeconds - 1] = quality;
            Array.Copy(HistStall, 1, HistStall, 0, HistorySeconds - 1); HistStall[HistorySeconds - 1] = stall;
        }

        // ---------------- before/after ----------------

        public void StartCapture(string kind)
        {
            CaptureKind = kind;
            m_Capture.Clear();
            CaptureRemaining = CaptureSeconds;
            Mod.Log.Info($"[SPC] Performance: {kind} capture started");
        }

        public void ResetComparison()
        {
            Baseline = Optimized = null;
            CaptureKind = null;
            m_Capture.Clear();
        }

        private double CaptureElapsed()
        {
            double sec = 0;
            foreach (var s in m_Capture) sec += s.IntervalMs / 1000.0;
            return sec;
        }

        private void FinishCapture()
        {
            var summary = new CaptureSummary
            {
                Label = CaptureKind,
                Profile = Profiles.Label((Profile)Mod.Settings.ControllerProfile) + (Mod.Settings.AdaptiveMode ? " + Adaptive" : ""),
                Quality = float.IsNaN(Quality) ? -1 : Quality,
                Seconds = CaptureElapsed(),
            };
            double speed = 0, fps = 0, step = 0, peak = 0, backlog = 0, cores = 0; int n = 0, nStep = 0, nCores = 0, pathLimited = 0;
            foreach (var s in m_Capture)
            {
                n++;
                speed += s.AbsRatio;
                fps += s.FrameMsAvg > 0 ? 1000.0 / s.FrameMsAvg : 0;
                backlog += Math.Max(0, s.PathBacklog);
                if (s.Limiter == "pathfinding") pathLimited++;
                if (!double.IsNaN(s.StepMs) && s.StepMs > 0) { step += s.StepMs; nStep++; if (s.StepMs > peak) peak = s.StepMs; }
                if (!double.IsNaN(s.GameCores)) { cores += s.GameCores; nCores++; }
                if (s.Population >= 0) summary.Population = s.Population;
            }
            if (n > 0)
            {
                summary.SpeedPct = 100 * speed / n;
                summary.Fps = fps / n;
                summary.Backlog = backlog / n;
                summary.PathLimitedPct = 100.0 * pathLimited / n;
            }
            if (nStep > 0) { summary.StepMs = step / nStep; summary.PeakStepMs = peak; }
            if (nCores > 0) summary.GameCores = cores / nCores;

            if (CaptureKind == "baseline") Baseline = summary; else Optimized = summary;
            Mod.Log.Info($"[SPC] Performance: {CaptureKind} capture done — speed {summary.SpeedPct:0.0}%, fps {summary.Fps:0.0}, step {summary.StepMs:0.00} ms");
            CaptureKind = null;
            m_Capture.Clear();
        }
    }
}
