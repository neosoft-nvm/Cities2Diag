using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;

namespace PerformanceDetective
{
    public class Mod : IMod
    {
        public static ILog Log { get; } = LogManager.GetLogger($"{nameof(PerformanceDetective)}.{nameof(Mod)}").SetShowsErrorsInUI(false);
        public static Setting Settings { get; private set; }
        public static DetectiveSystem Detective { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            Log.Info(nameof(OnLoad));

            Settings = new Setting(this);
            Settings.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(Settings));
            AssetDatabase.global.LoadSettings(nameof(PerformanceDetective), Settings, new Setting(this));

            // UI update runs every rendered frame, also while the simulation is paused or slow.
            updateSystem.UpdateAt<DetectiveSystem>(SystemUpdatePhase.UIUpdate);
            Detective = updateSystem.World.GetOrCreateSystemManaged<DetectiveSystem>();
        }

        public void OnDispose()
        {
            Log.Info(nameof(OnDispose));
            if (Settings != null)
            {
                Settings.UnregisterInOptionsUI();
                Settings = null;
            }
        }
    }
}
