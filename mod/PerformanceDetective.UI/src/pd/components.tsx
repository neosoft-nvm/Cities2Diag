import { FloatingButton, Panel, Button, Scrollable } from "cs2/ui";
import { useValue } from "cs2/api";
import classNames from "classnames";
import icon from "./icon.svg";
import styles from "./detective.module.scss";
import {
  DetectiveState, Summary, Target, PROFILES, command, fmt, limiterText, panelOpen$, speedColor, stabilityColor, useDetective,
} from "./state";

// ------------------------------------------------------------------ toolbar button

export const ToolbarButton = () => {
  const s = useDetective();
  const open = useValue(panelOpen$);
  const color = s ? stabilityColor(s.stability) : "#8a94a3";
  const tip = s && s.speedPct != null
    ? `Performance Detective — simulation at ${fmt(s.speedPct)}% of selected speed (Ctrl+Alt+P)`
    : "Performance Detective (Ctrl+Alt+P)";
  return (
    <div className={styles.toolbarWrap}>
      <FloatingButton src={icon} selected={open} tooltipLabel={tip} onSelect={() => command("togglePanel")} />
      <div className={styles.statusDot} style={{ backgroundColor: color }} />
    </div>
  );
};

// ------------------------------------------------------------------ overlay

export const Overlay = () => {
  const s = useDetective();
  if (!s || !s.overlay || !s.inSession) return null;
  const color = stabilityColor(s.stability);
  const lim = limiterText(s);
  return (
    <div className={styles.overlay} style={{ borderLeftColor: color }}>
      <div className={styles.overlayTitle}>Simulation performance</div>
      <div className={styles.overlayBig}>
        <div className={styles.overlayNumber} style={{ color: speedColor(s.speedPct) }}>{fmt(s.speedPct)}%</div>
        <div className={styles.heroLabel}>of {fmt(s.selectedSpeed, 0)}× · {s.stability}</div>
      </div>
      <div>FPS {fmt(s.fps)} · {fmt(s.stepMs, 1)} ms/step</div>
      <div className={styles.overlayLine}>{lim.title}</div>
      <div className={styles.overlayLine}>
        Quality {s.quality >= 0 ? fmt(s.quality) + "%" : "custom"} · Adaptive {s.adaptive ? "ON" : "OFF"}
      </div>
    </div>
  );
};

// ------------------------------------------------------------------ panel

export const DetectivePanel = () => {
  const open = useValue(panelOpen$);
  const s = useDetective();
  if (!open) return null;
  return (
    <Panel
      className={styles.panel}
      header={<span>Performance Detective</span>}
      onClose={() => command("panel:0")}
    >
      <Scrollable style={{ maxHeight: "78vh" }}>
        <div className={styles.content}>
          {!s || !s.inSession ? (
            <div className={styles.note}>Load a city to start measuring.</div>
          ) : (
            <>
              <FindingsSection s={s} />
              <AutoTuneSection s={s} />
              <SpeedSection s={s} />
              <CpuBreakdownSection s={s} />
              <AssetPacksSection s={s} />
              <SourcesSection s={s} />
              <GraphSection s={s} />
              <MetricsSection s={s} />
              <ControllerSection s={s} />
              <CompareSection s={s} />
              <ActionsSection s={s} />
            </>
          )}
        </div>
      </Scrollable>
    </Panel>
  );
};

const levelColor = (level: string) => level === "problem" ? "#e5484d" : level === "good" ? "#4fbf6a" : "#8a94a3";

const FindingsSection = ({ s }: { s: DetectiveState }) => {
  const list = s.findings ?? [];
  if (list.length === 0) return null;
  return (
    <div style={{ marginBottom: "10rem" }}>
      <div className={styles.sectionTitle}>What the detective found</div>
      {list.map((f, i) => (
        <div key={i} className={styles.finding} style={{ borderLeftColor: levelColor(f.level) }}>{f.text}</div>
      ))}
    </div>
  );
};

const mmss = (sec: number) => `${Math.floor(sec / 60)}:${String(Math.floor(sec % 60)).padStart(2, "0")}`;

