import { useEffect } from "react";

/**
 * Where a draggable panel was last left, in the game's draggable-panel units: the fraction of the screen space beside
 * and below the panel (0 = left/top edge, 1 = right/bottom). The game gives no callback when a panel is dragged, so
 * the position is read back from the element after every mouse release and kept in localStorage (per panel), so the
 * panel reopens — also after a restart — where it was left.
 */
export type Position = { x: number; y: number };

const storageKey = (key: string) => `PerformanceDetective.position.${key}`;
const memory: Record<string, Position> = {};

export const loadPosition = (key: string, fallback: Position): Position => {
  if (memory[key]) return memory[key];
  try {
    const raw = window.localStorage.getItem(storageKey(key));
    if (raw) {
      const p = JSON.parse(raw);
      if (typeof p?.x === "number" && typeof p?.y === "number") return (memory[key] = clampPosition(p));
    }
  } catch {
    /* no storage: keep the default */
  }
  return fallback;
};

const clampPosition = (p: Position): Position => ({ x: Math.min(Math.max(p.x, 0), 1), y: Math.min(Math.max(p.y, 0), 1) });

const savePosition = (key: string, className: string) => {
  const el = document.querySelector(`.${className}`) as HTMLElement | null;
  if (!el) return;
  const r = el.getBoundingClientRect();
  const freeX = window.innerWidth - r.width;
  const freeY = window.innerHeight - r.height;
  if (freeX <= 0 || freeY <= 0) return;
  const p = clampPosition({ x: r.left / freeX, y: r.top / freeY });
  const old = memory[key];
  if (old && Math.abs(old.x - p.x) < 0.001 && Math.abs(old.y - p.y) < 0.001) return;
  memory[key] = p;
  try {
    window.localStorage.setItem(storageKey(key), JSON.stringify(p));
  } catch {
    /* no storage: the position is kept until the game restarts */
  }
};

/** While the panel with this class is shown, remember its position after every drag (mouse release). */
export const useRememberPosition = (key: string, className: string, shown: boolean) => {
  useEffect(() => {
    if (!shown) return;
    const onUp = () => setTimeout(() => savePosition(key, className), 0);
    window.addEventListener("mouseup", onUp);
    return () => {
      window.removeEventListener("mouseup", onUp);
      savePosition(key, className);
    };
  }, [key, className, shown]);
};
