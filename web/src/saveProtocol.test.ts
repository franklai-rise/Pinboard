import { describe, expect, it } from "vitest";
import {
  collectReferencedFileIds,
  ExternalMutationGate,
  SaveProtocol,
  type SaveFilePayload
} from "./saveProtocol";

const image = (id: string): SaveFilePayload => ({
  id,
  dataUrl: `data:image/png;base64,${id}`,
  mimeType: "image/png",
  created: 42
});

describe("ExternalMutationGate", () => {
  it("blocks once, acknowledges once, and unlocks only on the matching reload", () => {
    const gate = new ExternalMutationGate();
    expect(gate.begin({ requestId: "capture-1", documentId: "board.pinboard" })).toBe(true);
    expect(gate.begin({ requestId: "capture-2", documentId: "board.pinboard" })).toBe(false);
    expect(gate.isBlocking).toBe(true);
    expect(gate.markReady()).toEqual({ requestId: "capture-1", documentId: "board.pinboard" });
    expect(gate.markReady()).toBeNull();
    expect(gate.completeOpen("other.pinboard")).toBe(false);
    expect(gate.completeOpen("board.pinboard")).toBe(true);
    expect(gate.isBlocking).toBe(false);
  });
});

describe("SaveProtocol", () => {
  it("keeps new files pending until the host acknowledges them", () => {
    const protocol = new SaveProtocol();
    protocol.openDocument("board.pinboard", 7, []);
    protocol.markChanged();

    const first = protocol.beginSave("save-1", "{\"elements\":[]}", [image("new-image")]);
    expect(first?.payload.baseRevision).toBe(7);
    expect(first?.payload.newFiles.map((file) => file.id)).toEqual(["new-image"]);

    protocol.reject({ saveId: "save-1", reason: "save-failed", error: "disk full" });
    expect(protocol.hasUnsavedChanges).toBe(true);

    const retry = protocol.beginSave("save-2", "{\"elements\":[]}", [image("new-image")]);
    expect(retry?.payload.newFiles.map((file) => file.id)).toEqual(["new-image"]);

    protocol.acknowledge({ saveId: "save-2", revision: 8, acceptedFileIds: ["new-image"] });
    expect(protocol.hasUnsavedChanges).toBe(false);

    protocol.markChanged();
    const later = protocol.beginSave("save-3", "{\"elements\":[]}", [image("new-image")]);
    expect(later?.payload.baseRevision).toBe(8);
    expect(later?.payload.newFiles).toEqual([]);
  });

  it("does not let an acknowledgement for another save clear pending work", () => {
    const protocol = new SaveProtocol();
    protocol.openDocument("board.pinboard", 3, []);
    protocol.markChanged();
    protocol.beginSave("current", "{}", [image("a")]);

    const result = protocol.acknowledge({ saveId: "stale", revision: 99, acceptedFileIds: ["a"] });
    expect(result.matched).toBe(false);
    expect(protocol.currentRevision).toBe(3);
    expect(protocol.isSaving).toBe(true);
    expect(protocol.hasUnsavedChanges).toBe(true);
  });

  it("queues edits made while a save is in flight on the acknowledged revision", () => {
    const protocol = new SaveProtocol();
    protocol.openDocument("board.pinboard", 10, []);
    protocol.markChanged();
    protocol.beginSave("save-1", "{\"version\":1}", [image("first")]);

    protocol.markChanged();
    const ack = protocol.acknowledge({ saveId: "save-1", revision: 11, acceptedFileIds: ["first"] });
    expect(ack).toEqual({ matched: true, needsSave: true });

    const followUp = protocol.beginSave("save-2", "{\"version\":2}", [image("first"), image("second")]);
    expect(followUp?.payload.baseRevision).toBe(11);
    expect(followUp?.payload.newFiles.map((file) => file.id)).toEqual(["second"]);
  });

  it("ignores an older reload of the active document", () => {
    const protocol = new SaveProtocol();
    expect(protocol.openDocument("board.pinboard", 5, ["asset"])).toBe(true);
    expect(protocol.openDocument("board.pinboard", 4, [])).toBe(false);
    expect(protocol.currentRevision).toBe(5);
  });

  it("does not let a duplicate same-revision reload discard local edits", () => {
    const protocol = new SaveProtocol();
    protocol.openDocument("board.pinboard", 5, []);
    protocol.markChanged();

    expect(protocol.openDocument("board.pinboard", 5, [])).toBe(false);
    expect(protocol.hasUnsavedChanges).toBe(true);
  });
});

describe("collectReferencedFileIds", () => {
  it("ignores stale Excalidraw files from a previously opened board", () => {
    const ids = collectReferencedFileIds([
      { id: "shape" },
      { id: "current-image", fileId: "current-file" },
      { id: "deleted-image", fileId: "undo-file", isDeleted: true },
      null
    ]);

    expect([...ids]).toEqual(["current-file", "undo-file"]);
    expect(ids.has("stale-file")).toBe(false);
  });
});
