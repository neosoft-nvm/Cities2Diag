# Where we left off — 2026-09-26

## State
* **Performance Detective 0.3.1** is built, installed in the local Mods folder and pushed (commit 87ad2aa).
* Mod settings reset to defaults (Maximum Accuracy, no reductions). Backup of the previous file:
  `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\PerformanceDetective.coc.bak`.
* Save backups of the cities were made by the player before controller testing.

## Next step (player)
1. Start CS2, load **Southwell**, press **Ctrl+Alt+P**.
2. Auto-Tune → **Thorough — keeps what helps (≈39 min)**; leave the city running at one speed, camera still.
3. Tell Claude when it has finished; results are read from
   `...\Cities Skylines II\Logs\PerformanceDetective.Mod.log` (lines with "Auto-Tune") and the session `samples.csv`
   under `...\ModsData\PerformanceDetective\Sessions\`.

## What we know so far (Southwell, ~250k population, i7-6700K 8 threads, 16 GB RAM, GTX 1080)
* Simulation at 1× runs at ~13–25 % of target speed; **pathfinding holds it back 100 % of the time**
  (queue 650–2,000 requests, 1 simulation step per rendered frame at ~12 FPS).
* The CPU is not full (game ~5 of 8 threads) while the game gives pathfinding only half of the job workers →
  "extra pathfinding threads" is the most promising lever (untested in game).
* Quick Auto-Tune run (0.3.0) was inconclusive: natural swings (13–25 %) exceed the effects, and ending the
  home-search reduction appeared to cause a burst (carry-over). 0.3.1 fixes the method (90 s settling, 2 comparisons).
* Low FPS separately caps simulation speed (max 2 steps/frame at 1× → ≤ ~40 % at 12 FPS).
* The game allocates ~25 GB on 16 GB RAM in Southwell: continuous paging, not fixable by the mod.

## Open ideas
* Pathfinding-sources panel: check which source dominates in Southwell (taxi / home searches / parking).
* Graphics/LOD test to raise FPS (raises the simulation-speed cap).
* Before sharing publicly: test on other cities/PCs, compatibility with traffic mods, Paradox Mods publishing.
* Roadmap v1.1 (panel) done; remaining roadmap items in docs/ROADMAP.md.
