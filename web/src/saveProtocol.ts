export type SaveFilePayload = {
  id: string;
  dataUrl: string;
  mimeType: string;
  created: number;
};

export type SceneSavePayload = {
  saveId: string;
  baseRevision: number;
  sceneJson: string;
  newFiles: SaveFilePayload[];
};

export type SceneSaveRequest = {
  documentId: string;
  saveId: string;
  payload: SceneSavePayload;
};

export type SaveAckPayload = {
  saveId: string;
  revision: number;
  acceptedFileIds?: string[];
};

export type SaveRejectedPayload = {
  saveId: string;
  reason: string;
  revision?: number;
  error?: string;
};

export type SaveResult = {
  matched: boolean;
  needsSave: boolean;
};

export type ExternalMutationRequest = {
  requestId: string;
  documentId: string;
};

export class ExternalMutationGate {
  private pending: (ExternalMutationRequest & { ready: boolean }) | null = null;

  get isBlocking() {
    return this.pending !== null;
  }

  get request() {
    return this.pending;
  }

  begin(request: ExternalMutationRequest) {
    if (this.pending) return false;
    this.pending = { ...request, ready: false };
    return true;
  }

  markReady() {
    if (!this.pending || this.pending.ready) return null;
    this.pending.ready = true;
    return { requestId: this.pending.requestId, documentId: this.pending.documentId };
  }

  fail() {
    if (!this.pending) return null;
    const request = { requestId: this.pending.requestId, documentId: this.pending.documentId };
    this.pending = null;
    return request;
  }

  completeOpen(documentId: string) {
    if (!this.pending || this.pending.documentId !== documentId) return false;
    this.pending = null;
    return true;
  }

  reset() {
    this.pending = null;
  }
}

type InFlightSave = {
  saveId: string;
  changeVersion: number;
  fileIds: string[];
};

/**
 * Tracks the optimistic canvas state without treating it as durable state.
 * A file becomes acknowledged only after the host returns SaveAck.
 */
export class SaveProtocol {
  private documentId = "";
  private revision = 0;
  private changeVersion = 0;
  private savedChangeVersion = 0;
  private acknowledgedFileIds = new Set<string>();
  private inFlight: InFlightSave | null = null;
  private rejection: SaveRejectedPayload | null = null;

  get activeDocumentId() {
    return this.documentId;
  }

  get currentRevision() {
    return this.revision;
  }

  get isSaving() {
    return this.inFlight !== null;
  }

  get hasUnsavedChanges() {
    return this.changeVersion > this.savedChangeVersion;
  }

  get lastRejection() {
    return this.rejection;
  }

  /** Returns false for an older snapshot of the same document. */
  openDocument(documentId: string, revision: number, fileIds: Iterable<string>) {
    if (documentId === this.documentId && revision < this.revision) return false;
    if (
      documentId === this.documentId &&
      revision === this.revision &&
      (this.hasUnsavedChanges || this.isSaving)
    ) {
      return false;
    }

    this.documentId = documentId;
    this.revision = revision;
    this.changeVersion = 0;
    this.savedChangeVersion = 0;
    this.acknowledgedFileIds = new Set(fileIds);
    this.inFlight = null;
    this.rejection = null;
    return true;
  }

  closeDocument() {
    this.documentId = "";
    this.revision = 0;
    this.changeVersion = 0;
    this.savedChangeVersion = 0;
    this.acknowledgedFileIds.clear();
    this.inFlight = null;
    this.rejection = null;
  }

  markChanged() {
    this.changeVersion += 1;
    return this.changeVersion;
  }

  beginSave(saveId: string, sceneJson: string, files: readonly SaveFilePayload[]): SceneSaveRequest | null {
    if (!this.documentId || !this.hasUnsavedChanges || this.inFlight) return null;

    const newFiles = files.filter((file) => !this.acknowledgedFileIds.has(file.id));
    this.inFlight = {
      saveId,
      changeVersion: this.changeVersion,
      fileIds: newFiles.map((file) => file.id)
    };
    this.rejection = null;

    return {
      documentId: this.documentId,
      saveId,
      payload: {
        saveId,
        baseRevision: this.revision,
        sceneJson,
        newFiles
      }
    };
  }

  acknowledge(payload: SaveAckPayload): SaveResult {
    if (!this.inFlight || payload.saveId !== this.inFlight.saveId) {
      return { matched: false, needsSave: this.hasUnsavedChanges };
    }

    const accepted = payload.acceptedFileIds ?? this.inFlight.fileIds;
    for (const fileId of accepted) this.acknowledgedFileIds.add(fileId);

    const allFilesAccepted = this.inFlight.fileIds.every((fileId) => this.acknowledgedFileIds.has(fileId));
    if (allFilesAccepted) {
      this.savedChangeVersion = Math.max(this.savedChangeVersion, this.inFlight.changeVersion);
    }
    this.revision = Math.max(this.revision, payload.revision);
    this.inFlight = null;
    this.rejection = allFilesAccepted
      ? null
      : {
          saveId: payload.saveId,
          reason: "assets-not-accepted",
          revision: payload.revision,
          error: "The host did not confirm every pending image."
        };

    return { matched: true, needsSave: this.hasUnsavedChanges };
  }

  reject(payload: SaveRejectedPayload): SaveResult {
    if (!this.inFlight || payload.saveId !== this.inFlight.saveId) {
      return { matched: false, needsSave: this.hasUnsavedChanges };
    }

    // Keep both the dirty scene and pending files. A retry must use the same
    // base revision unless the host supplies a fresh OpenDocument snapshot.
    this.inFlight = null;
    this.rejection = payload;
    return { matched: true, needsSave: this.hasUnsavedChanges };
  }
}

export function createSaveId() {
  if (typeof crypto !== "undefined" && typeof crypto.randomUUID === "function") {
    return crypto.randomUUID();
  }
  return `save-${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
}

/**
 * Excalidraw keeps binary files in memory even after resetScene(). Only send
 * files that the serialized scene actually references, otherwise switching
 * boards can copy an earlier board's image cache into the next database.
 */
export function collectReferencedFileIds(elements: readonly unknown[]) {
  const ids = new Set<string>();
  for (const element of elements) {
    if (!element || typeof element !== "object") continue;
    const fileId = (element as { fileId?: unknown }).fileId;
    if (typeof fileId === "string" && fileId.length > 0) ids.add(fileId);
  }
  return ids;
}
