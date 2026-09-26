# CS2 Stall Investigator

Diagnostic tooling for a specific Cities: Skylines II problem in large cities: the simulation periodically
slows to "slow motion" for ~5–7 seconds while FPS stays roughly the same, then suddenly catches up.

**Measure first. Analyze second. Optimize third.** This is not an FPS booster.

## Status

| Phase | Scope | State |
|---|---|---|
| 1 | Windows companion monitor: process detection, CPU, RAM, GPU/VRAM, logging, basic UI | **done — needs testing against a real stalling city** |
| 2 | Frame timing (PresentMon), rolling buffer, automatic stall detection, manual capture, event files, event viewer | **implemented — detector tested with a synthetic load; needs tuning against the real city** |
| 3 | CS2 diagnostic mod (simulation data from inside the game) | not started |
| 4 | Correlation of game + Windows + hardware + frame-time data | not started |
| 5 | Classification / pattern analysis | not started |

## Build & run

Requires the .NET 8 SDK (Windows).

```
dotnet build -c Release
src\Cs2Monitor\bin\Release\net8.0-windows\Cs2Monitor.exe
```

In VS Code: `Terminal → Run Build Task` (Ctrl+Shift+B), or F5 with the C# extension installed.

Start the monitor before or after the game. It attaches to `Cities2.exe` automatically and starts a new
session log each time the game starts. For frame timing, get PresentMon (see [tools/README.md](tools/README.md));
when the game starts you get one UAC prompt for it (none if the monitor itself runs as administrator).

