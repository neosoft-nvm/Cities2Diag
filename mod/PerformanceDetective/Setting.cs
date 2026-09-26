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
    [SettingsUIGroupOrder(kDetectionGroup, kCaptureGroup, kReportGroup)]
    [SettingsUIShowGroupName(kDetectionGroup, kCaptureGroup, kReportGroup)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kDetectionGroup = "Detection";
        public const string kCaptureGroup = "Capture";
        public const string kReportGroup = "Reports";

        public Setting(IMod mod) : base(mod) { }

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

                { s.GetOptionLabelLocaleID(nameof(Setting.CopyReport)), "Copy report for AI" },
                { s.GetOptionDescLocaleID(nameof(Setting.CopyReport)), "Writes report.md and report.json for the current (or last) session and copies the report to the clipboard. Paste it into the AI of your choice, a forum post or a Discord help channel. Nothing is sent anywhere by the mod." },
                { s.GetOptionLabelLocaleID(nameof(Setting.OpenFolder)), "Open log folder" },
                { s.GetOptionDescLocaleID(nameof(Setting.OpenFolder)), "Opens the folder with this session's samples, events and reports." },
            };
        }

        public void Unload() { }
    }
}
