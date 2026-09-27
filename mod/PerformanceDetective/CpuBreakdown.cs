using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Unity.Entities;

namespace PerformanceDetective
{
    /// <summary>
    /// One-minute CPU breakdown: how much of each simulation step every game system takes.
    ///
    /// Most simulation work runs as jobs on worker threads, so timing a system's Update alone would only show how long
    /// it takes to schedule them. While the breakdown runs, each simulation system's jobs are therefore completed right
    /// after its Update, and the time until they finish is counted for that system. This removes the overlap between
    /// systems, so the simulation runs slower during the minute; the shares are what matter, not the absolute speed.
    ///
    /// Only systems updated inside a simulation step are timed (between ControllerGateSystem and StepEndSystem).
    /// Pathfinding runs on its own threads outside the steps and is not part of this breakdown.
    ///
    /// The Harmony patch on SystemBase.Update is installed when a breakdown starts and removed when it ends, so the
    /// mod costs nothing when this feature is not in use.
    /// </summary>
    public sealed class CpuBreakdown
    {
        public sealed class Entry
        {
            public string Name;
            public double TotalMs;
            public long Calls;
        }

        public const double DurationSeconds = 60;
        private const string HarmonyId = "PerformanceDetective.CpuBreakdown";

        private static CpuBreakdown s_Active;
        private static readonly double s_MsPerTick = 1000.0 / Stopwatch.Frequency;

        // object, not Harmony: 0Harmony is provided by other mods at runtime. Keeping its types out of fields and out of
        // methods that always run means the mod still loads (breakdown unavailable) if no mod provides it.
        private object m_Harmony;
        private readonly Dictionary<Type, Entry> m_Entries = new Dictionary<Type, Entry>();
        private bool m_InStep;
        private int m_Depth;
        private long m_StepStart;
        private double m_StepMsTotal;
        private long m_Steps;
        private double m_RunSeconds;

        public bool Running { get; private set; }
        public bool Finished { get; private set; }
        public double Remaining => Math.Max(0, DurationSeconds - m_RunSeconds);
        public long Steps => m_Steps;
        /// <summary>Average measured time of a whole simulation step during the breakdown.</summary>
        public double StepMs => m_Steps > 0 ? m_StepMsTotal / m_Steps : double.NaN;
        public List<Entry> Result { get; private set; } = new List<Entry>();
        public string Error { get; private set; } = "";
        public string SavedTo { get; private set; } = "";

        public void Start()
        {
            if (Running) return;
            m_Entries.Clear();
            m_Steps = 0;
            m_StepMsTotal = 0;
            m_RunSeconds = 0;
            m_InStep = false;
            m_Depth = 0;
            Result = new List<Entry>();
            Error = "";
            SavedTo = "";
            Finished = false;
            try
            {
                m_Harmony = Patch();
            }
            catch (Exception e)
            {
                Error = "CPU breakdown is not available: " + e.Message;
                Mod.Log.Warn("[SPC] Compatibility: CPU breakdown unavailable: " + e.Message);
                Unpatch();
                return;
            }
            s_Active = this;
            Running = true;
            Mod.Log.Info("[SPC] CPU breakdown: started");
        }

        public void Cancel()
        {
            if (!Running) return;
            Stop();
            Mod.Log.Info("[SPC] CPU breakdown: stopped early");
        }

        /// <summary>Called once per rendered frame with the real time that passed while the simulation was running.</summary>
        public void Tick(double runningSeconds, string sessionDir)
        {
            if (!Running) return;
            m_RunSeconds += runningSeconds;
            if (m_RunSeconds < DurationSeconds) return;
            Stop();
            Finish(sessionDir);
        }

        // Simulation step bracket: ControllerGateSystem runs first in every step, StepEndSystem last.
        public static void StepBegin()
        {
            var a = s_Active;
            if (a == null) return;
            a.m_InStep = true;
            a.m_Depth = 0;
            a.m_StepStart = Stopwatch.GetTimestamp();
        }

        public static void StepEnd()
        {
            var a = s_Active;
            if (a == null || !a.m_InStep) return;
            a.m_InStep = false;
            a.m_StepMsTotal += (Stopwatch.GetTimestamp() - a.m_StepStart) * s_MsPerTick;
            a.m_Steps++;
        }