**When you see a slowdown, press `Ctrl+Alt+M`** — *Capture event* (works while the game has focus; you'll hear a chime).
If the detector is already recording a stall, this marks it as confirmed by you; otherwise it saves a manual event
around that moment.

## Output

```
%LOCALAPPDATA%\CS2StallInvestigator\
  settings.json                        editable settings (created on first run)
  Sessions\Session_20260926_201500\
    samples.csv                        one row per sample (default 5 per second)
    frames.csv                         raw per-frame data from PresentMon
    session.json                       system info, game version, settings, markers, event list
    Events\
      CS2_Event_00017.json             summary, observations, rules fired, full timeline
      CS2_Event_00017.csv              every sample of the event window, for spreadsheets
```

An empty CSV cell means *not measured*, never zero. Event ids are unique across all sessions.

## Stall detection

Each sample is finalised ~2.5 s after it is taken (`FrameSettleMs`), once PresentMon has delivered that
moment's frames; the detector then runs on it:

```
NORMAL ──any trigger rule fires──▶ WARNING ──≥ 2 s, ≥ 70 % of samples abnormal──▶ STALL
   ▲                                  │ quiet 1 s                                   │ quiet 1 s
   └──────────── NORMAL ◀── 20 s ── RECOVERY ◀─────────────────────────────────────┘
```

A rule compares one metric with its **baseline** — the median over the last 60 s of normal samples — so
"normal" adapts to your city. Default rules (all editable in `settings.json` → `Detection.Rules`):

| Rule | Condition | Role |
|---|---|---|
| GPU load drop | GPU load ≤ baseline − 30 points | trigger |
| Frame time rise | frame time ≥ 1.5 × baseline and ≥ baseline + 5 ms | trigger |
| Game main thread saturated | main thread ≥ 95 % and ≥ baseline + 15 | trigger |
| Game CPU rise | game CPU ≥ baseline + 1 core | context |
| Game not responding | Windows reports the window hung | trigger |
| Monitor itself delayed | sample interval ≥ 2 × baseline (system-wide hitch) | trigger |
| Hard page faults | ≥ 1000 pages/s | context |
| Other processes CPU rise | non-game processes ≥ baseline + 1 core | context |

*Context* rules are recorded but never start or extend a stall. Paging comes in bursts on a busy PC, and game CPU
looks like it "rises" after any pause, so as triggers both produced false or overlong stalls in the first real
sessions. Metric keys usable in rules are listed in `src/Cs2Monitor/Metrics.cs`. Settings files from older versions
are migrated automatically (`SettingsVersion`).

These defaults are a starting point, not knowledge about CS2: **the first real sessions are for tuning them.**
Each event records which rules fired, so false positives are easy to see.

Important limit: the monitor cannot see simulation speed from outside the game. If the slowdown leaves every
Windows-side measurement unchanged, the detector will miss it — use Capture, and see Phase 3.

Events are labelled `UNKNOWN / INSUFFICIENT DATA` until automatic classification exists (Phase 5); they list
measured before/during/after values and notable changes ("observations") without drawing conclusions.
For manual captures, *during* is 5 s before to 1 s after the key press.

## What is measured, and where it comes from

Every value comes from a Windows or NVIDIA API; nothing is estimated.

| Area | Values | Source |
|---|---|---|
| Game process | CPU time (as cores busy and % of PC), working set, private bytes, page faults/s, disk I/O, "not responding" | `GetProcessTimes`, `GetProcessMemoryInfo`, `GetProcessIoCounters`, `IsHungAppWindow` |
| Game threads | main thread %, busiest thread % + name, 2nd busiest, threads ≥ 90 % | `NtQuerySystemInformation` (thread list, every 2 s), `GetThreadTimes`, `GetThreadDescription` |
| Game GPU | 3D engine %, dedicated/shared VRAM | Windows `GPU Engine` / `GPU Process Memory` counters (same as Task Manager) |
| CPU | busy %, utility % (Task Manager value), effective clock, busy % per logical processor | PDH `Processor Information` |
| Memory | RAM used/total, memory load, commit used/limit, pagefile %, hard-fault pages in/s, page faults/s | `GlobalMemoryStatusEx`, PDH `Memory`, `Paging File` |
| Disk | read/write MB/s, queue length | PDH `PhysicalDisk(_Total)` |
| NVIDIA GPU (each) | load %, memory-controller load %, VRAM used, temperature, core/memory clock, power, clock-limit reasons | NVML (`nvml.dll`, ships with the driver) |
| Other processes (1/s) | total CPU of everything except the game; top 3 by CPU and by hard faults; game hard faults | `NtQuerySystemInformation` |
| Monitor itself | actual interval between samples, collection cost | `Stopwatch` |

`interval_ms` matters: if the monitor itself is delayed (e.g. by a system-wide hitch), the gap shows up there.

### Accuracy notes

* **Per-thread CPU %** is quantised: Windows updates thread CPU time in ~15.6 ms steps, so at 5 samples/s
  a single reading can be off by about ±8 % and occasionally exceed 100 %. Trends across samples are reliable.
* **Main thread** = the process's oldest thread, which for a Unity game is the main thread.
* **NVML temperature, clocks, power and throttle reasons** are refreshed once per second (each call blocks
  in the driver for 1–3 ms); load and VRAM are read every sample.
* NVML GPU load is averaged by the driver over its own sampling window (1/6 s – 1 s depending on GPU).

### Frame timing

From PresentMon, per sample: frames, FPS, average and maximum frame time (`MsBetweenPresents`), and the average
CPU-busy / GPU-busy time per frame (`MsCPUBusy`, `MsGPUBusy`) — the split that separates CPU-side from GPU-side
frame cost. PresentMon writes into a named pipe owned by the monitor (it locks output files while running), and
its frame timestamps use the same QueryPerformanceCounter clock as the samples, so no alignment guessing is involved.
If the monitor is closed while the game runs, an elevated PresentMon keeps running until the game exits.

### Not measured yet

* **Population, simulation speed, agents, etc.** — only available from inside the game: Phase 3 mod.

## Monitor overhead

Measured on an i7-6700K with two NVIDIA GPUs at 5 samples/s: median collection time 6.6 ms per sample
(mostly waiting on the GPU driver), 95th percentile 33 ms, about 5–8 % of one logical core in total.
The sampler runs on its own above-normal-priority thread so it keeps sampling on time even when the game
saturates the CPU. To reduce overhead further, set `EnableNvml` or `EnableGpuEngineCounters` to `false`, or
raise `SampleIntervalMs`.

## Settings (`settings.json`)

| Key | Default | Meaning |
|---|---|---|
| `ProcessName` | `Cities2` | process to attach to |
| `SampleIntervalMs` | `200` | 5 samples/s (50–5000) |
| `LogDirectory` | `%LOCALAPPDATA%\CS2StallInvestigator\Sessions` | keep it out of OneDrive: syncing every write costs 2–3 cores for seconds |
| `CaptureHotkey` | `Ctrl+Alt+M` | e.g. `Shift+F9`, `Pause`; uses a low-level keyboard hook, the key still reaches the game |
| `CaptureSound` / `CaptureToast` | `true` | two-tone beep / small top-right notice on each capture |
| `EnableGpuEngineCounters` | `true` | per-process GPU % and VRAM |
| `EnableNvml` | `true` | NVIDIA hardware values |
| `EnableThreadSampling` | `true` | main / busiest thread CPU |
| `ThreadListRefreshMs` | `2000` | how often new game threads are picked up |
| `UiRefreshMs` | `500` | |
| `HistorySeconds` | `120` | span of the live chart |
| `FrameTimingEnabled` | `true` | use PresentMon |
| `PresentMonPath` | *(empty)* | empty = find `tools\PresentMon*.exe` |
| `PresentMonAllowUacPrompt` | `true` | when not running as admin, ask via UAC when the game starts |
| `FrameSettleMs` | `2500` | delay before a sample is finalised (frames arrive late) |
| `Detection` | | baseline/duration thresholds, pre/post seconds (20/20), `Rules` — see above |

## Testing without the game

```
Cs2Monitor.exe --process explorer --logdir C:\temp\cs2test --headless 10
```

* `--settings FILE` use a separate settings file (read-only)
* `--process NAME` attach to any process; `--logdir DIR` log elsewhere
* `--headless N` sample for N seconds without a window; `--capture-at SEC` simulate a capture key press
* `--gpu-engine 0`, `--threads 0`, `--frames 0` disable those collectors

None of these change `settings.json`.
