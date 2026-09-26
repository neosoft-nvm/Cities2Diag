# Roadmap — Performance Detective (CS2 Stall Investigator for everyone)

Public name: **Performance Detective**. License: MIT.

Goal: any Cities: Skylines II player can find out *why* their game sputters, with evidence, and get
suggestions — alone, or by handing the data to the AI of their choice.

Principles carried over from the investigation: **measure first**, never fabricate, show the evidence behind
every suggestion, never claim causation from correlation, the tool must not cause stutter itself, and no data
leaves the player's PC unless they choose to send it.

## What the first investigation taught us

| Finding (one PC, 2 cities) | Consequence for the product |
|---|---|
| The "slow motion, FPS unchanged" stalls are invisible from outside the game | The core must be an **in-game mod** that measures simulation speed |
| The simulation had almost no CPU headroom; other programs taking 2–3 cores for seconds caused stalls | Attribute CPU use of other processes during every stall |
| A ~2 s freeze every 5 minutes disappeared when autosave was set to 30 minutes | Correlate stalls with autosave and other periodic in-game events |
| Big city allocated 25 GB on 16 GB RAM → constant paging | Report memory headroom and paging, warn clearly |
| Writing logs into a OneDrive folder cost 2–3 cores per write | Tool writes only to local, unsynced folders |
| Global hotkeys can silently fail; players need feedback | Capture key with audible/visible confirmation |

## Components

* **Mod** (C#, official CS2 modding toolchain, distributed on Paradox Mods). Runs inside the game: simulation
  speed and tick timing, frame time, game CPU/threads, memory, system CPU and other processes (Win32 APIs work
  from inside the game), NVIDIA GPU via NVML, city stats, enabled mods.
* **Companion app** (this repo's Windows monitor, optional). Adds what needs admin rights or an external view:
  PresentMon frame pipeline (CPU/GPU busy per frame), process-level views while the game is loading or frozen.
* **Report format** (open, versioned JSON + a Markdown summary) — the contract between mod, companion, and any AI.

## v1 — Basic: "measure and record" (mod)

* Verify what the current modding API exposes (simulation frame counter / speed, population, etc.) — documented,
  nothing assumed.
* Live simulation-speed measurement: actual simulation ticks per second vs. the selected speed.
* Automatic stall detection on simulation speed (the real signal), with frame time alongside.
* Capture key (configurable) with sound + on-screen confirmation.
* Per-session log and per-stall event files, written to a local folder.
* **"Copy report for AI" button**: a Markdown summary + JSON with the key numbers and a ready-made prompt,
  to paste into ChatGPT, Claude, Gemini, a local model, a forum post, or a Discord help channel.
* Options page in the game's settings menu (thresholds, capture key, folder).

## v2 — Smarter: "explain the evidence" (mod)

* In-game status panel: simulation speed, FPS, CPU headroom, memory headroom, last stall.
* Other-process attribution: which programs used CPU / caused paging during each stall.
* Periodic-pattern finder: stalls every N minutes, correlation with autosave interval, in-game events.
* Rule-based findings with evidence and confidence, e.g.
  "RAM: game uses 25 GB on a 16 GB PC — heavy paging in 14 of 17 stalls",
  "Stalls repeat every 5.0 min ± 1 s — matches your autosave interval",
  "During 9 of 11 stalls another program used > 2 cores (top: …)".
  Each finding has a *suggested experiment* (e.g. "set autosave to 30 min and compare"), not a verdict.
* Session report: stalls/hour, durations, patterns, system summary.
* Enabled-mods list recorded with every session.

## v3 — Advanced: "compare and isolate"

* Session comparison (A/B): same city, different setting/mods/background → stalls per hour, significance hints.
* **Mod bisect assistant**: guided "disable half of your mods" rounds to find a problematic mod, with the
  comparison done by the tool.
* Simulation profiling: which simulation systems take the time during a stall (only if the mod framework
  allows it safely — to be investigated).
* Companion integration: mod and Windows app share one timeline; PresentMon frame split.
* Save-game / city statistics that correlate with load (agents, vehicles, pathfinding queues — as available).

## v4 — Full suite: "assist"

* **Bring-your-own-AI analysis**: optional connection to the player's own provider (Anthropic, OpenAI, Google,
  or a local model via Ollama/LM Studio) using *their* key; the data sent is shown first. Off by default.
* Guided troubleshooting flow: symptoms → measurements → experiments → result.
* Opt-in anonymous community benchmarks: "cities of 200k pop on 8-thread CPUs usually run at X× speed".
* Shareable report page for forums/Discord.
* Localisation.

## Release/ops

* Open source (MIT), GitHub releases for the companion, Paradox Mods for the mod.
* Versioned report schema so AI prompts and community tools stay compatible.
* Every release tested on a real large city before publishing; overhead budget published with each release.
