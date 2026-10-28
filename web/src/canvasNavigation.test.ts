import { describe, expect, it } from "vitest";
import { bottomViewport, readViewport, reloadViewport, textHitViewport, textMatchRange } from "./canvasNavigation";

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

describe("search text navigation", () => {
  const viewport = { width: 900, height: 600, scrollX: 0, scrollY: 0, zoom: { value: 0.1 } };

  it("centers a keyword near the tail of a long card without shrinking its text", () => {
    const target = { x: 100, y: 200, width: 600, height: 25025, text: `${"Earlier content\n".repeat(1000)}Keep this keyword`, fontSize: 20, lineHeight: 1.25 };
    const result = textHitViewport(target, viewport, "keyword");
    expect(result.zoom.value).toBe(0.75);
    const matchLineY = target.y + 1000.5 * 25;
    expect((matchLineY + result.scrollY) * result.zoom.value).toBe(300);
  });

  it("preserves readable zoom and matches a phrase across wrapped lines", () => {
    const target = { x: 0, y: 0, width: 600, height: 75, text: "Notes: future\nreference material\nfor another day", fontSize: 20 };
    expect(textMatchRange(target.text, "future reference")).toEqual({ start: 7, end: 23 });
    const result = textHitViewport(target, { ...viewport, zoom: { value: 1.1 } }, "reference");
    expect(result.zoom.value).toBe(1.1);
    expect((37.5 + result.scrollY) * result.zoom.value).toBeCloseTo(300);
  });

  it("falls back to the first matching term and first readable line when no term matches", () => {
    expect(textMatchRange("中文参考资料与文字记录", "缺失 参考资料")).toEqual({ start: 2, end: 6 });
    const target = { x: 0, y: 50, width: 600, height: 10000, text: "Opening line\n".repeat(400), fontSize: 20 };
    const result = textHitViewport(target, { ...viewport, zoom: { value: 3 } }, "absent");
    expect(result.zoom.value).toBe(1.25);
    expect((62.5 + result.scrollY) * result.zoom.value).toBe(300);
  });

  it("accounts for rotated text around its center", () => {
    const target = { x: 10, y: 20, width: 400, height: 50, text: "keyword\nnext line", fontSize: 20, angle: Math.PI / 2 };
    const result = textHitViewport(target, { ...viewport, zoom: { value: 1 } }, "keyword");
    expect((222.5 + result.scrollX)).toBeCloseTo(450);
  });
});