        private static void Prefix(out long __state)
        {
            __state = 0;
            var a = s_Active;
            if (a == null || !a.m_InStep) return;
            // Only the outermost system counts, so a system that updates others is not counted twice.
            // __state: 0 = not tracked, -1 = nested call, otherwise the start timestamp.
            __state = a.m_Depth++ == 0 ? Stopwatch.GetTimestamp() : -1;
        }

        private static void Postfix(SystemBase __instance, long __state)
        {
            var a = s_Active;
            if (a == null || !a.m_InStep) return;
            // __state == 0: the call started before the step bracket opened (ControllerGateSystem itself).
            if (__state == 0) return;
            a.m_Depth--;
            if (__state < 0) return;
            a.m_Depth = 0;
            try { __instance.CheckedStateRef.Dependency.Complete(); }
            catch (Exception) { /* a system without jobs, or already disposed: its Update time still counts */ }
            double ms = (Stopwatch.GetTimestamp() - __state) * s_MsPerTick;
            var type = __instance.GetType();
            if (!a.m_Entries.TryGetValue(type, out var e))
                a.m_Entries[type] = e = new Entry { Name = type.FullName ?? type.Name };
            e.TotalMs += ms;
            e.Calls++;
        }

        private void Stop()
        {
            s_Active = null;
            Running = false;
            Unpatch();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static object Patch()
        {
            var harmony = new Harmony(HarmonyId);
            var target = AccessTools.Method(typeof(SystemBase), nameof(SystemBase.Update));
            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(CpuBreakdown), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(CpuBreakdown), nameof(Postfix)));
            return harmony;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void UnpatchAll(object harmony) => ((Harmony)harmony).UnpatchAll(HarmonyId);

        private void Unpatch()
        {
            try { if (m_Harmony != null) UnpatchAll(m_Harmony); }
            catch (Exception e) { Mod.Log.Warn("[SPC] Error: CPU breakdown unpatch: " + e.Message); }
            m_Harmony = null;
        }

        private void Finish(string sessionDir)
        {
            var list = new List<Entry>(m_Entries.Values);
            list.Sort((x, y) => y.TotalMs.CompareTo(x.TotalMs));
            Result = list;
            Finished = true;

            double total = 0;
            foreach (var e in list) total += e.TotalMs;
            var inv = CultureInfo.InvariantCulture;
            var log = new StringBuilder();
            log.Append($"[SPC] CPU breakdown: finished, {m_Steps} steps, {StepMs.ToString("0.00", inv)} ms per step; top systems:");
            for (int i = 0; i < Math.Min(15, list.Count); i++)
                log.Append($" {ShortName(list[i].Name)} {(list[i].TotalMs / Math.Max(1, m_Steps)).ToString("0.00", inv)} ms ({(100 * list[i].TotalMs / Math.Max(1e-9, total)).ToString("0.0", inv)}%);");
            Mod.Log.Info(log.ToString());

            if (string.IsNullOrEmpty(sessionDir)) return;
            var csv = new StringBuilder("system,ms_per_step,share_pct,calls,total_ms\n");
            foreach (var e in list)
                csv.Append(e.Name).Append(',')
                   .Append((e.TotalMs / Math.Max(1, m_Steps)).ToString("0.000", inv)).Append(',')
                   .Append((100 * e.TotalMs / Math.Max(1e-9, total)).ToString("0.00", inv)).Append(',')
                   .Append(e.Calls).Append(',')
                   .Append(e.TotalMs.ToString("0.0", inv)).Append('\n');
            string path = Path.Combine(sessionDir, $"cpu_breakdown_{DateTime.Now.ToString("yyyyMMdd_HHmmss", inv)}.csv");
            SavedTo = path;
            string text = csv.ToString();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Directory.CreateDirectory(sessionDir);
                    File.WriteAllText(path, text, new UTF8Encoding(false));
                }
                catch (Exception e) { Mod.Log.Warn("[SPC] Error: CPU breakdown file: " + e.Message); }
            });
        }

        /// <summary>"Game.Simulation.ResidentAISystem" → "ResidentAISystem".</summary>
        public static string ShortName(string fullName)
        {
            int dot = fullName.LastIndexOf('.');
            return dot < 0 ? fullName : fullName.Substring(dot + 1);
        }
    }
}
