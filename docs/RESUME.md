# Where we left off — 2026-09-27

## State
* **Performance Detective 0.4.2** (CPU breakdown + faster Auto-Tune) is built and installed in the local Mods folder.
  0.4.2 stops shipping 0Harmony.dll: the bundled 2.2.2 loaded first and broke mods needing Harmony 2.3
  (NA Highway Signs: MissingMethodException every frame). Harmony is compile-only now; never bundle it.
  Backup of 0.3.1 and its settings: `F:\VscodeWin\Cities2Diag-backups\` (never under LocalLow: the game loads it as a mod).
* Thorough Auto-Tune on **Southwell** finished (2026-09-26 evening, 13 blocks):
  normal game 15.0 % simulation speed; **pathfinding threads 3 → 5: +11.3 points** (+14.0 and +8.7 in the two
  comparisons), queue −868 → **kept and saved**. Taxi dispatch −75 % and home searches −75 %: +1.1 points each,
  inside the 7-point natural swing of the baseline blocks → no effect.
* After the change (33 min of play): ~23 % speed, pathfinding queue ~400 (median 125) instead of ~1,750.
* The CPU is now the ceiling: game uses ~6.9 of 8 threads, system CPU ~94 %, ~8 FPS (128 ms frames).
  Game has 7 job workers, default 3 pathfinding threads; more threads are unlikely to help with no CPU left.
* PC: i7-6700K, 16 GB RAM (game ~25 GB in Southwell → paging), GTX 1080 + GTX 960 (2 GB), 4K desktop at 30 Hz.
  Lossless Scaling installed; it can only smooth the picture, not speed up the simulation.

## 0.4.0 — CPU breakdown (merged into main in 0.4.1)
* Panel button "Run CPU breakdown (1 min)": times every simulation system per step (Harmony patch on
  SystemBase.Update, installed only during the minute; jobs are completed per system so worker time is counted).
  Results in the panel, the log and `cpu_breakdown_*.csv` in the session folder.
* Compiles with the game's assemblies; not yet run in game — run it once in Southwell.

## Auto-Tune takeaways
* 39-minute runs are not needed routinely: the result is saved. Rerun only after large city growth or new options.
* Ideas to shorten it: stop early on obvious results, remember results per city, 45 s settling for thread tests,
  leave small-effect options out of the default run.

## Open ideas
* More RAM (32 GB) is probably the cheapest real gain in Southwell.
* Before sharing publicly: test on other cities/PCs, compatibility with traffic mods, Paradox Mods publishing.
* Remaining roadmap items in docs/ROADMAP.md.
