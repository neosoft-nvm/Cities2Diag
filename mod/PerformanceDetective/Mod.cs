using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using PerformanceDetective.Controller;

namespace PerformanceDetective
{
    public class Mod : IMod
    {
        public static ILog Log { get; } = LogManager.GetLogger($"{nameof(PerformanceDetective)}.{nameof(Mod)}").SetShowsErrorsInUI(false);
        public static Setting Settings { get; private set; }
        public static DetectiveSystem Detective { get; private set; }
        public static ControllerManager Manager { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            Log.Info("[SPC] Initialization: OnLoad " + typeof(Mod).Assembly.GetName().Version);

            Settings = new Setting(this);
            Settings.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(Settings));
            AssetDatabase.global.LoadSettings(nameof(PerformanceDetective), Settings, new Setting(this));

            Manager = new ControllerManager();

            // Measurement: every rendered frame (also while paused or slow).
            updateSystem.UpdateAt<DetectiveSystem>(SystemUpdatePhase.UIUpdate);
            Detective = updateSystem.World.GetOrCreateSystemManaged<DetectiveSystem>();

            // Controller gate: first system of every simulation step.
            updateSystem.UpdateBefore<ControllerGateSystem>(SystemUpdatePhase.GameSimulation);

            // End of every simulation step (closes the CPU breakdown's step bracket).
            updateSystem.UpdateAfter<StepEndSystem>(SystemUpdatePhase.GameSimulation);

            // In-game panel and overlay.
            updateSystem.UpdateAt<UI.DetectiveUISystem>(SystemUpdatePhase.UIUpdate);
        }

        public void OnDispose()
        {
            Log.Info("[SPC] Initialization: OnDispose");
            Manager?.Controller.ReleaseAll("mod unloaded");
            Manager?.Threads.Restore();
            Detective?.Breakdown.Cancel();
            if (Settings != null)
            {
                Settings.UnregisterInOptionsUI();
                Settings = null;
            }
        }
    }
}
