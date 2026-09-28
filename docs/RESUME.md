# Where we left off — 2026-09-28

Everything below is committed and pushed to `main` on GitHub (neosoft-nvm/Cities2Diag). The gaming PC's checkout is
`F:\VscodeWin\Cities2Diag`; the game data is `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II`.

## State
* **Performance Detective 0.4.3** is built and installed in the local Mods folder (exactly one copy, no bundled DLLs).
  It contains: Auto-Tune (stops early when decisive, remembers the last result per city), pathfinding-threads control,
  CPU breakdown, **mini monitor** (toggle "Mini monitor ON/OFF" in the panel), **asset investigator** (panel button
  "Investigate asset packs": all saves + objects per pack in the loaded city; tabs "Used by no save", "Only other
  cities", "Barely used here"; type filter; "Copy this list").
* Not yet exercised in game: the asset investigator and the mini monitor (0.4.3 loaded fine on 2026-09-28 11:33).
* Mod settings (`PerformanceDetective.coc`): controller on, Custom profile, 2 extra pathfinding threads, tourists/events/
  taxi reductions 25 %, mini monitor on; last Auto-Tune recorded for Starford. Copy in `F:\VscodeWin\Cities2Diag-backups\`.

## Performance findings (Southwell, ~252k people, i7-6700K 8 threads, 16 GB RAM, GTX 1080 at 4K/30 Hz)
* Pathfinding threads 3 → 5 (Auto-Tune, kept): simulation 15 % → ~27 %, pathfinding queue ~1,750 → ~75.
* **Now FPS-bound**: at 1× the game runs at most 2 simulation steps per rendered frame; 95 % of frames hit that cap.
  ~8 FPS × 2 = 16 steps/s = the ~27 % measured. A step costs ~12 ms; the rest of the ~130 ms frame is rendering at 4K.
  → Next lever: raise real FPS (lower preset / resolution / LOD / shadows). Frame generation (Lossless Scaling on the
  GTX 960) does not help the simulation. Graphics settings were reset to defaults on 2026-09-27 (see incidents).
* CPU breakdown (12.1 ms/step): car navigation (Road Rule) 11.6 %, pedestrian navigation 10.6 %, citizen travel purpose
  9.6 %, happiness 8.0 %, Taxi Traffic mod 5.2 % — no single system dominates.
* Game uses ~25 GB on 16 GB RAM → paging; 32 GB RAM remains the cheapest hardware gain.

## Load time and asset packs
* Full playset (239 enabled mods, 178 asset packs, 42.9 GB of assets): launch → menu **7 min 52 s**
  (4 min 45 s of it loading 31,748 prefabs); Southwell from the menu 68 s.
* **Starford 2 test playset** (84 mods): launch → menu **4 min 25 s** (19,226 prefabs in 2 min 39 s); Starford 2 loaded
  in 34 s without missing-content complaints (2026-09-28 11:32–11:37).
* Save files are zips; `SaveGameMetadata` lists `contentPrerequisites` (the mods whose content the city contains — what
  the game checks for its missing-content warning). Code-only mods are never listed.
* Tools in `tools\` (read-only for the game; tested on this PC with Windows PowerShell 5.1):
  * `SaveInspector.cmd` — double-click, pick a save: used / not used / missing mods (CSV + Skyve list on the Desktop).
  * `AssetAudit.ps1` — all saves at once: packs used by no save (47, 661 MB) or only by other cities.
  * `SaveModList.ps1 -Save <cok>` — just the mods one save uses (.txt/.csv/.json).
  * `LeanPlayset.ps1 -Save <cok>` — enabled mods minus unused asset packs (Skyve playset JSON).
  * `TestPlayset.ps1 -Save <cok> -Keep "<ids>"` — minimal playset: the save's mods + their Paradox Mods requirements
    (from Skyve's `ModsData\Skyve\PdxModsCache.json`) + kept tools. **Standard -Keep list** (the player's "basics"):
    `74324,74604,138523,75250,80095,87190,133736,78188,75613,128269,80931,75826,125278,75804`
    = Move It, Anarchy, Node Controller, Better Bulldozer, Traffic, Road Builder, Network Tools, Extended Tooltip,
    Water Features, Zone Tools, Toggle Overlays, Plop the Growables, Platter, Skyve.
* Playset files made so far (Desktop of the gaming PC): `Southwell lean playset.json`, `Starford lean playset.json`,
  `Starford 2 test playset.json` (imported and active in Skyve as "Starford 2 test playset").
* Skyve playset export format: `{"ContractFormatVersion":-1,"GeneralData":{Id,Name,...},"SubscribedMods":{id:{Source,Id,
  Name,Version,LoadOrder,IsEnabled,IsVersionLocked,PlaysetId}},"LocalMods":{}}` (copied from Skyve's own exports in
  `ModsData\Skyve\Playsets\Shared`). Skyve imports it via Playsets → Import.

## Incidents to remember
* A mod backup under LocalLow (`ModsData\...\backup_0.3.1`) was loaded as a second mod → keep backups in
  `F:\VscodeWin\Cities2Diag-backups\` only.
* Bundling 0Harmony 2.2.2 broke mods that need Harmony 2.3 → Harmony is compile-only; never ship third-party DLLs.
* 2026-09-27: after an unclean shutdown ~25 settings files were empty; Simple Mod Checker Plus deleted them and closed
  the game (game `Settings.coc` incl. graphics reset). Its settings backup should be turned on.
* Game start is expensive for the player (4–8 min): verify installs offline, batch changes, and while the game runs build
  to a scratch folder (`-p:LocalModsPath=...`, and `CSII_USERDATAPATH` pointing to a scratch folder for the UI build).

## Next steps
1. In game: press "Investigate asset packs" and use the "Barely used here" tab; try the mini monitor while lowering
   graphics settings (FPS is now the simulation limit).
2. Make test playsets for other cities on request (`TestPlayset.ps1` with the standard -Keep list).
3. Optional: tighten Auto-Tune's small-effect rule (small options are wrongly kept ~1 in 3 when nothing helps, offline).
4. Before sharing publicly: test on other cities/PCs, traffic-mod compatibility, Paradox Mods publishing (docs/ROADMAP.md).
