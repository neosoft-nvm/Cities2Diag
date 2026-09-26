using System;
using System.Collections.Generic;
using System.IO;
using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using Game.UI;
using UnityEngine;

namespace PerformanceDetective
{
    /// <summary>Options → Performance Detective.</summary>
    [FileLocation(nameof(PerformanceDetective))]
    [SettingsUITabOrder(kControllerSection, kSection)]
    [SettingsUIGroupOrder(kControllerGroup, kAdaptiveGroup, kCustomGroup, kDetectionGroup, kCaptureGroup, kReportGroup)]
    [SettingsUIShowGroupName(kControllerGroup, kAdaptiveGroup, kCustomGroup, kDetectionGroup, kCaptureGroup, kReportGroup)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kDetectionGroup = "Detection";
        public const string kCaptureGroup = "Capture";
        public const string kReportGroup = "Reports";
        public const string kControllerSection = "Controller";
        public const string kControllerGroup = "ControllerMain";
        public const string kAdaptiveGroup = "Adaptive";
        public const string kCustomGroup = "Custom";

        public Setting(IMod mod) : base(mod) { }

        // ---------------- Simulation Performance Controller ----------------
        // Most players use the in-game panel (toolbar button); these mirror it.

        [SettingsUISection(kControllerSection, kControllerGroup)]
        [SettingsUIButton]
        public bool OpenPanel
        {
            set => UI.DetectiveUISystem.PanelOpen = true;
        }

        [SettingsUISection(kControllerSection, kControllerGroup)]
        public bool ControllerEnabled { get; set; }

        [SettingsUISection(kControllerSection, kControllerGroup)]
        public Controller.Profile ProfileSetting
        {
            get => (Controller.Profile)ControllerProfile;
            set { ControllerProfile = (int)value; Mod.Manager?.Apply(); }
        }

        [SettingsUIHidden]
        public int ControllerProfile { get; set; }

        [SettingsUISection(kControllerSection, kControllerGroup)]
        public bool ShowOverlay { get; set; }

        [SettingsUISection(kControllerSection, kAdaptiveGroup)]
        public bool AdaptiveMode { get; set; }

        [SettingsUISection(kControllerSection, kAdaptiveGroup)]
        [SettingsUISlider(min = 50, max = 100, step = 5, unit = Unit.kPercentage)]
        public int TargetSpeedPercent { get; set; }

        [SettingsUISection(kControllerSection, kAdaptiveGroup)]
        [SettingsUISlider(min = 20, max = 100, step = 10, unit = Unit.kPercentage)]
        public int MinimumQuality { get; set; }

        [SettingsUISection(kControllerSection, kCustomGroup)]
        [SettingsUISlider(min = 0, max = 75, step = 25, unit = Unit.kPercentage)]
        public int CustomPets { get; set; }

        [SettingsUISection(kControllerSection, kCustomGroup)]
        [SettingsUISlider(min = 0, max = 75, step = 25, unit = Unit.kPercentage)]
        public int CustomTourists { get; set; }

        [SettingsUISection(kControllerSection, kCustomGroup)]
        [SettingsUISlider(min = 0, max = 75, step = 25, unit = Unit.kPercentage)]
        public int CustomEvents { get; set; }

        [SettingsUISection(kControllerSection, kCustomGroup)]
        [SettingsUISlider(min = 0, max = 75, step = 25, unit = Unit.kPercentage)]
        public int CustomHappiness { get; set; }

        [SettingsUISection(kControllerSection, kCustomGroup)]
        [SettingsUISlider(min = 0, max = 75, step = 25, unit = Unit.kPercentage)]
        public int CustomWorkers { get; set; }

        [SettingsUISection(kControllerSection, kCustomGroup)]
        [SettingsUISlider(min = 0, max = 75, step = 25, unit = Unit.kPercentage)]
        public int CustomCitizens { get; set; }

        [SettingsUISection(kControllerSection, kControllerGroup)]
        [SettingsUIButton]
        public bool ResetController
        {
            set
            {
                ResetControllerDefaults();
                ApplyAndSave();
                Mod.Manager?.Apply();
            }
        }

        /// <summary>Reduction (percent) of a category in the Custom profile.</summary>
        public int GetCustomReduction(string key)
        {
            switch (key)
            {
                case "pets": return CustomPets;
                case "tourists": return CustomTourists;
                case "events": return CustomEvents;
                case "happiness": return CustomHappiness;
                case "workers": return CustomWorkers;
                case "citizens": return CustomCitizens;
                default: return 0;
            }
        }

        public void SetCustomReduction(string key, int percent)
        {
            percent = Math.Max(0, Math.Min(75, percent));
            switch (key)
            {
                case "pets": CustomPets = percent; break;
                case "tourists": CustomTourists = percent; break;
                case "events": CustomEvents = percent; break;
                case "happiness": CustomHappiness = percent; break;
                case "workers": CustomWorkers = percent; break;
                case "citizens": CustomCitizens = percent; break;
            }
        }

        public void ResetControllerDefaults()
        {
            ControllerEnabled = true;
            ControllerProfile = (int)Controller.Profile.MaximumAccuracy; // no changes to the simulation until the player chooses
            AdaptiveMode = false;
            TargetSpeedPercent = 90;
            MinimumQuality = 40;
            CustomPets = CustomTourists = CustomEvents = CustomHappiness = CustomWorkers = CustomCitizens = 0;
            ShowOverlay = false;
        }

        public override void Apply()
        {
            base.Apply();
            Mod.Manager?.Apply();
        }

        [SettingsUISection(kSection, kDetectionGroup)]
        [SettingsUISlider(min = 20, max = 90, step = 5, unit = Unit.kPercentage)]
        public int StallThresholdPercent { get; set; }

        [SettingsUISection(kSection, kDetectionGroup)]
        [SettingsUISlider(min = 1, max = 10, step = 0.5f, unit = Unit.kFloatSingleFraction)]
        public float MinStallSeconds { get; set; }

        [SettingsUISection(kSection, kDetectionGroup)]
        public bool NotifyStalls { get; set; }

        [SettingsUISection(kSection, kCaptureGroup)]
        public bool BeepOnCapture { get; set; }

        [SettingsUISection(kSection, kCaptureGroup)]
        [SettingsUISlider(min = 10, max = 60, step = 5, unit = Unit.kInteger)]
        public int PostEventSeconds { get; set; }

        [SettingsUISection(kSection, kReportGroup)]
        [SettingsUIButton]
        public bool CopyReport
        {
            set
            {
                var md = Mod.Detective?.WriteReports(out _);
                if (md == null)
                {
                    Mod.Log.Info("no data yet for a report");
                    return;
                }
                GUIUtility.systemCopyBuffer = md;
                Mod.Log.Info("report copied to clipboard");
            }
        }

        [SettingsUISection(kSection, kReportGroup)]
        [SettingsUIButton]
        public bool OpenFolder
        {
            set
            {
                var dir = Mod.Detective?.SessionOrLastDirectory ?? DetectiveSystem.RootDirectory;
                Directory.CreateDirectory(dir);
                Application.OpenURL("file:///" + dir.Replace('\\', '/'));
            }
        }

        public override void SetDefaults()
        {
            StallThresholdPercent = 50;
            MinStallSeconds = 2f;
            NotifyStalls = true;
            BeepOnCapture = true;
            PostEventSeconds = 20;
            ResetControllerDefaults();
        }
    }

