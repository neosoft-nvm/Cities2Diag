using System;
using System.Diagnostics;
using Colossal.UI.Binding;
using Game;
using Game.UI;
using PerformanceDetective.Controller;

namespace PerformanceDetective.UI
{
    /// <summary>
    /// Bridge to the in-game panel (React UI module in mod/PerformanceDetective/UI).
    ///   performanceDetective.state   — JSON snapshot, rebuilt twice per second
    ///   performanceDetective.command — "name" or "name:argument" from the panel
    /// </summary>
    public partial class DetectiveUISystem : UISystemBase
    {
        private const string Group = "performanceDetective";
        private const double RefreshSeconds = 0.5;

        private readonly Stopwatch m_Clock = Stopwatch.StartNew();
        private double m_LastBuild = -10;
        private string m_State = "{}";

        /// <summary>Panel visibility lives on the C# side so the keyboard shortcut and Options button can open it.</summary>
        public static bool PanelOpen { get; set; }

        public override GameMode gameMode => GameMode.Game;

        protected override void OnCreate()
        {
            base.OnCreate();
            AddUpdateBinding(new GetterValueBinding<string>(Group, "state", () => m_State));
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "panelOpen", () => PanelOpen));
            AddBinding(new TriggerBinding<string>(Group, "command", OnCommand));
        }

        protected override void OnUpdate()
        {
            // Ctrl+Alt+P opens/closes the panel (works however crowded the toolbar is).
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.pKey.wasPressedThisFrame && kb.ctrlKey.isPressed && kb.altKey.isPressed)
                PanelOpen = !PanelOpen;

            double now = m_Clock.Elapsed.TotalSeconds;
            if (now - m_LastBuild >= RefreshSeconds)
            {
                m_LastBuild = now;
                try { m_State = BuildState(); }
                catch (Exception e) { Mod.Log.Warn("[SPC] Error: UI state: " + e.Message); }
            }
            base.OnUpdate();
        }

        private void OnCommand(string command)
        {
            try
            {
                var st = Mod.Settings;
                var manager = Mod.Manager;
                if (st == null || manager == null || string.IsNullOrEmpty(command)) return;
                int colon = command.IndexOf(':');
                string name = colon < 0 ? command : command.Substring(0, colon);
                string arg = colon < 0 ? "" : command.Substring(colon + 1);
                bool changedSettings = true;

                bool tuneCommand = name == "autoTune" || name == "cancelAutoTune" || name == "panel" || name == "togglePanel"
                                   || name == "capture" || name == "copyReport" || name == "openFolder" || name == "overlay";
                if (manager.Tune.Running && !tuneCommand)
                {
                    Mod.Log.Info("[SPC] Configuration: ignored '" + command + "' while Auto-Tune runs");
                    return;
                }

                switch (name)
                {
                    case "autoTune":
                        changedSettings = false;
                        if (Mod.Detective != null && Mod.Detective.Breakdown.Running) break; // would skew both measurements
                        manager.StartAutoTune(arg == "thorough");
                        break;
                    case "cpuBreakdown": changedSettings = false; Mod.Detective?.Breakdown.Start(); break;
                    case "cancelCpuBreakdown": changedSettings = false; Mod.Detective?.Breakdown.Cancel(); break;
                    case "cancelAutoTune": changedSettings = false; manager.CancelAutoTune(); break;
                    case "panel": changedSettings = false; PanelOpen = arg == "1"; break;
                    case "togglePanel": changedSettings = false; PanelOpen = !PanelOpen; break;
                    case "profile":
                        if (int.TryParse(arg, out int p) && p >= 0 && p <= (int)Profile.Custom) st.ControllerProfile = p;
                        break;
                    case "enabled": st.ControllerEnabled = arg == "1"; break;
                    case "adaptive": st.AdaptiveMode = arg == "1"; break;
                    case "overlay": st.ShowOverlay = arg == "1"; break;
                    case "minQuality":
                        if (int.TryParse(arg, out int q)) st.MinimumQuality = Math.Max(20, Math.Min(100, q));
                        break;
                    case "targetSpeed":
                        if (int.TryParse(arg, out int t)) st.TargetSpeedPercent = Math.Max(50, Math.Min(100, t));
                        break;
                    case "custom":
                        {
                            var parts = arg.Split('=');
                            if (parts.Length == 2 && int.TryParse(parts[1], out int v)) st.SetCustomReduction(parts[0], v);
                            break;
                        }
                    case "threads":
                        if (int.TryParse(arg, out int th)) st.PathfindExtraThreads = Math.Max(0, Math.Min(manager.Threads.MaxExtra, th));
                        break;
                    case "reset": st.ResetControllerDefaults(); break;
                    case "capture": changedSettings = false; Mod.Detective?.CaptureNow(); break;
                    case "copyReport": changedSettings = false; st.CopyReport = true; break;
                    case "openFolder": changedSettings = false; st.OpenFolder = true; break;
                    case "baseline": changedSettings = false; manager.StartCapture("baseline"); break;
                    case "optimized": changedSettings = false; manager.StartCapture("optimized"); break;
                    case "resetCompare": changedSettings = false; manager.ResetComparison(); break;
                    default:
                        changedSettings = false;
                        Mod.Log.Warn("[SPC] Configuration: unknown UI command " + name);
                        break;
                }
                if (changedSettings)
                {
                    st.ApplyAndSave();
                    manager.Apply();
                    Mod.Log.Info("[SPC] Configuration: " + command);
                }
                m_LastBuild = -10; // refresh the panel right away
            }
            catch (Exception e)
            {
                Mod.Log.Warn("[SPC] Error: command '" + command + "': " + e.Message);
            }
        }

        private static string BuildState()
        {
            var st = Mod.Settings;
            var m = Mod.Manager;
            var d = Mod.Detective;
            var s = d?.LastSample;
            var j = new Json().BeginObject();
            j.Prop("version", typeof(Mod).Assembly.GetName().Version.ToString(3));
            j.Prop("inSession", d != null && d.InSession);

            // Controller settings
            j.Prop("enabled", st.ControllerEnabled).Prop("profile", st.ControllerProfile).Prop("adaptive", st.AdaptiveMode)
             .Prop("overlay", st.ShowOverlay).Prop("minQuality", st.MinimumQuality).Prop("targetSpeed", st.TargetSpeedPercent)
             .Prop("quality", m != null && !float.IsNaN(m.Quality) ? m.Quality : -1, 0)
             .Prop("adaptiveNote", m?.AdaptiveNote ?? "");

            // Live measurements
            j.Prop("stability", m?.Stability ?? "unknown");
            j.Prop("speedPct", m != null ? m.SmoothedSpeed * 100 : double.NaN, 1);
            if (s != null)
            {
                j.Prop("selectedSpeed", s.SelectedSpeed, 1).Prop("fps", s.FrameMsAvg > 0 ? 1000 / s.FrameMsAvg : double.NaN, 1)
                 .Prop("frameMs", s.FrameMsAvg, 1).Prop("stepMs", s.StepMs, 2).Prop("stepsPerFrame", s.StepsPerFrame, 2)
                 .Prop("backlog", s.PathBacklog).Prop("headroom", s.PathHeadroom).Prop("limiter", s.Limiter)
                 .Prop("gameCores", s.GameCores, 2).Prop("cpuPct", s.SystemCpuPct, 0).Prop("population", s.Population)
                 .Prop("ramFreeGb", s.RamAvailMb / 1024, 1).Prop("gameMemGb", s.PrivateMb / 1024, 1).Prop("ramGb", s.RamTotalMb / 1024, 1)
                 .Prop("detectorState", s.State);
            }
            j.Prop("preference", d?.PerformancePreference ?? "unknown");
            j.Prop("stalls", d?.StallCount ?? 0).Prop("captures", d?.CaptureCount ?? 0);
            var last = d?.LastStall;
            if (last != null)
                j.Name("lastStall").BeginObject().Prop("seconds", last.DurationSec, 1).Prop("minPct", last.MinRatio * 100, 0)
                 .Prop("agoSeconds", d.SessionTime - last.EndT, 0).Prop("catchUp", last.CatchUpSeconds, 1).EndObject();

            // Automatic findings
            j.Name("findings").BeginArray();
            foreach (var f in Findings.Build(d)) j.BeginObject().Prop("level", f.Level).Prop("text", f.Text).EndObject();
            j.EndArray();

            // Auto-Tune
            if (m != null)
            {
                var t = m.Tune;
                j.Name("autoTune").BeginObject().Prop("running", t.Running).Prop("finished", t.Finished)
                 .Prop("current", t.CurrentName).Prop("block", t.BlockIndex + 1).Prop("blocks", t.BlockCount)
                 .Prop("blockRemaining", t.BlockRemaining, 0).Prop("totalRemaining", t.TotalRemaining, 0)
                 .Prop("settling", t.Settling).Prop("summary", t.Summary);
                j.Name("candidates").BeginArray();
                foreach (var c in t.Candidates)
                    j.BeginObject().Prop("name", c.Name).Prop("gain", c.MeanDiff, 1).Prop("clear", c.Clear).Prop("promising", c.Promising)
                     .Prop("comparisons", c.Diffs.Count).EndObject();
                j.EndArray().EndObject();
            }

            // CPU breakdown: share of each simulation step per game system
            if (d != null)
            {
                var b = d.Breakdown;
                j.Name("cpuBreakdown").BeginObject().Prop("running", b.Running).Prop("finished", b.Finished)
                 .Prop("remaining", b.Remaining, 0).Prop("duration", CpuBreakdown.DurationSeconds, 0)
                 .Prop("steps", b.Steps).Prop("stepMs", b.StepMs, 2).Prop("error", b.Error).Prop("savedTo", b.SavedTo);
                j.Name("systems").BeginArray();
                double total = 0;
                foreach (var e in b.Result) total += e.TotalMs;
                int shown = 0;
                foreach (var e in b.Result)
                {
                    if (shown++ >= 12) break;
                    j.BeginObject().Prop("name", CpuBreakdown.ShortName(e.Name))
                     .Prop("msPerStep", e.TotalMs / Math.Max(1, b.Steps), 2)
                     .Prop("sharePct", total > 0 ? 100 * e.TotalMs / total : 0, 1).EndObject();
                }
                j.EndArray().EndObject();
            }

            // Where pathfinding work comes from (last minute)
            if (d != null)
            {
                j.Prop("sourcesPerMin", d.Sources.TotalPerMinute, 0);
                j.Name("sources").BeginArray();
                int shown = 0;
                foreach (var src in d.Sources.Top)
                {
                    if (shown++ >= 8) break;
                    j.BeginObject().Prop("name", src.Name).Prop("perMin", src.PerMinute, 0).Prop("workPct", src.WorkShare * 100, 1)
                     .Prop("successPct", src.SuccessRate * 100, 0).Prop("tip", src.Tip).EndObject();
                }
                j.EndArray();
            }

            // What the controller is doing
            j.Name("targets").BeginArray();
            if (m != null)
                foreach (var (target, reduction, active, _, _) in m.Controller.Status())
                    j.BeginObject().Prop("key", target.Key).Prop("name", target.Name).Prop("effect", target.Effect)
                     .Prop("reductionPct", reduction * 100, 0).Prop("active", active)
                     .Prop("custom", st.GetCustomReduction(target.Key)).EndObject();
            j.EndArray();
            if (m != null)
                j.Name("threads").BeginObject().Prop("available", m.Threads.Available).Prop("current", m.Threads.Current)
                 .Prop("default", m.Threads.Default).Prop("workers", m.Threads.Workers).Prop("extra", st.PathfindExtraThreads)
                 .Prop("maxExtra", m.Threads.MaxExtra).EndObject();
            j.Name("unavailable").BeginArray();
            if (m != null) foreach (var u in m.Controller.Unavailable) j.Value(u);
            j.EndArray();

            // 2-minute history for the graph
            if (m != null)
            {
                int n = m.HistCount, start = ControllerManager.HistorySeconds - n;
                j.Name("history").BeginObject();
                j.Name("speed").BeginArray(); for (int i = start; i < ControllerManager.HistorySeconds; i++) j.Value(m.HistSpeed[i] * 100, 0); j.EndArray();
                j.Name("fps").BeginArray(); for (int i = start; i < ControllerManager.HistorySeconds; i++) j.Value(m.HistFps[i], 0); j.EndArray();
                j.Name("backlog").BeginArray(); for (int i = start; i < ControllerManager.HistorySeconds; i++) j.Value(m.HistBacklog[i], 0); j.EndArray();
                j.Name("quality").BeginArray(); for (int i = start; i < ControllerManager.HistorySeconds; i++) j.Value(m.HistQuality[i], 0); j.EndArray();
                j.Name("stall").BeginArray(); for (int i = start; i < ControllerManager.HistorySeconds; i++) j.Value(m.HistStall[i]); j.EndArray();
                j.EndObject();

                j.Name("compare").BeginObject().Prop("running", m.CaptureKind).Prop("remaining", m.CaptureRemaining, 0);
                WriteSummary(j, "baseline", m.Baseline);
                WriteSummary(j, "optimized", m.Optimized);
                j.EndObject();
            }
            return j.EndObject().ToString();
        }

        private static void WriteSummary(Json j, string name, CaptureSummary c)
        {
            if (c == null) return;
            j.Name(name).BeginObject().Prop("profile", c.Profile).Prop("quality", c.Quality, 0).Prop("seconds", c.Seconds, 0)
             .Prop("speedPct", c.SpeedPct, 1).Prop("fps", c.Fps, 1).Prop("stepMs", c.StepMs, 2).Prop("peakStepMs", c.PeakStepMs, 1)
             .Prop("backlog", c.Backlog, 0).Prop("pathLimitedPct", c.PathLimitedPct, 0).Prop("gameCores", c.GameCores, 2)
             .Prop("population", c.Population).EndObject();
        }
    }
}