const AutoTuneSection = ({ s }: { s: DetectiveState }) => {
  const t = s.autoTune;
  if (!t) return null;
  if (t.running) {
    const done = (t.block - 1) / Math.max(1, t.blocks);
    return (
      <div className={styles.tuneCard}>
        <div className={classNames(styles.row, styles.spaceBetween)}>
          <b>Auto-Tune running</b>
          <span className={styles.metricLabel}>{t.mayStopEarly ? "at most" : "about"} {mmss(t.totalRemaining)} left</span>
        </div>
        <div className={styles.qualityBar}><div className={styles.qualityFill} style={{ width: `${Math.round(done * 100)}%` }} /></div>
        <div className={styles.note}>
          Step {t.block} of {t.blocks}: <b>{t.current}</b> — {t.settling ? "letting the city settle" : "measuring"} ({mmss(t.blockRemaining)}).
          Keep the game running at this speed; pausing just pauses the test.
        </div>
        <Button className={styles.chip} onSelect={() => command("cancelAutoTune")}>Stop and restore my settings</Button>
      </div>
    );
  }
  return (
    <div className={styles.tuneCard}>
      <b>Auto-Tune</b>
      <div className={styles.note} style={{ marginTop: "2rem" }}>
        Tests options aimed at the pathfinding bottleneck (more pathfinding threads, fewer taxi and home searches), alternating
        with the normal game so rush hours don't skew the result. The quick check reports what looks promising; the thorough
        test keeps only what clearly helps and stops early when the first round already gives a clear answer. You just
        leave the city running. Once it has run for a city, you only need it again after the city has grown a lot.
      </div>
      {t.advice && <div className={styles.finding} style={{ borderLeftColor: "#4aa3ff" }}>{t.advice}</div>}
      {t.finished && t.summary &&<div className={styles.finding} style={{ borderLeftColor: "#4aa3ff" }}>{t.summary}</div>}
      {t.finished && t.candidates.map(c => (
        <div key={c.name} className={styles.targetHead}>
          <span>{c.name}</span>
          <span className={c.clear ? styles.better : c.promising ? styles.promising : styles.metricLabel}>
            {c.gain == null ? "–" : `${c.gain >= 0 ? "+" : ""}${c.gain.toFixed(1)} points`}{c.clear ? " ✓ kept" : c.promising ? " – promising" : ""}
          </span>
        </div>
      ))}
      <div className={styles.row} style={{ marginTop: "6rem" }}>
        <Button className={styles.button} onSelect={() => command("autoTune:quick")}>Quick check (≈20 min)</Button>
        <Button className={styles.button} onSelect={() => command("autoTune:thorough")}>Thorough — keeps what helps (≈20–38 min)</Button>
      </div>
    </div>
  );
};

const CpuBreakdownSection = ({ s }: { s: DetectiveState }) => {
  const b = s.cpuBreakdown;
  if (!b) return null;
  const tuneRunning = !!s.autoTune?.running;
  if (b.running) {
    const done = 1 - b.remaining / Math.max(1, b.duration);
    return (
      <div className={styles.tuneCard}>
        <div className={classNames(styles.row, styles.spaceBetween)}>
          <b>CPU breakdown running</b>
          <span className={styles.metricLabel}>about {mmss(b.remaining)} left</span>
        </div>
        <div className={styles.qualityBar}><div className={styles.qualityFill} style={{ width: `${Math.round(done * 100)}%` }} /></div>
        <div className={styles.note}>
          Timing every simulation system. The city runs slower during this minute; that is expected and ends with the test.
        </div>
        <Button className={styles.chip} onSelect={() => command("cancelCpuBreakdown")}>Stop</Button>
      </div>
    );
  }
  return (
    <div className={styles.tuneCard}>
      <b>CPU breakdown</b>
      <div className={styles.note} style={{ marginTop: "2rem" }}>
        Shows which parts of the simulation use the most CPU time per step. Takes one minute of normal play; the city runs
        slower during that minute. Pathfinding runs separately and is not included.
      </div>
      {b.error && <div className={styles.finding} style={{ borderLeftColor: "#e5484d" }}>{b.error}</div>}
      {b.finished && (
        <div className={styles.note} style={{ marginTop: "4rem" }}>
          {b.steps.toLocaleString()} steps measured, {fmt(b.stepMs, 1)} ms per step while timing.
        </div>
      )}
      {b.finished && b.systems.map(x => (
        <div key={x.name} className={styles.targetHead}>
          <span>{x.name}</span>
          <span className={styles.metricLabel}>{fmt(x.msPerStep, 2)} ms · {fmt(x.sharePct, 1)}%</span>
        </div>
      ))}
      <div className={styles.row} style={{ marginTop: "6rem" }}>
        {tuneRunning
          ? <span className={styles.metricLabel}>Available when Auto-Tune has finished.</span>
          : <Button className={styles.button} onSelect={() => command("cpuBreakdown")}>{b.finished ? "Run again (1 min)" : "Run CPU breakdown (1 min)"}</Button>}
      </div>
    </div>
  );
};

