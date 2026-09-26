export type CanvasViewport = {
  scrollX: number;
  scrollY: number;
  zoom: { value: number };
};

export type ContentBounds = readonly [number, number, number, number];

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
