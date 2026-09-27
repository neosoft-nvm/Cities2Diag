using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace PerformanceDetective
{
    /// <summary>
    /// Which subscribed asset packs are actually placed in the loaded city.
    ///
    /// Every loaded prefab is mapped to the pack it came from (its asset path: pdx_mods/&lt;mod id&gt;_&lt;version&gt;/…, or a local
    /// asset); every object in the city (anything with a PrefabRef: buildings, props, roads, decals, sub-objects of other
    /// buildings) counts as a use of its prefab. Packs with none of their objects in the city are candidates to disable —
    /// for this city only, and only if the player does not plan to use them. Read-only: nothing is changed.
    /// </summary>
    public sealed class AssetUsage
    {
        public sealed class Pack
        {
            public string ModId = "";        // Paradox Mods id, "" for local assets
            public string Name = "";         // mod name from Modding.log, else the asset file name
            public int Prefabs;              // prefabs this pack loaded
            public int UsedPrefabs;          // of those, how many are in the city
            public long Placed;              // objects in the city made from this pack
            public bool SupportOnly;         // only meshes/pieces/themes/UI: used through other assets, never placed itself
            public readonly Dictionary<string, int> Kinds = new Dictionary<string, int>();
        }

        // Prefab types that are never placed on their own; a pack made only of these supports other assets.
        private static readonly HashSet<string> s_SupportKinds = new HashSet<string>
        {
            "RenderPrefab", "CharacterStyle", "NetPiecePrefab", "ClimatePrefab", "ThemePrefab", "UIAssetCategoryPrefab",
            "UIAssetMenuPrefab", "UIGroupPrefab", "AssetPackPrefab", "ContentPrefab", "ActivityLocationPrefab",
        };

        private static readonly Regex s_Pdx = new Regex(@"assetdb://paradoxmods/pdx_mods/(\d+)_[^/]*/([^@/\]]+)", RegexOptions.Compiled);
        private static readonly Regex s_Local = new Regex(@"assetdb://user/([^@\]]+)", RegexOptions.Compiled);
        private static readonly Regex s_EnabledMod = new Regex(@"^\s*- (.+?) \((\d+)\)\s*$", RegexOptions.Compiled);

        public bool Finished { get; private set; }
        public string Error { get; private set; } = "";
        public string SavedTo { get; private set; } = "";
        public double Seconds { get; private set; }
        public long CityObjects { get; private set; }
        public List<Pack> Packs { get; private set; } = new List<Pack>();

        /// <summary>Packs with objects of their own that are not placed anywhere in this city, biggest first.</summary>
        public IEnumerable<Pack> Unused
        {
            get { foreach (var p in Packs) if (p.Placed == 0 && !p.SupportOnly) yield return p; }
        }

        public void Scan(EntityManager em, PrefabSystem prefabSystem, string sessionDir)
        {
            var clock = Stopwatch.StartNew();
            Finished = false;
            Error = "";
            SavedTo = "";
            try
            {
                var names = ReadModNames();
                var packs = new Dictionary<string, Pack>();
                var packOfPrefab = new Dictionary<Entity, Pack>();

                using (var query = em.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadOnly<PrefabData>() },
                    Options = EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities,
                }))
                using (var prefabEntities = query.ToEntityArray(Allocator.Temp))
                {
                    foreach (var entity in prefabEntities)
                    {
                        if (!prefabSystem.TryGetPrefab(entity, out PrefabBase prefab) || prefab == null) continue;
                        string source = prefab.asset?.ToString();
                        if (string.IsNullOrEmpty(source)) continue; // built into the game
                        string key, modId = "", file;
                        var m = s_Pdx.Match(source);
                        if (m.Success) { modId = m.Groups[1].Value; key = "pdx:" + modId; file = m.Groups[2].Value; }
                        else
                        {
                            var l = s_Local.Match(source);
                            if (!l.Success) continue; // game or DLC content
                            key = "local"; file = "Local assets";
                        }
                        if (!packs.TryGetValue(key, out var pack))
                        {
                            packs[key] = pack = new Pack
                            {
                                ModId = modId,
                                Name = modId.Length > 0 && names.TryGetValue(modId, out var n) ? n : Path.GetFileNameWithoutExtension(file),
                            };
                        }
                        pack.Prefabs++;
                        string kind = prefab.GetType().Name;
                        pack.Kinds[kind] = pack.Kinds.TryGetValue(kind, out var k) ? k + 1 : 1;
                        packOfPrefab[entity] = pack;
                    }
                }

                var usedPrefabs = new HashSet<Entity>();
                using (var query = em.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadOnly<PrefabRef>() },
                    None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
                }))
                using (var refs = query.ToComponentDataArray<PrefabRef>(Allocator.TempJob))
                {
                    CityObjects = refs.Length;
                    for (int i = 0; i < refs.Length; i++)
                    {
                        var p = refs[i].m_Prefab;
                        if (!packOfPrefab.TryGetValue(p, out var pack)) continue;
                        pack.Placed++;
                        if (usedPrefabs.Add(p)) pack.UsedPrefabs++;
                    }
                }

                var list = new List<Pack>(packs.Values);
                foreach (var p in list)
                {
                    p.SupportOnly = true;
                    foreach (var kind in p.Kinds.Keys)
                        if (!s_SupportKinds.Contains(kind) && !kind.StartsWith("UI", StringComparison.Ordinal)) { p.SupportOnly = false; break; }
                }
                // Unused packs first, biggest first; then used packs by how few of their objects are in the city.
                list.Sort((a, b) =>
                {
                    int ua = a.Placed == 0 && !a.SupportOnly ? 0 : 1, ub = b.Placed == 0 && !b.SupportOnly ? 0 : 1;
                    return ua != ub ? ua.CompareTo(ub) : b.Prefabs.CompareTo(a.Prefabs);
                });
                Packs = list;
                Finished = true;
                Seconds = clock.Elapsed.TotalSeconds;

                int unused = 0, unusedPrefabs = 0;
                foreach (var p in Unused) { unused++; unusedPrefabs += p.Prefabs; }
                Mod.Log.Info($"[SPC] Asset packs: {list.Count} packs, {unused} not placed in this city ({unusedPrefabs} prefabs), {CityObjects} city objects, scanned in {Seconds:0.0} s");
                Save(sessionDir);
            }
            catch (Exception e)
            {
                Error = "Asset scan failed: " + e.Message;
                Mod.Log.Warn("[SPC] Error: asset scan: " + e);
            }
        }

        /// <summary>Mod id → name from the "Enabled Mods" list the game writes to Modding.log.</summary>
        private static Dictionary<string, string> ReadModNames()
        {
            var names = new Dictionary<string, string>();
            try
            {
                string path = Path.Combine(Application.persistentDataPath, "Logs", "Modding.log");
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(fs))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        var m = s_EnabledMod.Match(line);
                        if (m.Success) names[m.Groups[2].Value] = m.Groups[1].Value;
                    }
                }
            }
            catch (Exception e) { Mod.Log.Info("[SPC] Asset packs: mod names unavailable: " + e.Message); }
            return names;
        }

        private void Save(string sessionDir)
        {
            if (string.IsNullOrEmpty(sessionDir)) return;
            var inv = CultureInfo.InvariantCulture;
            var csv = new StringBuilder("mod_id,name,status,prefabs,prefabs_in_city,objects_in_city,kinds\n");
            foreach (var p in Packs)
            {
                string status = p.Placed > 0 ? "used" : p.SupportOnly ? "support (meshes/pieces/themes)" : "not placed in this city";
                var kinds = new StringBuilder();
                foreach (var kv in p.Kinds) kinds.Append(kinds.Length > 0 ? "; " : "").Append(kv.Key).Append(' ').Append(kv.Value.ToString(inv));
                csv.Append(p.ModId).Append(',').Append(Quote(p.Name)).Append(',').Append(Quote(status)).Append(',')
                   .Append(p.Prefabs.ToString(inv)).Append(',').Append(p.UsedPrefabs.ToString(inv)).Append(',')
                   .Append(p.Placed.ToString(inv)).Append(',').Append(Quote(kinds.ToString())).Append('\n');
            }
            string path = Path.Combine(sessionDir, $"asset_packs_{DateTime.Now.ToString("yyyyMMdd_HHmmss", inv)}.csv");
            SavedTo = path;
            string text = csv.ToString();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Directory.CreateDirectory(sessionDir);
                    File.WriteAllText(path, text, new UTF8Encoding(false));
                }
                catch (Exception e) { Mod.Log.Warn("[SPC] Error: asset pack file: " + e.Message); }
            });
        }

        private static string Quote(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
    }
}