const AssetPacksSection = ({ s }: { s: DetectiveState }) => {
  const a = s.assetPacks;
  if (!a) return null;
  return (
    <div className={styles.tuneCard}>
      <b>Unused asset packs</b>
      <div className={styles.note} style={{ marginTop: "2rem" }}>
        Lists subscribed asset packs with none of their buildings, props, roads or decals placed in this city. Each loaded
        asset adds to the game's start-up time and memory use. Disabling a pack only affects future games, so check that your
        other cities don't use it. Takes a few seconds; nothing is changed.
      </div>
      {a.error && <div className={styles.finding} style={{ borderLeftColor: "#e5484d" }}>{a.error}</div>}
      {a.finished && (
        <div className={styles.note} style={{ marginTop: "4rem" }}>
          {a.unused} of {a.packs} asset packs are not placed in this city ({a.unusedPrefabs.toLocaleString()} assets).
          {a.list.length < a.unused ? ` Largest ${a.list.length} shown; ` : " "}the full list is in the session folder (asset_packs_*.csv).
        </div>
      )}
      {a.finished && a.list.map(p => (
        <div key={p.id + p.name} className={styles.targetHead}>
          <span>{p.name}</span>
          <span className={styles.metricLabel}>{p.prefabs.toLocaleString()} assets{p.id ? ` · ${p.id}` : ""}</span>
        </div>
      ))}
      <div className={styles.row} style={{ marginTop: "6rem" }}>
        <Button className={styles.button} onSelect={() => command("scanAssets")}>{a.finished ? "Scan again" : "Find unused asset packs"}</Button>
      </div>
    </div>
  );
};

const SpeedSection = ({ s }: { s: DetectiveState }) => {
  const pct = s.speedPct ?? 0;
  const color = speedColor(s.speedPct);
  const lim = limiterText(s);
  return (
    <div>
      <div className={styles.hero}>
        <div className={styles.heroNumber} style={{ color }}>{s.speedPct == null ? "–" : `${fmt(pct)}%`}</div>
        <div className={styles.heroLabel}>
          simulation speed<br />of the {fmt(s.selectedSpeed, 0)}× you selected
        </div>
      </div>
      <div className={styles.track}>
        <div className={styles.trackFill} style={{ width: `${Math.min(100, Math.max(0, pct))}%`, backgroundColor: color }} />
        {s.adaptive && <div className={styles.trackMarker} style={{ left: `${s.targetSpeed}%` }} />}
      </div>
      <div className={styles.limiterRow}>
        <div className={styles.pill} style={{ backgroundColor: stabilityColor(s.stability) }}>{s.stability}</div>
        <div className={styles.limiterTitle}>{lim.title}</div>
      </div>
      <div className={styles.limiterDetail}>{lim.detail}</div>
      {s.lastStall && (
        <div className={styles.note}>
          Last slow-motion stall: {fmt(s.lastStall.seconds, 1)} s at {fmt(s.lastStall.minPct)}% of normal,
          {" "}{fmt(s.lastStall.agoSeconds)} s ago{s.lastStall.catchUp > 0 ? `, then ${fmt(s.lastStall.catchUp, 1)} s of catch-up` : ""}.
          {" "}Stalls this session: {s.stalls}.
        </div>
      )}
    </div>
  );
};

const SourcesSection = ({ s }: { s: DetectiveState }) => {
  const list = (s.sources ?? []).filter(x => x.workPct > 0.5 || x.perMin > 0);
  if (list.length === 0) return null;
  const top = list[0];
  return (
    <div className={styles.section}>
      <div className={styles.sectionTitle}>Where pathfinding work comes from (last minute)</div>
      <div className={styles.note} style={{ marginTop: 0, marginBottom: "4rem" }}>
        {fmt(s.sourcesPerMin)} route searches per minute. Bars show each source's share of the search work.
      </div>
      {list.slice(0, 6).map(x => (
        <div key={x.name} className={styles.target}>
          <div className={styles.targetHead}>
            <span>{x.name}</span>
            <span className={styles.metricLabel}>
              {fmt(x.workPct)}% · {fmt(x.perMin)}/min{x.successPct != null && x.successPct < 90 ? ` · ${fmt(x.successPct)}% found a route` : ""}
            </span>
          </div>
          <div className={styles.targetBar}>
            <div className={styles.targetFill} style={{ width: `${Math.min(100, x.workPct)}%`, backgroundColor: "#4aa3ff" }} />
          </div>
        </div>
      ))}
      {top.tip && top.workPct >= 15 && (
        <div className={styles.note}><b>Community tip for "{top.name}":</b> {top.tip}</div>
      )}
    </div>
  );
};