    /// <summary>English texts for the options page.</summary>
    public class LocaleEN : IDictionarySource
    {
        private readonly Setting m_Setting;

        public LocaleEN(Setting setting) => m_Setting = setting;

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            var s = m_Setting;
            return new Dictionary<string, string>
            {
                { s.GetSettingsLocaleID(), "Performance Detective" },
                { s.GetOptionTabLocaleID(Setting.kSection), "Main" },
                { s.GetOptionGroupLocaleID(Setting.kDetectionGroup), "Stall detection" },
                { s.GetOptionGroupLocaleID(Setting.kCaptureGroup), "Capture (Ctrl+Alt+M in game)" },
                { s.GetOptionGroupLocaleID(Setting.kReportGroup), "Reports" },

                { s.GetOptionLabelLocaleID(nameof(Setting.StallThresholdPercent)), "Stall threshold" },
                { s.GetOptionDescLocaleID(nameof(Setting.StallThresholdPercent)), "A stall starts when the simulation runs below this share of its normal speed for this city (learned during the last minute of normal play)." },
                { s.GetOptionLabelLocaleID(nameof(Setting.MinStallSeconds)), "Minimum stall length (s)" },
                { s.GetOptionDescLocaleID(nameof(Setting.MinStallSeconds)), "Shorter slowdowns are ignored, so single hitches do not count as stalls." },
                { s.GetOptionLabelLocaleID(nameof(Setting.NotifyStalls)), "Show a notification during stalls" },
                { s.GetOptionDescLocaleID(nameof(Setting.NotifyStalls)), "Shows a small notification while a slow-motion stall is detected." },

                { s.GetOptionLabelLocaleID(nameof(Setting.BeepOnCapture)), "Beep on capture" },
                { s.GetOptionDescLocaleID(nameof(Setting.BeepOnCapture)), "Plays two short tones when you press Ctrl+Alt+M, so you know the moment was captured." },
                { s.GetOptionLabelLocaleID(nameof(Setting.PostEventSeconds)), "Seconds recorded after a capture" },
                { s.GetOptionDescLocaleID(nameof(Setting.PostEventSeconds)), "A capture saves the 20 seconds before the key press and this many seconds after it." },

                { s.GetOptionTabLocaleID(Setting.kControllerSection), "Simulation controller" },
                { s.GetOptionGroupLocaleID(Setting.kControllerGroup), "Simulation Performance Controller" },
                { s.GetOptionGroupLocaleID(Setting.kAdaptiveGroup), "Adaptive mode" },
                { s.GetOptionGroupLocaleID(Setting.kCustomGroup), "Custom profile: how much less often each part runs" },

                { s.GetOptionLabelLocaleID(nameof(Setting.OpenPanel)), "Open the Performance Detective panel" },
                { s.GetOptionDescLocaleID(nameof(Setting.OpenPanel)), "Opens the in-game panel (close the Options menu to see it). Shortcut in game: Ctrl+Alt+P. The toolbar button is in the top-left button row and in the mod menu." },
                { s.GetOptionLabelLocaleID(nameof(Setting.ControllerEnabled)), "Controller enabled" },
                { s.GetOptionDescLocaleID(nameof(Setting.ControllerEnabled)), "Master switch. Off = the game runs exactly as normal; everything the controller changed is restored immediately." },
                { s.GetOptionLabelLocaleID(nameof(Setting.ProfileSetting)), "Simulation quality profile" },
                { s.GetOptionDescLocaleID(nameof(Setting.ProfileSetting)), "Trades simulation detail for less CPU work. Maximum Accuracy changes nothing. Lower profiles update citizens, pets and tourists less often, which may help a CPU-bound city keep its speed. Results depend on your PC, city and mods — use the before/after comparison in the panel to see whether it helps." },
                { s.GetEnumValueLocaleID(Controller.Profile.MaximumAccuracy), "Maximum Accuracy (normal game)" },
                { s.GetEnumValueLocaleID(Controller.Profile.Balanced), "Balanced" },
                { s.GetEnumValueLocaleID(Controller.Profile.Performance), "Performance" },
                { s.GetEnumValueLocaleID(Controller.Profile.ExtremePerformance), "Extreme Performance (significantly reduces simulation fidelity)" },
                { s.GetEnumValueLocaleID(Controller.Profile.Custom), "Custom" },
                { s.GetOptionLabelLocaleID(nameof(Setting.ShowOverlay)), "Show compact overlay" },
                { s.GetOptionDescLocaleID(nameof(Setting.ShowOverlay)), "A small always-visible box with simulation speed, FPS, what limits the simulation, and the current quality." },
                { s.GetOptionLabelLocaleID(nameof(Setting.ResetController)), "Reset controller to defaults" },
                { s.GetOptionDescLocaleID(nameof(Setting.ResetController)), "Back to Maximum Accuracy with adaptive mode off: the game runs as normal." },

                { s.GetOptionLabelLocaleID(nameof(Setting.AdaptiveMode)), "Adaptive mode" },
                { s.GetOptionDescLocaleID(nameof(Setting.AdaptiveMode)), "Automatically lowers simulation quality step by step when the simulation cannot keep the target speed, and slowly restores it when it can. Changes at most every 15 seconds; never below the minimum quality." },
                { s.GetOptionLabelLocaleID(nameof(Setting.TargetSpeedPercent)), "Target simulation speed" },
                { s.GetOptionDescLocaleID(nameof(Setting.TargetSpeedPercent)), "Share of the speed you selected (1×, 2×, 3×) that adaptive mode tries to hold." },
                { s.GetOptionLabelLocaleID(nameof(Setting.MinimumQuality)), "Minimum quality (simulation budget)" },
                { s.GetOptionDescLocaleID(nameof(Setting.MinimumQuality)), "Adaptive mode never goes below this. Lower = more simulation detail may be sacrificed." },

                { s.GetOptionLabelLocaleID(nameof(Setting.CustomPets)), "Pets" },
                { s.GetOptionDescLocaleID(nameof(Setting.CustomPets)), "Pets decide what to do less often. Rarely noticeable." },
                { s.GetOptionLabelLocaleID(nameof(Setting.CustomTourists)), "Tourist households" },
                { s.GetOptionDescLocaleID(nameof(Setting.CustomTourists)), "Tourists re-evaluate their plans less often." },
                { s.GetOptionLabelLocaleID(nameof(Setting.CustomEvents)), "Event attendance" },
                { s.GetOptionDescLocaleID(nameof(Setting.CustomEvents)), "Citizens are recruited for city events less often." },
                { s.GetOptionLabelLocaleID(nameof(Setting.CustomHappiness)), "Citizen wellbeing" },
                { s.GetOptionDescLocaleID(nameof(Setting.CustomHappiness)), "Happiness and health values update more slowly (they still update)." },
                { s.GetOptionLabelLocaleID(nameof(Setting.CustomWorkers)), "Workers" },
                { s.GetOptionDescLocaleID(nameof(Setting.CustomWorkers)), "Work-related citizen state updates less often." },
                { s.GetOptionLabelLocaleID(nameof(Setting.CustomCitizens)), "Citizen daily decisions" },
                { s.GetOptionDescLocaleID(nameof(Setting.CustomCitizens)), "Citizens decide where to go next less often. Also means fewer new trips for pathfinding. Traffic and street life may look calmer." },

                { s.GetOptionLabelLocaleID(nameof(Setting.CopyReport)), "Copy report for AI" },
                { s.GetOptionDescLocaleID(nameof(Setting.CopyReport)), "Writes report.md and report.json for the current (or last) session and copies the report to the clipboard. Paste it into the AI of your choice, a forum post or a Discord help channel. Nothing is sent anywhere by the mod." },
                { s.GetOptionLabelLocaleID(nameof(Setting.OpenFolder)), "Open log folder" },
                { s.GetOptionDescLocaleID(nameof(Setting.OpenFolder)), "Opens the folder with this session's samples, events and reports." },
            };
        }

        public void Unload() { }
    }
}
