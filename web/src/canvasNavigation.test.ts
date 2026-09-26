import { describe, expect, it } from "vitest";
import { bottomViewport, readViewport, reloadViewport } from "./canvasNavigation";

describe("bottom navigation", () => {
  const viewport = { width: 900, height: 600, scrollX: 40, scrollY: -200, zoom: { value: 0.5 } };

  it("finds the actual lowest bounds and brings horizontally scattered content into view", () => {
    const result = bottomViewport([[0, 0, 400, 200], [8000, 1500, 8400, 1800]], viewport)!;
    expect((8200 + result.scrollX) * viewport.zoom.value).toBe(450);
    expect((1800 + result.scrollY) * viewport.zoom.value).toBe(528);
    expect(viewport.zoom.value).toBe(0.5);
  });

  it("shows the readable tail of long text and left-aligns an over-wide card", () => {
    const result = bottomViewport([[100, -300, 5000, 20000]], viewport)!;
    expect((100 + result.scrollX) * viewport.zoom.value).toBe(32);
    expect((20000 + result.scrollY) * viewport.zoom.value).toBe(528);
  });

  it("uses supplied rotated/line bounds and safely ignores invalid or empty content", () => {
    expect(bottomViewport([], viewport)).toBeNull();
    expect(bottomViewport([[NaN, 0, 0, 3]], viewport)).toBeNull();
    const result = bottomViewport([[-80, -30, 180, 240], [0, 0, 100, 200]], viewport)!;
    expect((240 + result.scrollY) * viewport.zoom.value).toBe(528);
  });
});

describe("reload viewport", () => {
  const reading = { scrollX: -3450, scrollY: -8200, zoom: { value: 1.25 } };
  const saved = { scrollX: 0, scrollY: 0, zoom: { value: 0.5 } };

  it("keeps the reading position during background capture, including its zoom", () => {
    expect(reloadViewport(true, reading, saved)).toEqual(reading);
  });

  it("restores the host viewport after unloading but does not carry another board's view", () => {
    expect(reloadViewport(false, reading, saved)).toEqual(saved);
    expect(reloadViewport(false, reading)).toBeUndefined();
    expect(readViewport({ ...saved, zoom: { value: 0 } })).toBeUndefined();
  });
});