const GraphSection = ({ s }: { s: DetectiveState }) => {
  const h = s.history;
  if (!h || h.speed.length === 0) return null;
  const maxBacklog = Math.max(1, ...h.backlog.map(v => v ?? 0));
  return (
    <div className={styles.section}>
      <div className={styles.sectionTitle}>Last {h.speed.length} seconds — simulation speed</div>
      <div className={styles.graph}>
        {h.speed.map((v, i) => (
          <div
            key={i}
            className={classNames(styles.bar, h.stall[i] && styles.barStall)}
            style={{
              height: `${Math.min(100, Math.max(1, v ?? 0))}%`,
              backgroundColor: v == null ? "transparent" : speedColor(v),
            }}
          />
        ))}
      </div>
      <div className={classNames(styles.graph, styles.graphSmall)}>
        {h.backlog.map((v, i) => (
          <div key={i} className={styles.bar} style={{ height: `${Math.max(2, ((v ?? 0) / maxBacklog) * 100)}%`, backgroundColor: "#4aa3ff" }} />
        ))}
      </div>
      <div className={styles.graphLegend}>
        <span>Speed (green ≥ 85%, amber, red &lt; 50%) · red shade = stall</span>
        <span>blue = pathfinding queue (max {maxBacklog.toLocaleString()})</span>
      </div>
    </div>
  );
};

const Metric = ({ label, value }: { label: string; value: string }) => (
  <div className={styles.metric}>
    <span className={styles.metricLabel}>{label}</span>
    <span className={styles.metricValue}>{value}</span>
  </div>
);

const MetricsSection = ({ s }: { s: DetectiveState }) => (
  <div className={styles.section}>
    <div className={styles.sectionTitle}>Live measurements</div>
    <div className={styles.metrics}>
      <Metric label="FPS" value={fmt(s.fps)} />
      <Metric label="Frame time" value={fmt(s.frameMs, 1, " ms")} />
      <Metric label="CPU per sim step" value={fmt(s.stepMs, 1, " ms")} />
      <Metric label="Sim steps / frame" value={fmt(s.stepsPerFrame, 2)} />
      <Metric label="Pathfinding queue" value={s.backlog != null && s.backlog >= 0 ? s.backlog.toLocaleString() : "–"} />
      <Metric label="Game CPU" value={fmt(s.gameCores, 1, " threads")} />
      <Metric label="Whole-PC CPU" value={fmt(s.cpuPct, 0, "%")} />
      <Metric label="Population" value={s.population != null && s.population >= 0 ? s.population.toLocaleString() : "–"} />
      <Metric label="Game memory" value={`${fmt(s.gameMemGb, 1)} / ${fmt(s.ramGb, 1)} GB RAM`} />
      <Metric label="Free RAM" value={fmt(s.ramFreeGb, 1, " GB")} />
    </div>
    {s.gameMemGb != null && s.ramGb != null && s.gameMemGb > s.ramGb * 0.85 && (
      <div className={styles.warning}>
        The game uses {fmt(s.gameMemGb, 1)} GB on a PC with {fmt(s.ramGb, 1)} GB RAM — Windows must page to disk. No simulation setting can change this.
      </div>
    )}
  </div>
);

const Stepper = ({ value, suffix, onDown, onUp }: { value: number; suffix: string; onDown: () => void; onUp: () => void }) => (
  <div className={styles.stepper}>
    <Button className={styles.stepButton} onSelect={onDown}>−</Button>
    <div className={styles.stepValue}>{value}{suffix}</div>
    <Button className={styles.stepButton} onSelect={onUp}>+</Button>
  </div>
);

