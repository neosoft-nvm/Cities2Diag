using Game;
using Game.Simulation;

namespace PerformanceDetective.Controller
{
    /// <summary>
    /// Registered with UpdateBefore in the GameSimulation phase, so it runs first in every simulation step and decides
    /// which controlled systems get their turn in that step. Allocation-free; a handful of comparisons per step.
    /// </summary>
    public partial class ControllerGateSystem : GameSystemBase
    {
        private SimulationSystem m_Simulation;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
        }

        protected override void OnUpdate()
        {
            var manager = Mod.Manager;
            if (manager == null) return;
            manager.Controller.Resolve(World);
            manager.Controller.Step(m_Simulation.frameIndex);
        }
    }
}
