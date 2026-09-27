using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Game;
using Game.City;
using Game.SceneFlow;
using Game.Simulation;
using Game.UI.Localization;
using Game.UI.Menu;
using Unity.Entities;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PerformanceDetective
{
    /// <summary>
    /// Runs once per rendered frame (UI update phase, so it also runs while the simulation is paused or slow).
    /// Per frame it only counts frame time; every SampleIntervalMs of real time it takes one sample.
    /// All disk writes go through <see cref="BackgroundWriter"/>.
    /// </summary>
    public partial class DetectiveSystem : GameSystemBase
    {
        private const double SampleIntervalMs = 200;
        private const double PreEventSeconds = 20;
        private const double PopulationIntervalSeconds = 10;
        private const string NotificationId = "PerformanceDetective";

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private SimulationSystem m_Simulation;
        private Game.Pathfind.PathfindResultSystem m_PathfindResults;
        private NotificationUISystem m_Notifications;
        private EntityQuery m_PopulationQuery;
        private CityConfigurationSystem m_CityConfig;

        private readonly Stopwatch m_Clock = new Stopwatch();
        private readonly StallDetector m_Detector = new StallDetector();
        private BackgroundWriter m_Writer;
        private FileSystemWatcher m_SaveWatcher;
        private readonly ConcurrentQueue<string> m_SaveEvents = new ConcurrentQueue<string>();

        // Session state (a session = one loaded city)
        private bool m_InSession;
        private string m_SessionDir;
        private double m_SessionStart;
        private readonly List<Sample> m_Samples = new List<Sample>();
        private readonly List<StallEvent> m_Events = new List<StallEvent>();
        private readonly List<(double T, string Name)> m_Saves = new List<(double, string)>();
        private readonly List<StallEvent> m_PendingCaptures = new List<StallEvent>();
        private readonly StringBuilder m_CsvBuffer = new StringBuilder();
        private double m_LastCsvFlush;
        private SystemSnapshot m_System;
        private int m_NextEventId = 1;
        private int m_CaptureCount;
        private string m_LastSessionDir;

        // Sampling state
        private double m_LastSampleT;
        private uint m_LastFrameIndex;
        private int m_Frames;
        private double m_FrameMsSum, m_FrameMsMax;
        private long m_LastProcCpu = -1, m_LastSysIdle, m_LastSysTotal;
        private uint m_LastPageFaults;
        private double m_LastPopulationT = -1000;
        private int m_Population = -1;
        private bool m_StallNotified;

        public static string RootDirectory => Path.Combine(Application.persistentDataPath, "ModsData", "PerformanceDetective");
        public string SessionOrLastDirectory => m_SessionDir ?? m_LastSessionDir;

        /// <summary>Which game systems generate pathfinding work (read-only statistics from the game).</summary>
        public readonly PathfindSources Sources = new PathfindSources();
        public readonly CpuBreakdown Breakdown = new CpuBreakdown();
        private double m_LastSourcesT = -10;

        // Read by the UI system (main thread).
        public bool InSession => m_InSession;
        public Sample LastSample => m_Samples.Count > 0 ? m_Samples[m_Samples.Count - 1] : null;
        public int StallCount { get { int n = 0; foreach (var e in m_Events) if (e.Kind == "stall") n++; return n; } }
        public StallEvent LastStall { get { for (int i = m_Events.Count - 1; i >= 0; i--) if (m_Events[i].Kind == "stall") return m_Events[i]; return null; } }
        public int CaptureCount => m_CaptureCount;
        public string PerformancePreference => m_System?.PerformancePreference ?? "unknown";
        public string CityName { get { try { return m_CityConfig?.cityName ?? ""; } catch (Exception) { return ""; } } }
        public double SessionTime => m_InSession ? m_Clock.Elapsed.TotalSeconds - m_SessionStart : 0;

        /// <summary>Samples of the last <paramref name="seconds"/> seconds (oldest first).</summary>
        public List<Sample> RecentSamples(double seconds)
        {
            var result = new List<Sample>();
            if (m_Samples.Count == 0) return result;
            double from = m_Samples[m_Samples.Count - 1].T - seconds;
            int i = m_Samples.Count - 1;
            while (i > 0 && m_Samples[i - 1].T >= from) i--;
            for (; i < m_Samples.Count; i++) result.Add(m_Samples[i]);
            return result;
        }

        /// <summary>Capture button in the panel (same as the capture key).</summary>
        public void CaptureNow()
        {
            if (m_InSession) Capture(SessionTime);
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            try { m_PathfindResults = World.GetOrCreateSystemManaged<Game.Pathfind.PathfindResultSystem>(); }
            catch (Exception e) { Mod.Log.Warn("[SPC] Compatibility: pathfinding status unavailable: " + e.Message); }
            m_Notifications = World.GetOrCreateSystemManaged<NotificationUISystem>();
            try { m_CityConfig = World.GetOrCreateSystemManaged<CityConfigurationSystem>(); }
            catch (Exception e) { Mod.Log.Warn("[SPC] Compatibility: city name unavailable: " + e.Message); }
            m_PopulationQuery = GetEntityQuery(ComponentType.ReadOnly<Population>());
            m_Writer = new BackgroundWriter();
            m_Clock.Start();
            StartSaveWatcher();
        }

        protected override void OnDestroy()
        {
            EndSession();
            m_SaveWatcher?.Dispose();
            m_Writer?.Dispose();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            var settings = Mod.Settings;
            double now = m_Clock.Elapsed.TotalSeconds;

            var gm = GameManager.instance;
            bool inGame = gm != null && gm.gameMode == GameMode.Game;
            if (inGame && !m_InSession) StartSession(now);
            else if (!inGame && m_InSession) EndSession();
            if (!m_InSession) return;

            // Per frame: only frame-time bookkeeping and the capture key.
            double frameMs = UnityEngine.Time.unscaledDeltaTime * 1000.0;
            m_Frames++;
            m_FrameMsSum += frameMs;
            if (frameMs > m_FrameMsMax) m_FrameMsMax = frameMs;
            CheckCaptureKey(now);

            if ((now - m_LastSampleT) * 1000.0 < SampleIntervalMs) return;
            TakeSample(now, gm.isGameLoading, settings);
        }

        private void TakeSample(double now, bool loading, Setting settings)
        {
            var s = new Sample
            {
                Utc = DateTime.UtcNow,
                T = now - m_SessionStart,
                IntervalMs = (now - m_LastSampleT) * 1000.0,
                Loading = loading,
                SelectedSpeed = m_Simulation.selectedSpeed,
                SmoothSpeed = m_Simulation.smoothSpeed,
                Frames = m_Frames,
                FrameMsAvg = m_Frames > 0 ? m_FrameMsSum / m_Frames : double.NaN,
                FrameMsMax = m_FrameMsMax,
            };
            double dt = now - m_LastSampleT;
            m_LastSampleT = now;
            m_Frames = 0;
            m_FrameMsSum = m_FrameMsMax = 0;

            uint frameIndex = m_Simulation.frameIndex;
            uint steps = frameIndex >= m_LastFrameIndex ? frameIndex - m_LastFrameIndex : 0;
            s.TicksPerSec = dt > 0 ? steps / dt : 0;
            m_LastFrameIndex = frameIndex;
            if (s.SelectedSpeed > 0 && !loading) s.AbsRatio = s.TicksPerSec / (60.0 * s.SelectedSpeed);
            if (s.Frames > 0) s.StepsPerFrame = (double)steps / s.Frames;
            float stepSeconds = m_Simulation.frameDuration;
            if (stepSeconds > 0) s.StepMs = stepSeconds * 1000.0;
            MeasurePathfinding(s, frameIndex);
            Breakdown.Tick(!loading && s.SelectedSpeed > 0 && steps > 0 ? dt : 0, m_SessionDir);
            if (!loading && s.T - m_LastSourcesT >= 2)
            {
                m_LastSourcesT = s.T;
                Sources.Update(m_PathfindResults, s.T);
            }

            MeasureResources(s, dt);

            if (!loading && s.T - m_LastPopulationT >= PopulationIntervalSeconds && !m_Detector.InStall)
            {
                m_LastPopulationT = s.T;
                try
                {
                    if (!m_PopulationQuery.IsEmptyIgnoreFilter)
                        m_Population = m_PopulationQuery.GetSingleton<Population>().m_Population;
                }
                catch (Exception) { /* no single city entity (e.g. during load) */ }
            }
            s.Population = m_Population;

            while (m_SaveEvents.TryDequeue(out var save))
            {
                if (m_Saves.Count > 0 && m_Saves[m_Saves.Count - 1].Name == save && s.T - m_Saves[m_Saves.Count - 1].T < 10) continue;
                m_Saves.Add((s.T, save));
                s.Marker = AppendMarker(s.Marker, "save written: " + save);
            }

            m_Detector.ThresholdRatio = settings.StallThresholdPercent / 100.0;
            m_Detector.MinStallSeconds = settings.MinStallSeconds;
            bool wasInStall = m_Detector.InStall;
            var finished = m_Detector.Process(s);
            var manager = Mod.Manager;
            if (manager != null)
            {
                manager.OnSample(s);
                s.Quality = manager.Quality;
            }
            m_Samples.Add(s);
            AppendCsv(s);

            if (m_Detector.InStall && !wasInStall && settings.NotifyStalls) ShowStallNotification(s);
            if (!m_Detector.InStall && m_StallNotified) HideNotification();
            if (finished != null) FinishEvent(finished);

            for (int i = m_PendingCaptures.Count - 1; i >= 0; i--)
            {
                var c = m_PendingCaptures[i];
                if (s.T - c.StartT < settings.PostEventSeconds) continue;
                m_PendingCaptures.RemoveAt(i);
                c.EndT = c.StartT;
                FinishEvent(c);
            }

            if (s.T - m_LastCsvFlush >= 5) FlushCsv(s.T);
        }

        /// <summary>
        /// The game runs the simulation only up to the frame by which queued path results are due, and slows the
        /// simulation when fewer than 48 frames of headroom remain (see docs/CONTROLLER_DESIGN.md).
        /// </summary>
        private void MeasurePathfinding(Sample s, uint frameIndex)
        {
            if (m_PathfindResults == null) return;
            try
            {
                uint pending = m_PathfindResults.pendingSimulationFrame;
                s.PathBacklog = m_PathfindResults.pendingRequestCount;
                s.PathHeadroom = pending == uint.MaxValue ? -1 : Math.Max(0L, (long)pending - frameIndex);
            }
            catch (Exception)
            {
                m_PathfindResults = null; // API changed: stop trying, keep the rest working
                Mod.Log.Warn("[SPC] Compatibility: pathfinding status no longer readable");
                return;
            }
            if (s.Loading || s.SelectedSpeed <= 0 || double.IsNaN(s.AbsRatio)) s.Limiter = "";
            else if (s.PathHeadroom >= 0 && s.PathHeadroom < 48) s.Limiter = "pathfinding";
            else if (s.AbsRatio < 0.9) s.Limiter = "cpu";
            else s.Limiter = "none";
        }

        private void MeasureResources(Sample s, double dt)
        {
            if (!Native.IsWindows) return;
            try
            {
                var process = Native.GetCurrentProcess();
                if (Native.GetProcessTimes(process, out _, out _, out long kernel, out long user))
                {
                    long cpu = kernel + user;
                    if (m_LastProcCpu >= 0 && dt > 0) s.GameCores = (cpu - m_LastProcCpu) / 1e7 / dt;
                    m_LastProcCpu = cpu;
                }
                if (Native.GetSystemTimes(out long idle, out long sysKernel, out long sysUser))
                {
                    long total = sysKernel + sysUser; // kernel time includes idle time
                    if (m_LastSysTotal > 0 && total > m_LastSysTotal)
                        s.SystemCpuPct = 100.0 * (1.0 - (double)(idle - m_LastSysIdle) / (total - m_LastSysTotal));
                    m_LastSysIdle = idle;
                    m_LastSysTotal = total;
                }
                if (Native.GetProcessMemoryInfo(process, out var mem, (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.PROCESS_MEMORY_COUNTERS_EX))))
                {
                    s.PrivateMb = (ulong)mem.PrivateUsage / 1048576.0;
                    s.WorkingSetMb = (ulong)mem.WorkingSetSize / 1048576.0;
                    if (m_LastPageFaults > 0 && dt > 0) s.PageFaultsPerSec = (mem.PageFaultCount - m_LastPageFaults) / dt;
                    m_LastPageFaults = mem.PageFaultCount;
                }
                if (Native.TryGetMemory(out var ms))
                {
                    s.RamTotalMb = ms.ullTotalPhys / 1048576.0;
                    s.RamAvailMb = ms.ullAvailPhys / 1048576.0;
                    s.CommitLimitMb = ms.ullTotalPageFile / 1048576.0;
                    s.CommitUsedMb = (ms.ullTotalPageFile - ms.ullAvailPageFile) / 1048576.0;
                }
            }
            catch (Exception e)
            {
                Mod.Log.Warn("resource measurement failed: " + e.Message);
            }
        }

        // ---------------- capture key ----------------

        private void CheckCaptureKey(double now)
        {
            var kb = Keyboard.current;
            if (kb == null || !kb.mKey.wasPressedThisFrame || !kb.ctrlKey.isPressed || !kb.altKey.isPressed) return;
            Capture(now - m_SessionStart);
        }

        /// <summary>Marks "it's slow now": confirms an ongoing stall, or records a manual capture event.</summary>
        public void Capture(double t)
        {
            m_CaptureCount++;
            bool inStall = m_Detector.InStall;
            if (inStall) m_Detector.ConfirmByUser();
            else m_PendingCaptures.Add(new StallEvent { Kind = "capture", StartT = t, StartUtc = DateTime.UtcNow, ConfirmedByUser = true });
            if (m_Samples.Count > 0)
            {
                var last = m_Samples[m_Samples.Count - 1];
                last.Marker = AppendMarker(last.Marker, "capture #" + m_CaptureCount);
            }
            if (Mod.Settings.BeepOnCapture) Native.Beep();
            Notify($"Captured #{m_CaptureCount}" + (inStall ? " — stall in progress, marked as confirmed" : " — saved with the 20 s before and after"), 3f);
        }

        // ---------------- events ----------------

        private void FinishEvent(StallEvent e)
        {
            e.Id = m_NextEventId++;
            if (double.IsNaN(e.EndT)) e.EndT = e.StartT;
            foreach (var (t, name) in m_Saves)
                if (t >= e.StartT - 30 && t <= e.EndT + 30) { e.SaveWrittenNearby = name; break; }
            m_Events.Add(e);

            double from = e.StartT - PreEventSeconds;
            double to = Math.Max(e.EndT + e.CatchUpSeconds, e.StartT) + Mod.Settings.PostEventSeconds;
            var window = m_Samples.Where(x => x.T >= from && x.T <= to).ToList();
            if (window.Count > 0)
            {
                double avg = 0; int n = 0;
                foreach (var x in window) if (x.T >= e.StartT && x.T <= e.EndT && !double.IsNaN(x.Ratio)) { avg += x.Ratio; n++; }
                e.AvgRatio = n > 0 ? avg / n : double.NaN;
            }

            var j = new Json().BeginObject()
                .Prop("schema", Report.SchemaVersion).Prop("id", e.Id).Prop("kind", e.Kind).Prop("confirmedByUser", e.ConfirmedByUser)
                .Prop("startUtc", e.StartUtc.ToString("o", Inv)).Prop("durationSec", e.DurationSec, 2)
                .Prop("minRatio", e.MinRatio).Prop("avgRatio", e.AvgRatio).Prop("catchUpSeconds", e.CatchUpSeconds, 2)
                .Prop("catchUpPeakRatio", e.CatchUpPeakRatio).Prop("baselineTicksPerSec", e.BaselineTicksPerSec, 1)
                .Prop("selectedSpeed", e.SelectedSpeed).Prop("saveNearby", e.SaveWrittenNearby);
            j.Name("timeline").BeginObject();
            WriteSeries(j, "tSec", window, x => x.T - e.StartT, 2);
            WriteSeries(j, "ratio", window, x => x.Ratio, 3);
            WriteSeries(j, "ticksPerSec", window, x => x.TicksPerSec, 1);
            WriteSeries(j, "frameMsAvg", window, x => x.FrameMsAvg, 1);
            WriteSeries(j, "frameMsMax", window, x => x.FrameMsMax, 1);
            WriteSeries(j, "gameCores", window, x => x.GameCores, 2);
            WriteSeries(j, "systemCpuPct", window, x => x.SystemCpuPct, 0);
            WriteSeries(j, "pageFaultsPerSec", window, x => x.PageFaultsPerSec, 0);
            WriteSeries(j, "ramAvailMb", window, x => x.RamAvailMb, 0);
            WriteSeries(j, "privateMb", window, x => x.PrivateMb, 0);
            j.Name("state").BeginArray(); foreach (var x in window) j.Value(x.State); j.EndArray();
            j.Name("marker").BeginArray(); foreach (var x in window) j.Value(x.Marker); j.EndArray();
            j.EndObject().EndObject();
            m_Writer.Write(Path.Combine(m_SessionDir, "Events", $"PD_Event_{e.Id:0000}_{e.Kind}.json"), j.ToString());
            Mod.Log.Info($"{e.Kind} #{e.Id}: {e.DurationSec:0.0} s, min {e.MinRatio:0.00}, catch-up {e.CatchUpSeconds:0.0} s");
        }

        private static void WriteSeries(Json j, string name, List<Sample> window, Func<Sample, double> f, int decimals)
        {
            j.Name(name).BeginArray();
            foreach (var x in window) j.Value(f(x), decimals);
            j.EndArray();
        }

        // ---------------- session / files ----------------

        private void StartSession(double now)
        {
            m_InSession = true;
            m_SessionStart = now;
            m_LastSampleT = now;
            m_LastFrameIndex = m_Simulation.frameIndex;
            m_Samples.Clear();
            m_Events.Clear();
            m_Saves.Clear();
            m_PendingCaptures.Clear();
            m_CsvBuffer.Clear();
            m_LastCsvFlush = 0;
            m_NextEventId = 1;
            m_CaptureCount = 0;
            m_Population = -1;
            m_LastPopulationT = -1000;
            m_SessionDir = Path.Combine(RootDirectory, "Sessions", DateTime.Now.ToString("yyyyMMdd_HHmmss", Inv));
            m_System = TakeSystemSnapshot();
            Sources.Reset();
            m_LastSourcesT = -10;
            Mod.Manager?.OnSessionStart();
            m_Writer.Write(Path.Combine(m_SessionDir, "samples.csv"),
                "utc,t_s,interval_ms,loading,selected_speed,smooth_speed,ticks_per_s,ratio,abs_speed,steps_per_frame,step_ms," +
                "path_backlog,path_headroom,limiter,quality,state,frames,frame_ms_avg,frame_ms_max," +
                "game_cores,system_cpu_pct,private_mb,working_set_mb,page_faults_per_s,ram_avail_mb,ram_total_mb,commit_used_mb,commit_limit_mb,population,marker\n");
            Mod.Log.Info("session started: " + m_SessionDir);
        }

        private void EndSession()
        {
            if (!m_InSession) return;
            m_InSession = false;
            Mod.Manager?.OnSessionEnd();
            Breakdown.Cancel();
            foreach (var c in m_PendingCaptures) { c.EndT = c.StartT; FinishEvent(c); }
            m_PendingCaptures.Clear();
            HideNotification();
            if (m_Samples.Count > 0)
            {
                FlushCsv(double.MaxValue);
                WriteReports(out _);
            }
            m_LastSessionDir = m_SessionDir;
            m_SessionDir = null;
            Mod.Log.Info("session ended");
        }

        /// <summary>Writes report.md / report.json for the current (or last) session. Returns the Markdown.</summary>
        public string WriteReports(out string directory)
        {
            directory = SessionOrLastDirectory;
            if (m_Samples.Count == 0 || directory == null || m_System == null) return null;
            string md = Report.Markdown(m_System, m_Samples, m_Events, m_Saves, Mod.Settings.StallThresholdPercent / 100.0, Mod.Settings.MinStallSeconds);
            string json = Report.JsonSummary(m_System, m_Samples, m_Events, m_Saves, Mod.Settings.StallThresholdPercent / 100.0, Mod.Settings.MinStallSeconds);
            m_Writer.Write(Path.Combine(directory, "report.md"), md);
            m_Writer.Write(Path.Combine(directory, "report.json"), json);
            return md;
        }

        private void AppendCsv(Sample s)
        {
            var b = m_CsvBuffer;
            b.Append(s.Utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", Inv)).Append(',')
             .Append(N(s.T, 3)).Append(',').Append(N(s.IntervalMs, 1)).Append(',').Append(s.Loading ? "1" : "0").Append(',')
             .Append(N(s.SelectedSpeed, 2)).Append(',').Append(N(s.SmoothSpeed, 3)).Append(',').Append(N(s.TicksPerSec, 2)).Append(',')
             .Append(N(s.Ratio, 3)).Append(',').Append(N(s.AbsRatio, 3)).Append(',').Append(N(s.StepsPerFrame, 2)).Append(',')
             .Append(N(s.StepMs, 2)).Append(',').Append(s.PathBacklog >= 0 ? s.PathBacklog.ToString(Inv) : "").Append(',')
             .Append(s.PathHeadroom >= 0 ? s.PathHeadroom.ToString(Inv) : "").Append(',').Append(s.Limiter).Append(',')
             .Append(N(s.Quality, 0)).Append(',')
             .Append(s.State).Append(',').Append(s.Frames).Append(',')
             .Append(N(s.FrameMsAvg, 2)).Append(',').Append(N(s.FrameMsMax, 2)).Append(',')
             .Append(N(s.GameCores, 3)).Append(',').Append(N(s.SystemCpuPct, 1)).Append(',')
             .Append(N(s.PrivateMb, 0)).Append(',').Append(N(s.WorkingSetMb, 0)).Append(',').Append(N(s.PageFaultsPerSec, 0)).Append(',')
             .Append(N(s.RamAvailMb, 0)).Append(',').Append(N(s.RamTotalMb, 0)).Append(',').Append(N(s.CommitUsedMb, 0)).Append(',')
             .Append(N(s.CommitLimitMb, 0)).Append(',').Append(s.Population >= 0 ? s.Population.ToString(Inv) : "").Append(',')
             .Append(s.Marker == null ? "" : "\"" + s.Marker.Replace("\"", "\"\"") + "\"").Append('\n');
        }

        private void FlushCsv(double t)
        {
            if (m_CsvBuffer.Length == 0 || m_SessionDir == null) return;
            m_Writer.Append(Path.Combine(m_SessionDir, "samples.csv"), m_CsvBuffer.ToString());
            m_CsvBuffer.Clear();
            m_LastCsvFlush = t;
        }

        private static string N(double v, int d) => double.IsNaN(v) || double.IsInfinity(v) ? "" : v.ToString("F" + d, Inv);

        private static string AppendMarker(string existing, string add) => existing == null ? add : existing + " | " + add;

        private SystemSnapshot TakeSystemSnapshot()
        {
            var snap = new SystemSnapshot
            {
                ModVersion = typeof(Mod).Assembly.GetName().Version.ToString(3),
                GameVersion = Application.version,
                Os = SystemInfo.operatingSystem,
                Cpu = SystemInfo.processorType,
                LogicalProcessors = SystemInfo.processorCount,
                RamTotalMb = SystemInfo.systemMemorySize,
                Gpu = SystemInfo.graphicsDeviceName,
                GpuMemoryMb = SystemInfo.graphicsMemorySize,
            };
            try { snap.EnabledMods = GameManager.instance.modManager.ListModsEnabled() ?? new string[0]; } catch (Exception) { }
            try { snap.PerformancePreference = m_Simulation.performancePreference.ToString(); } catch (Exception) { snap.PerformancePreference = "unknown"; }
            return snap;
        }

        private void StartSaveWatcher()
        {
            try
            {
                string saves = Path.Combine(Application.persistentDataPath, "Saves");
                if (!Directory.Exists(saves)) return;
                m_SaveWatcher = new FileSystemWatcher(saves, "*.cok")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                };
                FileSystemEventHandler handler = (_, e) => m_SaveEvents.Enqueue(Path.GetFileNameWithoutExtension(e.Name));
                m_SaveWatcher.Created += handler;
                m_SaveWatcher.Changed += handler;
                m_SaveWatcher.Renamed += (_, e) => m_SaveEvents.Enqueue(Path.GetFileNameWithoutExtension(e.Name));
                m_SaveWatcher.EnableRaisingEvents = true;
            }
            catch (Exception e)
            {
                Mod.Log.Warn("save watcher unavailable: " + e.Message);
            }
        }

        // ---------------- notifications ----------------

        private void ShowStallNotification(Sample s)
        {
            m_StallNotified = true;
            m_Notifications.AddOrUpdateNotification(NotificationId, LocalizedString.Value("Performance Detective"),
                LocalizedString.Value($"Slow motion detected: simulation at {Report.Pct(s.Ratio)} of normal"), null, null, null, null);
        }

        private void HideNotification()
        {
            if (!m_StallNotified) return;
            m_StallNotified = false;
            m_Notifications.RemoveNotification(NotificationId, 2f, null, null, null, null, null, null);
        }

        private void Notify(string text, float seconds)
        {
            m_Notifications.AddOrUpdateNotification(NotificationId + ".capture", LocalizedString.Value("Performance Detective"), LocalizedString.Value(text), null, null, null, null);
            m_Notifications.RemoveNotification(NotificationId + ".capture", seconds, null, null, null, null, null, null);
        }
    }
}