const ControllerSection = ({ s }: { s: DetectiveState }) => {
  const custom = s.profile === 4;
  return (
    <div className={styles.section}>
      <div className={classNames(styles.row, styles.spaceBetween)}>
        <div className={styles.sectionTitle}>Simulation controller</div>
        <Button className={classNames(styles.chip, s.enabled && styles.chipSelected)} onSelect={() => command(`enabled:${s.enabled ? 0 : 1}`)}>
          {s.enabled ? "ON" : "OFF"}
        </Button>
      </div>
      <div className={styles.row}>
        {PROFILES.map((name, i) => (
          <Button
            key={name}
            className={classNames(styles.chip, s.profile === i && styles.chipSelected, i === 3 && styles.chipWarn)}
            onSelect={() => command(`profile:${i}`)}
          >
            {name}
          </Button>
        ))}
      </div>
      {s.profile === 3 && <div className={styles.warning}>Extreme Performance significantly reduces simulation fidelity.</div>}
      {s.profile === 0 && !s.adaptive && <div className={styles.note}>Maximum Accuracy: the game runs exactly as normal.</div>}

      {!custom && (
        <>
          <div className={classNames(styles.row, styles.spaceBetween)} style={{ marginTop: "8rem" }}>
            <span>Adaptive mode</span>
            <Button className={classNames(styles.chip, s.adaptive && styles.chipSelected)} onSelect={() => command(`adaptive:${s.adaptive ? 0 : 1}`)}>
              {s.adaptive ? "ON" : "OFF"}
            </Button>
          </div>
          {s.adaptive && (
            <>
              <div className={classNames(styles.row, styles.spaceBetween)}>
                <span className={styles.metricLabel}>Target speed</span>
                <Stepper value={s.targetSpeed} suffix="%" onDown={() => command(`targetSpeed:${s.targetSpeed - 5}`)} onUp={() => command(`targetSpeed:${s.targetSpeed + 5}`)} />
              </div>
              <div className={classNames(styles.row, styles.spaceBetween)} style={{ marginTop: "4rem" }}>
                <span className={styles.metricLabel}>Minimum quality (budget)</span>
                <Stepper value={s.minQuality} suffix="%" onDown={() => command(`minQuality:${s.minQuality - 10}`)} onUp={() => command(`minQuality:${s.minQuality + 10}`)} />
              </div>
              {s.adaptiveNote && <div className={styles.note}>{s.adaptiveNote}</div>}
            </>
          )}
          <div className={classNames(styles.row, styles.spaceBetween)} style={{ marginTop: "8rem" }}>
            <span>Simulation quality</span>
            <span className={styles.metricValue}>{fmt(s.quality)}%</span>
          </div>
          <div className={styles.qualityBar}><div className={styles.qualityFill} style={{ width: `${Math.max(0, s.quality)}%` }} /></div>
        </>
      )}

      {s.threads?.available && (
        <>
          <div className={classNames(styles.row, styles.spaceBetween)} style={{ marginTop: "10rem" }}>
            <span>Pathfinding threads</span>
            <Stepper value={s.threads.current} suffix={` of ${s.threads.workers}`}
              onDown={() => command(`threads:${s.threads!.extra - 1}`)} onUp={() => command(`threads:${s.threads!.extra + 1}`)} />
          </div>
          <div className={styles.note}>
            Game default: {s.threads.default}. More threads calculate routes faster when the CPU has spare capacity; they don't reduce simulation detail.
          </div>
        </>
      )}
      <div className={styles.sectionTitle} style={{ marginTop: "10rem" }}>{custom ? "Custom: fewer updates per part" : "Currently reduced"}</div>
      {s.targets.map(t => <TargetRow key={t.key} t={t} custom={custom} />)}
      {s.unavailable.length > 0 && <div className={styles.warning}>Not available in this game version: {s.unavailable.join(", ")}</div>}
      <div className={styles.note}>
        Reduced parts are updated less often, never switched off. Changes apply immediately and are fully undone by Maximum Accuracy or OFF.
      </div>
    </div>
  );
};

const TargetRow = ({ t, custom }: { t: Target; custom: boolean }) => (
  <div className={styles.target}>
    <div className={styles.targetHead}>
      <span>{t.name}</span>
      {custom ? (
        <Stepper value={t.custom} suffix="%" onDown={() => command(`custom:${t.key}=${t.custom - 25}`)} onUp={() => command(`custom:${t.key}=${t.custom + 25}`)} />
      ) : (
        <span className={styles.metricLabel}>{t.reductionPct > 0 ? `${fmt(t.reductionPct)}% fewer updates` : "normal"}</span>
      )}
    </div>
    <div className={styles.targetBar}><div className={styles.targetFill} style={{ width: `${custom ? t.custom : t.reductionPct}%` }} /></div>
    {(custom ? t.custom : t.reductionPct) > 0 && <div className={styles.note}>{t.effect}</div>}
  </div>
);

