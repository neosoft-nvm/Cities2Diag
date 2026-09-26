# CS2 Stall Investigator

Diagnostic tooling for a specific Cities: Skylines II problem in large cities: the simulation periodically
slows to "slow motion" for ~5–7 seconds while FPS stays roughly the same, then suddenly catches up.

**Measure first. Analyze second. Optimize third.** This is not an FPS booster.

## Status

| Phase | Scope | State |
|---|---|---|
| 1 | Windows companion monitor: process detection, CPU, RAM, GPU/VRAM, logging, basic UI | **done — needs testing against a real stalling city** |
| 2 | Rolling buffer event recorder, automatic stall detection, event files, event viewer | not started |
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
session log each time the game starts.

**When you see a slowdown, press `Ctrl+Alt+M`** (works while the game has focus; you'll hear a chime).
This writes a marker into the log so the moment can be found later. Phase 2 will turn this into full
event capture.

## Output

```
Documents\CS2StallInvestigator\
  settings.json                        editable settings (created on first run)
  Sessions\Session_20260926_201500\
    samples.csv                        one row per sample (default 5 per second)
    session.json                       system info, game version, settings, markers
```

An empty CSV cell means *not measured*, never zero.

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
| Monitor itself | actual interval between samples, collection cost | `Stopwatch` |

`interval_ms` matters: if the monitor itself is delayed (e.g. by a system-wide hitch), the gap shows up there.

### Accuracy notes

* **Per-thread CPU %** is quantised: Windows updates thread CPU time in ~15.6 ms steps, so at 5 samples/s
  a single reading can be off by about ±8 % and occasionally exceed 100 %. Trends across samples are reliable.
* **Main thread** = the process's oldest thread, which for a Unity game is the main thread.
* **NVML temperature, clocks, power and throttle reasons** are refreshed once per second (each call blocks
  in the driver for 1–3 ms); load and VRAM are read every sample.
* NVML GPU load is averaged by the driver over its own sampling window (1/6 s – 1 s depending on GPU).

### Not measured yet

* **FPS / frame time** — planned for Phase 2 using PresentMon (ETW), the standard tool for this.
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
| `LogDirectory` | `Documents\CS2StallInvestigator\Sessions` | |
| `EnableGpuEngineCounters` | `true` | per-process GPU % and VRAM |
| `EnableNvml` | `true` | NVIDIA hardware values |
| `EnableThreadSampling` | `true` | main / busiest thread CPU |
| `ThreadListRefreshMs` | `2000` | how often new game threads are picked up |
| `UiRefreshMs` | `500` | |
| `HistorySeconds` | `120` | in-memory history (chart now, event capture later) |

## Testing without the game

```
Cs2Monitor.exe --process explorer --logdir C:\temp\cs2test --headless 10
```

`--process` attaches to any process, `--headless N` samples for N seconds without a window,
`--gpu-engine 0` / `--threads 0` disable those collectors. None of these change `settings.json`.
