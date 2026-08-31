import { describe, expect, it } from "vitest";
import { createWebMessage, isHostMessage } from "./bridge";

describe("canvas bridge", () => {
  it("creates a versioned message with document and request correlation", () => {
    expect(createWebMessage("SceneChanged", { saveId: "save-1" }, "board.pinboard", "save-1")).toEqual({
      version: 1,
      type: "SceneChanged",
      documentId: "board.pinboard",
      requestId: "save-1",
      payload: { saveId: "save-1" }
    });
  });

  it("rejects malformed or unsupported host messages", () => {
    expect(isHostMessage({ version: 1, type: "SaveAck", payload: {} })).toBe(true);
    expect(isHostMessage({ version: 2, type: "SaveAck" })).toBe(false);
    expect(isHostMessage({ version: 1, type: "" })).toBe(false);
    expect(isHostMessage(null)).toBe(false);
  });
});