type Better = "higher" | "lower";

const CompareRow = ({ label, b, o, digits, better, suffix = "" }:
  { label: string; b?: number | null; o?: number | null; digits: number; better: Better; suffix?: string }) => {
  let delta = "";
  let cls = "";
  if (b != null && o != null && !isNaN(b) && !isNaN(o)) {
    const d = o - b;
    delta = (d > 0 ? "+" : "") + d.toFixed(digits) + suffix;
    const good = better === "higher" ? d > 0 : d < 0;
    if (Math.abs(d) > Math.pow(10, -digits)) cls = good ? styles.better : styles.worse;
  }
  return (
    <div className={styles.tr}>
      <div className={styles.c1}>{label}</div>
      <div className={styles.c2}>{fmt(b, digits, suffix)}</div>
      <div className={styles.c3}>{fmt(o, digits, suffix)}</div>
      <div className={classNames(styles.c4, cls)}>{delta}</div>
    </div>
  );
};

const CompareSection = ({ s }: { s: DetectiveState }) => {
  const c = s.compare;
  const b: Summary | undefined = c?.baseline;
  const o: Summary | undefined = c?.optimized;
  return (
    <div className={styles.section}>
      <div className={styles.sectionTitle}>Before / after comparison</div>
      {c?.running ? (
        <div className={styles.note}>Measuring {c.running}… {fmt(c.remaining)} s of running simulation left. Keep the camera and speed the same.</div>
      ) : (
        <div className={styles.row}>
          <Button className={styles.button} onSelect={() => command("baseline")}>1. Measure baseline (60 s)</Button>
          <Button className={styles.button} onSelect={() => command("optimized")}>2. Measure with current settings</Button>
          {(b || o) && <Button className={styles.chip} onSelect={() => command("resetCompare")}>Clear</Button>}
        </div>
      )}
      {(b || o) && (
        <div className={styles.table}>
          <div className={classNames(styles.tr, styles.th)}>
            <div className={styles.c1}></div>
            <div className={styles.c2}>Baseline</div>
            <div className={styles.c3}>Current</div>
            <div className={styles.c4}>Change</div>
          </div>
          <div className={classNames(styles.tr, styles.th)}>
            <div className={styles.c1}>Settings</div>
            <div className={styles.c2}>{b?.profile ?? "–"}</div>
            <div className={styles.c3}>{o?.profile ?? "–"}</div>
            <div className={styles.c4}></div>
          </div>
          <CompareRow label="Simulation speed" b={b?.speedPct} o={o?.speedPct} digits={1} better="higher" suffix="%" />
          <CompareRow label="FPS" b={b?.fps} o={o?.fps} digits={1} better="higher" />
          <CompareRow label="CPU per sim step" b={b?.stepMs} o={o?.stepMs} digits={2} better="lower" suffix=" ms" />
          <CompareRow label="Worst step" b={b?.peakStepMs} o={o?.peakStepMs} digits={1} better="lower" suffix=" ms" />
          <CompareRow label="Pathfinding queue" b={b?.backlog} o={o?.backlog} digits={0} better="lower" />
          <CompareRow label="Time held by pathfinding" b={b?.pathLimitedPct} o={o?.pathLimitedPct} digits={0} better="lower" suffix="%" />
          <CompareRow label="Game CPU (threads)" b={b?.gameCores} o={o?.gameCores} digits={2} better="lower" />
          <div className={styles.note}>Measured values only, nothing estimated. Differences of a few percent can be normal variation — compare with the same camera view and speed.</div>
        </div>
      )}
    </div>
  );
};

const ActionsSection = ({ s }: { s: DetectiveState }) => (
  <div className={styles.section}>
    <div className={styles.row}>
      <Button className={styles.button} onSelect={() => command("capture")}>Capture this moment</Button>
      <Button className={styles.button} onSelect={() => command("copyReport")}>Copy report for AI</Button>
      <Button className={styles.button} onSelect={() => command("openFolder")}>Open log folder</Button>
      <Button className={classNames(styles.chip, s.overlay && styles.chipSelected)} onSelect={() => command(`overlay:${s.overlay ? 0 : 1}`)}>
        Overlay {s.overlay ? "ON" : "OFF"}
      </Button>
    </div>
    <div className={styles.footer}>
      <span>Captures: {s.captures} · Ctrl+Alt+M in game</span>
      <span>v{s.version}</span>
    </div>
  </div>
);
