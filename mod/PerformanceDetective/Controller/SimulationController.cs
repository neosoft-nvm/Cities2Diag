using System;
using System.Collections.Generic;
using Game;
using Game.Simulation;
using Unity.Entities;

namespace PerformanceDetective.Controller
{
    /// <summary>
    /// Runs selected game systems less often by switching their Enabled flag per simulation step.
    ///
    /// For a system with update interval I, every block of I simulation frames contains exactly one potential run,
    /// identified by r = frame / I. The run is allowed when
    ///     ((g × 7 + c × 11) mod 16) &lt; allowedSixteenths,   g = r mod 16 (slice), c = r / 16 (cycle)
    /// 7 and 11 are coprime to 16, so within a cycle exactly allowedSixteenths of the 16 slices run, and over cycles
    /// every slice gets the same share — no slice is ever starved.
    ///
    /// Safety: if a system's Enabled flag is changed by anyone else (the game disables systems after errors), the
    /// controller stops touching it. Release() re-enables everything it disabled.
    /// </summary>
    public sealed class SimulationController
    {
        private sealed class Slot
        {
            public ControlTarget Target;
            public ComponentSystemBase System;
            public int IntervalShift;       // log2(update interval)
            public int AllowedSixteenths = 16;
            public bool Controlling;        // we currently own its Enabled flag
            public bool LastSet = true;
            public bool Released;           // external change detected: hands off until reset
            public long Runs, Skips;
        }

        private readonly List<Slot> m_Slots = new List<Slot>();
        private bool m_Resolved;

        public IReadOnlyList<string> Unavailable => m_Unavailable;
        private readonly List<string> m_Unavailable = new List<string>();

        /// <summary>Finds the target systems in the running world. Missing systems are logged and skipped.</summary>
        public void Resolve(World world)
        {
            if (m_Resolved) return;
            m_Resolved = true;
            var assembly = typeof(SimulationSystem).Assembly;
            foreach (var target in ControlTargets.All)
            {
                try
                {
                    var type = assembly.GetType(target.TypeName, false);
                    var system = type != null ? world.GetExistingSystemManaged(type) : null;
                    if (system == null)
                    {
                        m_Unavailable.Add(target.Name);
                        Mod.Log.Warn($"[SPC] Compatibility: {target.TypeName} not found — '{target.Name}' control disabled");
                        continue;
                    }
                    int interval = system is GameSystemBase g ? g.GetUpdateInterval(SystemUpdatePhase.GameSimulation) : 1;
                    if (interval < 1 || (interval & (interval - 1)) != 0)
                    {
                        m_Unavailable.Add(target.Name);
                        Mod.Log.Warn($"[SPC] Compatibility: {target.TypeName} has unexpected interval {interval} — control disabled");
                        continue;
                    }
                    int shift = 0;
                    while ((1 << shift) < interval) shift++;
                    m_Slots.Add(new Slot { Target = target, System = system, IntervalShift = shift });
                    Mod.Log.Info($"[SPC] Initialization: controlling {target.TypeName} (interval {interval})");
                }
                catch (Exception e)
                {
                    m_Unavailable.Add(target.Name);
                    Mod.Log.Warn($"[SPC] Compatibility: {target.TypeName}: {e.Message} — control disabled");
                }
            }
        }

        /// <summary>Sets how much each target is reduced (0 = normal, 0.75 = 75 % fewer runs).</summary>
        public void SetReduction(string key, float reduction)
        {
            foreach (var s in m_Slots)
            {
                if (s.Target.Key != key) continue;
                int allowed = (int)Math.Round((1f - Clamp(reduction, 0f, 0.9375f)) * 16f);
                s.AllowedSixteenths = Math.Max(1, Math.Min(16, allowed));
            }
        }

        public float GetReduction(string key)
        {
            foreach (var s in m_Slots)
                if (s.Target.Key == key) return 1f - s.AllowedSixteenths / 16f;
            return 0f;
        }

        public bool IsAvailable(string key)
        {
            foreach (var s in m_Slots)
                if (s.Target.Key == key) return !s.Released;
            return false;
        }

        /// <summary>Called once per simulation step, before the game's simulation systems of that step.</summary>
        public void Step(uint frameIndex)
        {
            for (int i = 0; i < m_Slots.Count; i++)
            {
                var s = m_Slots[i];
                if (s.Released) continue;

                if (s.AllowedSixteenths >= 16)
                {
                    if (s.Controlling) ReleaseSlot(s, "restored to normal");
                    continue;
                }

                if (s.Controlling && s.System.Enabled != s.LastSet)
                {
                    s.Released = true;
                    s.Controlling = false;
                    Mod.Log.Warn($"[SPC] Compatibility: {s.Target.TypeName} was enabled/disabled by the game — no longer controlled");
                    continue;
                }
                if (!s.Controlling)
                {
                    if (!s.System.Enabled) continue; // the game has it switched off; leave it alone
                    s.Controlling = true;
                    s.LastSet = true;
                }

                uint run = frameIndex >> s.IntervalShift;
                uint slice = run & 15u, cycle = run >> 4;
                bool allow = ((slice * 7u + cycle * 11u) & 15u) < (uint)s.AllowedSixteenths;
                // Count once per run window (first frame of the window).
                if ((frameIndex & ((1u << s.IntervalShift) - 1u)) == 0u)
                {
                    if (allow) s.Runs++; else s.Skips++;
                }
                if (allow != s.LastSet)
                {
                    s.System.Enabled = allow;
                    s.LastSet = allow;
                }
            }
        }

        /// <summary>Re-enables every system this controller disabled (mod off, leaving the city, unloading).</summary>
        public void ReleaseAll(string reason)
        {
            foreach (var s in m_Slots)
            {
                if (s.Controlling) ReleaseSlot(s, reason);
            }
        }

        /// <summary>Forget "hands off" decisions (e.g. after loading another city).</summary>
        public void ResetSafety()
        {
            foreach (var s in m_Slots) s.Released = false;
        }

        private static void ReleaseSlot(Slot s, string reason)
        {
            try
            {
                if (!s.LastSet) s.System.Enabled = true;
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"[SPC] Error: could not re-enable {s.Target.TypeName}: {e.Message}");
            }
            s.Controlling = false;
            s.LastSet = true;
        }

        public IEnumerable<(ControlTarget Target, float Reduction, bool Active, long Runs, long Skips)> Status()
        {
            foreach (var s in m_Slots)
                yield return (s.Target, 1f - s.AllowedSixteenths / 16f, s.Controlling && !s.Released, s.Runs, s.Skips);
        }

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
