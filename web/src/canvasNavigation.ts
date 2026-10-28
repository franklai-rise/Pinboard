export type CanvasViewport = {
  scrollX: number;
  scrollY: number;
  zoom: { value: number };
};

export type ContentBounds = readonly [number, number, number, number];

export type TextFocusTarget = {
  x: number;
  y: number;
  width: number;
  height: number;
  angle?: number;
  text: string;
  fontSize: number;
  lineHeight?: number;
  textAlign?: string;
};

/** Find a phrase even when Excalidraw wrapped it across visual lines. */
export function textMatchRange(text: string, query: string) {
  const chars: string[] = [];
  const originalIndexes: number[] = [];
  for (let index = 0; index < text.length; index++) {
    const char = text[index];
    if (/\s/.test(char)) {
      if (!chars.length || chars.at(-1) === " ") continue;
      chars.push(" ");
      originalIndexes.push(index);
    } else {
      // Keep a map back to the displayed text for line/column positioning.
      const lower = char.toLowerCase();
      for (const lowered of lower) {
        chars.push(lowered);
        originalIndexes.push(index);
      }
    }
  }
  const normalized = chars.join("");
  const needle = query.trim().replace(/\s+/g, " ").toLowerCase();
  if (!needle) return undefined;
  let index = normalized.indexOf(needle);
  let length = needle.length;
  if (index < 0) {
    // Full-text search can return token matches rather than an exact phrase.
    const token = needle.split(" ").filter(part => part.length >= 2)
      .find(part => normalized.includes(part));
    if (!token) return undefined;
    index = normalized.indexOf(token);
    length = token.length;
  }
  return { start: originalIndexes[index], end: originalIndexes[index + length - 1] + 1 };
}

/** Center the matching visual line at readable scale, rather than fitting a tall card. */
export function textHitViewport(
  target: TextFocusTarget,
  viewport: CanvasViewport & { width: number; height: number },
  query = "",
  matchIndex?: number,
): CanvasViewport {
  const zoom = Math.min(1.25, Math.max(0.75, viewport.zoom.value || 1));
  const range = textMatchRange(target.text, query);
  const index = range?.start ?? (Number.isSafeInteger(matchIndex)
    ? Math.max(0, Math.min(target.text.length, matchIndex!)) : 0);
  const before = target.text.slice(0, index);
  const lines = target.text.split("\n");
  const row = before.split("\n").length - 1;
  const column = index - (before.lastIndexOf("\n") + 1);
  const line = lines[row] || "";
  const longest = Math.max(1, ...lines.map(value => value.length));
  const unit = Math.min(target.fontSize || 20, target.width / longest);
  const matchedLength = range ? Math.min(line.length - column, range.end - range.start) : 0;
  const lineWidth = line.length * unit;
  const alignment = target.textAlign === "right" ? target.width - lineWidth
    : target.textAlign === "center" ? (target.width - lineWidth) / 2 : 0;
  const visibleWidth = viewport.width / zoom;
  const hitX = Math.min(target.width, alignment + (column + matchedLength / 2) * unit);
  // With no query, start reading at the first line; a wide line stays left-aligned.
  const localX = query || range ? hitX : Math.min(target.width / 2, visibleWidth / 2 - 40 / zoom);
  const lineHeight = (target.fontSize || 20) * (target.lineHeight || 1.25);
  const localY = Math.min(target.height, (row + 0.5) * lineHeight);
  const angle = target.angle || 0;
  const dx = localX - target.width / 2;
  const dy = localY - target.height / 2;
  const centerX = target.x + target.width / 2 + dx * Math.cos(angle) - dy * Math.sin(angle);
  const centerY = target.y + target.height / 2 + dx * Math.sin(angle) + dy * Math.cos(angle);
  return {
    scrollX: viewport.width / (2 * zoom) - centerX,
    scrollY: viewport.height / (2 * zoom) - centerY,
    zoom: { value: zoom },
  };
}

/** Bounds are supplied by Excalidraw so rotation and line points are respected. */
export function bottomViewport(
  bounds: readonly ContentBounds[],
  viewport: CanvasViewport & { width: number; height: number },
): Pick<CanvasViewport, "scrollX" | "scrollY"> | null {
  let lowest: ContentBounds | undefined;
  for (const bound of bounds) {
    if (!bound.every(Number.isFinite)) continue;
    if (!lowest || bound[3] > lowest[3]) lowest = bound;
  }
  if (!lowest) return null;

  const zoom = viewport.zoom.value > 0 ? viewport.zoom.value : 1;
  const edge = Math.min(72, viewport.height / 4);
  const visibleWidth = viewport.width / zoom;
  const contentWidth = lowest[2] - lowest[0];
  return {
    scrollX: contentWidth > visibleWidth - 64 / zoom
      ? 32 / zoom - lowest[0]
      : visibleWidth / 2 - (lowest[0] + lowest[2]) / 2,
    // Keep the tail above the editing controls, including for very tall text.
    scrollY: (viewport.height - edge) / zoom - lowest[3],
  };
}

export function readViewport(value: unknown): CanvasViewport | undefined {
  if (!value || typeof value !== "object") return undefined;
  const candidate = value as Partial<CanvasViewport>;
  if (!Number.isFinite(candidate.scrollX) || !Number.isFinite(candidate.scrollY)
    || !candidate.zoom || !Number.isFinite(candidate.zoom.value) || candidate.zoom.value <= 0) {
    return undefined;
  }
  return { scrollX: candidate.scrollX!, scrollY: candidate.scrollY!, zoom: { value: candidate.zoom.value } };
}

export function reloadViewport(
  sameDocument: boolean,
  current: CanvasViewport,
  restored?: unknown,
): CanvasViewport | undefined {
  return sameDocument ? readViewport(current) : readViewport(restored);
}
