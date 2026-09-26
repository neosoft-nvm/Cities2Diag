import { useMemo } from "react";
import { bindValue, trigger, useValue } from "cs2/api";

const GROUP = "performanceDetective";

export interface Target {
  key: string;
  name: string;
  effect: string;
  reductionPct: number;
  active: boolean;
  custom: number;
}

export interface Summary {
  profile: string;
  quality: number;
  seconds: number;
  speedPct: number | null;
  fps: number | null;
  stepMs: number | null;
  peakStepMs: number | null;
  backlog: number | null;
  pathLimitedPct: number | null;
  gameCores: number | null;
  population: number;
}

export interface DetectiveState {
  version: string;
  inSession: boolean;
  enabled: boolean;
  profile: number;
  adaptive: boolean;
  overlay: boolean;
  minQuality: number;
  targetSpeed: number;
  quality: number;
  adaptiveNote: string;
  stability: string;
  speedPct: number | null;
  selectedSpeed?: number;
  fps?: number | null;
  frameMs?: number | null;
  stepMs?: number | null;
  stepsPerFrame?: number | null;
  backlog?: number;
  headroom?: number;
  limiter?: string;
  gameCores?: number | null;
  cpuPct?: number | null;
  population?: number;
  ramFreeGb?: number | null;
  gameMemGb?: number | null;
  ramGb?: number | null;
  preference: string;
  stalls: number;
  captures: number;
  lastStall?: { seconds: number; minPct: number; agoSeconds: number; catchUp: number };
  findings?: { level: "problem" | "info" | "good"; text: string }[];
  autoTune?: {
    running: boolean; finished: boolean; current: string; block: number; blocks: number;
    blockRemaining: number; totalRemaining: number; settling: boolean; summary: string;
    candidates: { name: string; gain: number | null; clear: boolean; comparisons: number }[];
  };
  sourcesPerMin?: number;
  sources?: { name: string; perMin: number; workPct: number; successPct: number | null; tip: string | null }[];
  targets: Target[];
  unavailable: string[];
  history?: { speed: (number | null)[]; fps: (number | null)[]; backlog: (number | null)[]; quality: (number | null)[]; stall: boolean[] };
  compare?: { running: string | null; remaining: number; baseline?: Summary; optimized?: Summary };
}

const state$ = bindValue<string>(GROUP, "state", "{}");

/** Whether the panel is open. Kept on the C# side so Ctrl+Alt+P and the Options button can open it too. */
export const panelOpen$ = bindValue<boolean>(GROUP, "panelOpen", false);

export function useDetective(): DetectiveState | null {
  const raw = useValue(state$);
  return useMemo(() => {
    try {
      const s = JSON.parse(raw) as DetectiveState;
      return s && s.version ? s : null;
    } catch {
      return null;
    }
  }, [raw]);
}

export function command(cmd: string) {
  trigger(GROUP, "command", cmd);
}

export const PROFILES = ["Maximum Accuracy", "Balanced", "Performance", "Extreme", "Custom"];

export function stabilityColor(stability: string): string {
  switch (stability) {
    case "stable": return "#4fbf6a";
    case "elevated": return "#f0b429";
    case "overloaded": return "#e5484d";
    default: return "#8a94a3";
  }
}

export function speedColor(pct: number | null | undefined): string {
  if (pct == null || isNaN(pct)) return "#8a94a3";
  return pct >= 85 ? "#4fbf6a" : pct >= 50 ? "#f0b429" : "#e5484d";
}

export function fmt(v: number | null | undefined, digits = 0, suffix = ""): string {
  if (v == null || isNaN(v)) return "–";
  return v.toFixed(digits) + suffix;
}

export function limiterText(s: DetectiveState): { title: string; detail: string } {
  switch (s.limiter) {
    case "pathfinding":
      return {
        title: "Limited by pathfinding",
        detail: `The game is waiting for routes to be calculated (${(s.backlog ?? 0).toLocaleString()} requests queued).`,
      };
    case "cpu":
      return {
        title: "Limited by CPU time per step",
        detail: s.preference && s.preference !== "SimulationSpeed"
          ? `Each step takes ${fmt(s.stepMs, 1)} ms. The game keeps FPS smooth by running fewer steps (Performance Preference: ${s.preference}).`
          : `Each simulation step takes ${fmt(s.stepMs, 1)} ms — more than this speed allows.`,
      };
    case "none":
      return { title: "Running at full speed", detail: "Nothing is holding the simulation back right now." };
    default:
      return {
        title: s.stability === "loading" ? "Loading" : "Paused",
        detail: s.stability === "loading" ? "Measurements resume when the city has loaded." : "Unpause to measure the simulation.",
      };
  }
}
