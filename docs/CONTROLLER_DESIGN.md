# Simulation Performance Controller — findings and design

Part of **Performance Detective** (one mod: monitor + controller). Implements
`CS2_Simulation_Performance_Controller_Specification` v1.0 (2026-09-26).

Everything below was verified against the installed game (Game.dll, game version as of 2026-09-26) by reading
metadata and a local decompilation kept **outside** this repository (game code is never committed).
Re-verify after every game update.

## 1. How the game paces the simulation (the "slow motion, then warp speed" mechanism)

Per rendered frame, `SimulationSystem` decides how many simulation steps (`num`) to run, then runs them in a loop
(`GameSimulation` phase once per step, `frameIndex++` each step):

1. **Target:** 60 steps per real second × selected speed.
2. **Pathfinding wall:** the simulation may not advance past `PathfindResultSystem.pendingSimulationFrame` (the frame
   by which queued path results are due). As the gap shrinks below 48 frames, the speed is scaled down; at 0 the
   simulation waits. **When pathfinding falls behind, the simulation deliberately runs in slow motion**, then catches
   up when results arrive.
3. **Frame-rate protection:** unless *Performance Preference* = Simulation Speed, steps per frame are capped from the
   frame's timing, so FPS is protected and the *simulation* absorbs the slowdown.
4. **Catch-up cap:** at most `clamp(round(speed × 2), 1, 8)` steps per rendered frame.

Measured on the test PC (Southwell, 25 GB city, 1× speed): 4–8 steps/s = ~7–13 % of the 60 steps/s target.

### Readable from a mod (public API)

| Value | Source | Use |
|---|---|---|
| steps done / target | `SimulationSystem.frameIndex`, `selectedSpeed`, 60/s | absolute simulation speed |
| game's own smoothed speed | `SimulationSystem.smoothSpeed` | cross-check |
| CPU time per step | `SimulationSystem.frameDuration` | per-step cost |
| pathfinding headroom | `PathfindResultSystem.pendingSimulationFrame − frameIndex` | "held back by pathfinding" when < 48 |
| pathfinding backlog | `PathfindResultSystem.pendingRequestCount` | queue size |
| trade-off setting | `SimulationSystem.performancePreference` | "limited by frame-rate protection" |

→ The panel can show **what is limiting the simulation right now**: pathfinding / frame-rate protection / CPU per step.

## 2. How systems are scheduled (the control point)

`UpdateSystem.Update(phase, frameIndex, iteration)` runs each registered system when
`(frameIndex & (interval − 1)) == offset`, by calling `system.Update()`. Unity ECS skips a system whose `Enabled`
is false. A mod system registered at the start of `GameSimulation` runs once per simulation step and can decide,
per step, whether a target system gets its turn by setting `Enabled`.

* No Harmony, no change to game code, instantly reversible (set `Enabled = true`).
* If the game itself disables a system (e.g. after an error), the controller must notice (`Enabled` differs from what
  it last set) and release that system instead of re-enabling it.

## 3. Control-point catalogue (first pass)

`Game.Simulation`: 194 systems run on an interval (of 926 game systems overall).

**Class A — rotating slices (safe to defer).** The system processes one of N entity groups per run (`UpdateFrame`
shared component). Skipping a run delays that group by one cycle; no data is lost. The skip pattern must rotate
(a period dividing the slice count would starve some groups forever). 27 systems, including:

| Category | Systems |
|---|---|
| Citizens | CitizenBehaviorSystem (16), CitizenHappinessSystem (16), WorkerSystem (16), FindEventAttendantsSystem (16) |
| Households / pets / tourists | HouseholdPetBehaviorSystem (16), TouristHouseholdBehaviorSystem (64), TouristLeaveSystem (512) |
| Traffic | TrafficFlowSystem (512), TrafficLightSystem (4) |
| Economy-adjacent (read-only stats) | CompanyStatisticsSystem, WealthStatisticsSystem, CityServiceStatisticsSystem (512) |
| Other | StreetLightSystem, TreeGrowthSystem, AttractionSystem, BuildingEfficiencySystem, … |

Each still needs a manual check that it does not consume per-frame events before it gets a slider.

**Class B — whole-set decision systems (riskier).** Process all entities of a kind per run, e.g. vehicle/service AI
(`PoliceCarAISystem` etc., interval 16). Skipping delays decisions such as arrival handling. Candidates only with
testing; **emergency services protected by default** (spec 7.6).

**Class C — not throttled.** Movement systems (`CarMoveSystem`, `HumanMoveSystem`: every step), systems that
consume queues/events, money/resource transactions (spec 7.7 consistency), serialization.

**Pathfinding.** Not interval-based. Levers found:
* Worker threads: `PathfindQueueSystem` uses `max(1, JobWorkerCount / 2)` threads (private field set at creation).
  Changing it would trade other simulation jobs for pathfinding throughput — needs reflection; investigate and test.
* Reducing *demand*: throttling citizen behaviour (Class A) reduces new trips → fewer path requests.
* Request delay budgets are set by each requester (`GetQueue(system, maxDelayFrames, …)`) — would need Harmony;
  deferred.

## 4. What will honestly not be available

* **Per-system CPU times** (spec 12): most work runs in worker-thread jobs; the release build gives no per-system
  timing to mods. Shown as "not measurable"; use before/after comparison instead.
* **Simulation LOD by distance** (spec 8): would require changing Burst-compiled jobs. Not planned.
* **Economy throttling** (spec 7.7): excluded unless a system is proven read-only (statistics).

## 5. Build plan

1. **Core** — control registry (resolve target systems by name at runtime; missing → feature disabled + logged),
   gate system at the start of `GameSimulation`, rotating skip schedule per system, safety release, reset.
2. **Profiles / budget / adaptive** — data-driven preset table → per-category fractions; budget slider sacrifices
   low-priority categories first; adaptive controller on absolute simulation speed with hysteresis + cooldown.
3. **Monitoring** — absolute speed, steps per frame, per-step time, pathfinding headroom/backlog, limiting factor.
4. **Visual (in-game UI module, React)** — toolbar button + panel: speed gauge ("12 % of 1×"), limiting-factor badge,
   2-minute graph of speed / FPS / pathfinding backlog with stall shading, profile picker, adaptive "currently
   reducing" list, before/after comparison; toggleable compact overlay.
5. **Before/after** — capture baseline, capture optimized, side-by-side from measured data only.
6. **Test on Southwell** (chronically slow) and Startford; every category must show a measured benefit or be removed
   from the main UI (spec 21).
