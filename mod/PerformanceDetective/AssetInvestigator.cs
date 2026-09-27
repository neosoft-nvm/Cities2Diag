using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace PerformanceDetective
{
    /// <summary>
    /// Game-wide view of the enabled asset packs: which of your saves use each one.
    ///
    /// Every save (.cok) is a zip whose SaveGameMetadata lists "contentPrerequisites" — the mods whose content the city
    /// contains (the game uses it for its missing-content warning). Only that small entry is read, on a background thread,
    /// so all saves are checked in seconds without loading them. Combined with <see cref="AssetUsage"/> (objects per pack
    /// in the loaded city) this gives: packs no save uses, packs only other cities use, and packs barely used here.
    /// Read-only: disabling is left to the player (Skyve or the Paradox Mods playset).
    /// </summary>
    public sealed class AssetInvestigator
    {
        public sealed class Row
        {
            public string Id = "", Name = "";
            public double SizeMb;              // .cok asset files in the newest cached version
            public bool HasCode;
            public readonly List<string> Cities = new List<string>();   // saves (city names) that use it
            public bool UsedHere;               // listed by the loaded city's save or placed in the city scan
            public long PlacedHere = -1;        // objects in the loaded city (-1 = city not scanned)
            public string Kind = "";            // main prefab type from the city scan
        }

        private static readonly Regex s_EnabledMod = new Regex(@"^\s*- (.+?) \((\d+)\)\s*$", RegexOptions.Compiled);
        private static readonly Regex s_Prereqs = new Regex("\"contentPrerequisites\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex s_ModRef = new Regex("\"Mod:(\\d+)\"", RegexOptions.Compiled);
        private static readonly Regex s_City = new Regex("\"cityName\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);
        private static readonly Regex s_AutoSave = new Regex("\"autoSave\"\\s*:\\s*true", RegexOptions.Compiled);

        private volatile bool m_Running;
        public bool Running => m_Running;
        public bool Finished { get; private set; }
        public string Error { get; private set; } = "";
        public int SavesChecked { get; private set; }
        public int EnabledMods { get; private set; }
        public List<Row> Rows { get; private set; } = new List<Row>();
        public string SavedTo { get; private set; } = "";

        /// <summary>Start the game-wide check on a background thread. <paramref name="city"/>: the loaded city's name.</summary>
        public void Start(string city, AssetUsage cityScan, string sessionDir)
        {
            if (m_Running) return;
            m_Running = true;
            Error = "";
            string root = Application.persistentDataPath;
            // Snapshot the city scan on the main thread; the worker only reads this copy.
            var placed = new Dictionary<string, (long Placed, string Kind)>();
            if (cityScan != null && cityScan.Finished)
                foreach (var p in cityScan.Packs)
                    if (p.ModId.Length > 0) placed[p.ModId] = (p.Placed, MainKind(p.Kinds));
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Run(root, city ?? "", placed, sessionDir); }
                catch (Exception e)
                {
                    Error = "Asset check failed: " + e.Message;
                    Mod.Log.Warn("[SPC] Error: asset investigator: " + e);
                }
                finally { m_Running = false; }
            });
        }

        private void Run(string root, string city, Dictionary<string, (long Placed, string Kind)> placed, string sessionDir)
        {
            // Enabled mods from the last game start.
            var enabled = new List<(string Id, string Name)>();
            using (var fs = new FileStream(Path.Combine(root, "Logs", "Modding.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs))
            {
                var seen = new HashSet<string>();
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var m = s_EnabledMod.Match(line);
                    if (m.Success && seen.Add(m.Groups[2].Value)) enabled.Add((m.Groups[2].Value, m.Groups[1].Value));
                }
            }

            // Newest cached folder per mod id.
            var folders = new Dictionary<string, (int Ver, string Path)>();
            string cache = Path.Combine(root, ".cache", "Mods", "pdx_mods");
            if (Directory.Exists(cache))
                foreach (var dir in Directory.GetDirectories(cache))
                {
                    string name = Path.GetFileName(dir);
                    int us = name.IndexOf('_');
                    if (us <= 0 || !int.TryParse(name.Substring(us + 1), out int ver)) continue;
                    string id = name.Substring(0, us);
                    if (!folders.TryGetValue(id, out var f) || f.Ver < ver) folders[id] = (ver, dir);
                }

            // Which saves use which mods (autosaves skipped: they repeat a city that has its own save).
            var usedBy = new Dictionary<string, HashSet<string>>();
            int saves = 0;
            string savesDir = Path.Combine(root, "Saves");
            if (Directory.Exists(savesDir))
                foreach (var file in Directory.GetFiles(savesDir, "*.cok", SearchOption.AllDirectories))
                {
                    string meta = ReadMetadata(file);
                    if (meta == null || s_AutoSave.IsMatch(meta)) continue;
                    saves++;
                    var cm = s_City.Match(meta);
                    string cityName = cm.Success ? Regex.Unescape(cm.Groups[1].Value) : Path.GetFileNameWithoutExtension(file);
                    var pm = s_Prereqs.Match(meta);
                    if (!pm.Success) continue;
                    foreach (Match mod in s_ModRef.Matches(pm.Groups[1].Value))
                    {
                        if (!usedBy.TryGetValue(mod.Groups[1].Value, out var set)) usedBy[mod.Groups[1].Value] = set = new HashSet<string>();
                        set.Add(cityName);
                    }
                }

            var rows = new List<Row>();
            foreach (var (id, name) in enabled)
            {
                var row = new Row { Id = id, Name = name };
                if (folders.TryGetValue(id, out var f))
                {
                    long bytes = 0;
                    foreach (var file in Directory.GetFiles(f.Path, "*", SearchOption.AllDirectories))
                    {
                        string ext = Path.GetExtension(file);
                        if (ext.Equals(".cok", StringComparison.OrdinalIgnoreCase)) bytes += new FileInfo(file).Length;
                        else if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)) row.HasCode = true;
                    }
                    row.SizeMb = bytes / (1024.0 * 1024.0);
                }
                if (row.SizeMb <= 0) continue; // code-only mods are not asset packs
                if (usedBy.TryGetValue(id, out var cities))
                {
                    row.Cities.AddRange(cities);
                    row.Cities.Sort(StringComparer.OrdinalIgnoreCase);
                }
                if (placed.TryGetValue(id, out var p)) { row.PlacedHere = p.Placed; row.Kind = p.Kind; }
                row.UsedHere = row.Cities.Contains(city) || row.PlacedHere > 0;
                rows.Add(row);
            }
            rows.Sort((a, b) => b.SizeMb.CompareTo(a.SizeMb));

            Rows = rows;
            SavesChecked = saves;
            EnabledMods = enabled.Count;
            Finished = true;
            double unusedMb = 0; int unused = 0;
            foreach (var r in rows) if (r.Cities.Count == 0 && !r.UsedHere) { unused++; unusedMb += r.SizeMb; }
            Mod.Log.Info($"[SPC] Asset investigator: {saves} saves, {enabled.Count} enabled mods, {rows.Count} asset packs, {unused} used by no save ({unusedMb:0} MB)");
            Save(sessionDir, city);
        }

        /// <summary>The SaveGameMetadata entry of a save (small JSON), or null.</summary>
        private static string ReadMetadata(string file)
        {
            try
            {
                using (var zip = ZipFile.OpenRead(file))
                    foreach (var entry in zip.Entries)
                        if (entry.FullName.EndsWith(".SaveGameMetadata", StringComparison.OrdinalIgnoreCase))
                            using (var reader = new StreamReader(entry.Open()))
                                return reader.ReadToEnd();
            }
            catch (Exception e) { Mod.Log.Info("[SPC] Asset investigator: skipped " + Path.GetFileName(file) + ": " + e.Message); }
            return null;
        }

        public static string MainKind(Dictionary<string, int> kinds)
        {
            string best = ""; int n = -1;
            foreach (var kv in kinds)
                if (kv.Value > n && kv.Key != "RenderPrefab") { best = kv.Key; n = kv.Value; }
            return best.EndsWith("Prefab", StringComparison.Ordinal) ? best.Substring(0, best.Length - 6) : best;
        }

        /// <summary>Plain-text list for Skyve or the playset: one "id  name  (size)" per line.</summary>
        public static string ToText(IEnumerable<Row> rows)
        {
            var sb = new StringBuilder();
            foreach (var r in rows) sb.Append(r.Id).Append('\t').Append(r.Name).Append('\t').Append(r.SizeMb.ToString("0", CultureInfo.InvariantCulture)).Append(" MB\n");
            return sb.ToString();
        }

        private void Save(string sessionDir, string city)
        {
            if (string.IsNullOrEmpty(sessionDir)) return;
            var inv = CultureInfo.InvariantCulture;
            var csv = new StringBuilder("mod_id,name,size_mb,has_code,saves_using,used_in_" + Regex.Replace(city, "[^A-Za-z0-9]+", "_") + ",objects_here,main_type,cities\n");
            foreach (var r in Rows)
                csv.Append(r.Id).Append(',').Append(Quote(r.Name)).Append(',').Append(r.SizeMb.ToString("0.0", inv)).Append(',')
                   .Append(r.HasCode ? "yes" : "no").Append(',').Append(r.Cities.Count.ToString(inv)).Append(',')
                   .Append(r.UsedHere ? "yes" : "no").Append(',').Append(r.PlacedHere < 0 ? "" : r.PlacedHere.ToString(inv)).Append(',')
                   .Append(Quote(r.Kind)).Append(',').Append(Quote(string.Join("; ", r.Cities))).Append('\n');
            string path = Path.Combine(sessionDir, $"asset_investigator_{DateTime.Now.ToString("yyyyMMdd_HHmmss", inv)}.csv");
            try
            {
                Directory.CreateDirectory(sessionDir);
                File.WriteAllText(path, csv.ToString(), new UTF8Encoding(false));
                SavedTo = path;
            }
            catch (Exception e) { Mod.Log.Warn("[SPC] Error: asset investigator file: " + e.Message); }
        }

        private static string Quote(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
    }
}
