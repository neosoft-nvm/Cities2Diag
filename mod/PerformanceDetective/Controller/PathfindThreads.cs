using System;
using System.Reflection;
using Game.Pathfind;
using Unity.Entities;
using Unity.Jobs.LowLevel.Unsafe;

namespace PerformanceDetective.Controller
{
    /// <summary>
    /// How many pathfinding jobs the game runs side by side. The game sets PathfindQueueSystem.m_MaxThreadCount to
    /// max(1, JobWorkerCount / 2) at creation and reads it only when scheduling pathfinding jobs; its per-thread lists
    /// grow on demand. Raising it gives pathfinding more of the worker threads — useful when pathfinding is the
    /// bottleneck while CPU threads are idle; it costs other simulation jobs CPU time when the CPU is already full.
    ///
    /// This is a private field (reflection, written from the main thread only when the value changes). If the field
    /// is missing after a game update the control is disabled and logged. Restore() always writes back the game's value.
    /// </summary>
    public sealed class PathfindThreads
    {
        private FieldInfo m_Field;
        private PathfindQueueSystem m_System;
        private bool m_Resolved;
        private int m_Current = -1;

        public bool Available { get; private set; }
        public int Default { get; private set; }
        public int Workers => JobsUtility.JobWorkerCount;
        public int Current => m_Current < 0 ? Default : m_Current;
        public int MaxExtra => Available ? Math.Max(0, Workers - Default) : 0;

        public void Resolve(World world)
        {
            if (m_Resolved) return;
            m_Resolved = true;
            try
            {
                m_System = world.GetExistingSystemManaged<PathfindQueueSystem>();
                m_Field = typeof(PathfindQueueSystem).GetField("m_MaxThreadCount", BindingFlags.Instance | BindingFlags.NonPublic);
                if (m_System == null || m_Field == null || m_Field.FieldType != typeof(int))
                {
                    Mod.Log.Warn("[SPC] Compatibility: pathfinding thread count not found — 'Pathfinding threads' control disabled");
                    return;
                }
                Default = (int)m_Field.GetValue(m_System);
                Available = Default >= 1;
                Mod.Log.Info($"[SPC] Pathfinding: game uses {Default} pathfinding threads of {Workers} job workers");
            }
            catch (Exception e)
            {
                Mod.Log.Warn("[SPC] Compatibility: pathfinding thread count unavailable: " + e.Message);
            }
        }

        /// <summary>Game default plus <paramref name="extra"/> threads, capped at the number of job workers.</summary>
        public void SetExtra(int extra)
        {
            if (!Available) return;
            int value = Math.Max(1, Math.Min(Workers, Default + Math.Max(0, extra)));
            if (value == Current) return;
            try
            {
                m_Field.SetValue(m_System, value);
                m_Current = value;
                Mod.Log.Info($"[SPC] Pathfinding: {value} pathfinding threads (game default {Default})");
            }
            catch (Exception e)
            {
                Available = false;
                Mod.Log.Warn("[SPC] Error: could not set pathfinding threads: " + e.Message);
            }
        }

        public void Restore() => SetExtra(0);
    }
}
