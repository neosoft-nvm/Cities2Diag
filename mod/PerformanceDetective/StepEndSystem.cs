using Game;

namespace PerformanceDetective
{
    /// <summary>
    /// Registered with UpdateAfter in the GameSimulation phase, so it runs last in every simulation step.
    /// Closes the step bracket that ControllerGateSystem opens; does nothing unless a CPU breakdown is running.
    /// </summary>
    public partial class StepEndSystem : GameSystemBase
    {
        protected override void OnUpdate() => CpuBreakdown.StepEnd();
    }
}
