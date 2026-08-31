import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  Excalidraw,
  exportToBlob,
  exportToSvg,
  hashElementsVersion,
  restoreAppState,
  restoreElements,
  serializeAsJSON
} from "@excalidraw/excalidraw";
import type { AppState, BinaryFiles, ExcalidrawImperativeAPI } from "@excalidraw/excalidraw/types";
import type { ExcalidrawElement } from "@excalidraw/excalidraw/element/types";
import { postToHost, subscribeToHost } from "./bridge";
import {
  collectReferencedFileIds,
  createSaveId,
  ExternalMutationGate,
  SaveProtocol,
  type SaveAckPayload,
  type SaveFilePayload,
  type SaveRejectedPayload
} from "./saveProtocol";

type HostAsset = {
  fileId: string;
  mimeType: string;
  dataUrl: string;
  createdAt: number;
};

type OpenPayload = {
  path: string;
  title: string;
  language?: "en" | "zh-CN";
  sceneJson: string;
  files: HostAsset[];
  revision: number;
};

type FlushWaiter = {
  requestId: string;
  documentId: string;
};

type SaveUiStatus = "saved" | "dirty" | "saving" | "failed" | "conflict";

function persistedSceneToken(elements: readonly ExcalidrawElement[], appState: AppState) {
  // serializeAsJSON persists these app-state fields but deliberately omits
  // transient selection, viewport, and image-cache changes.
  return JSON.stringify([
    hashElementsVersion(elements),
    appState.viewBackgroundColor,
    appState.gridModeEnabled,
    appState.gridSize,
    appState.gridStep
  ]);
}

export default function App() {
  const [api, setApi] = useState<ExcalidrawImperativeAPI | null>(null);
  const [documentPath, setDocumentPath] = useState("");
  const [documentTitle, setDocumentTitle] = useState("");
  const [language, setLanguage] = useState<"en" | "zh-CN">("en");
  const [loading, setLoading] = useState(true);
  const [externalMutationBlocking, setExternalMutationBlocking] = useState(false);
  const [saveStatus, setSaveStatus] = useState<SaveUiStatus>("saved");
  const [saveError, setSaveError] = useState("");
  const documentPathRef = useRef("");
  const loadingRef = useRef(true);
  const languageRef = useRef<"en" | "zh-CN">("en");
  const saveProtocol = useRef(new SaveProtocol());
  const externalMutation = useRef(new ExternalMutationGate());
  const saveTimer = useRef<number | null>(null);
  const maxSaveTimer = useRef<number | null>(null);
  const flushWaiters = useRef<FlushWaiter[]>([]);
  const deferredOpen = useRef<OpenPayload | null>(null);
  const lastPersistedSceneToken = useRef("");
  const lastState = useRef<{
    elements: readonly ExcalidrawElement[];
    appState: AppState;
    files: BinaryFiles;
  } | null>(null);

  const clearSaveTimers = useCallback(() => {
    if (saveTimer.current !== null) window.clearTimeout(saveTimer.current);
    if (maxSaveTimer.current !== null) window.clearTimeout(maxSaveTimer.current);
    saveTimer.current = null;
    maxSaveTimer.current = null;
  }, []);

  const completeFlushWaiters = useCallback((succeeded: boolean, reason?: string) => {
    const pending = flushWaiters.current.splice(0);
    for (const waiter of pending) {
      postToHost(
        succeeded ? "FlushResult" : "FlushFailed",
        succeeded
          ? { revision: saveProtocol.current.currentRevision }
          : { reason: reason ?? "save-failed" },
        waiter.documentId,
        waiter.requestId
      );
    }
  }, []);

  const completeExternalMutationReady = useCallback(() => {
    const waiter = externalMutation.current.markReady();
    if (!waiter) return false;
    postToHost(
      "ExternalMutationReady",
      { revision: saveProtocol.current.currentRevision },
      waiter.documentId,
      waiter.requestId
    );
    return true;
  }, []);

  const failExternalMutation = useCallback((reason: string, error?: string) => {
    const waiter = externalMutation.current.fail();
    if (!waiter) return;
    postToHost("ExternalMutationFailed", { reason, error }, waiter.documentId, waiter.requestId);
    loadingRef.current = false;
    setExternalMutationBlocking(false);
  }, []);

  const applyOpenDocument = useCallback((payload: OpenPayload) => {
    if (!api) return false;
    const revision = Number.isSafeInteger(payload.revision) ? payload.revision : 0;
    if (!saveProtocol.current.openDocument(payload.path, revision, payload.files.map((file) => file.fileId))) {
      return false;
    }

    externalMutation.current.completeOpen(payload.path);
    setExternalMutationBlocking(false);

    clearSaveTimers();
    loadingRef.current = true;
    setLoading(true);
    setSaveStatus("saved");
    setSaveError("");
    lastState.current = null;
    documentPathRef.current = payload.path;
    setDocumentPath(payload.path);
    setDocumentTitle(payload.title);
    setLanguage(payload.language === "zh-CN" ? "zh-CN" : "en");

    const scene = JSON.parse(payload.sceneJson);
    const elements = restoreElements(scene.elements ?? [], null);
    const appState = restoreAppState(scene.appState ?? {}, null);
    const binaryFiles = {} as BinaryFiles;
    for (const file of payload.files) {
      (binaryFiles as Record<string, unknown>)[file.fileId] = {
        id: file.fileId,
        dataURL: file.dataUrl,
        mimeType: file.mimeType,
        created: file.createdAt,
        lastRetrieved: file.createdAt
      };
    }

    api.resetScene();
    api.addFiles(Object.values(binaryFiles));
    api.updateScene({ elements, appState, collaborators: new Map() });
    const restoredAppState = api.getAppState();
    lastState.current = { elements, appState: restoredAppState, files: binaryFiles };
    lastPersistedSceneToken.current = persistedSceneToken(elements, restoredAppState);
    window.setTimeout(() => {
      loadingRef.current = false;
      setLoading(false);
      postToHost("DocumentRendered", { path: payload.path, revision }, payload.path);
    }, 0);
    return true;
  }, [api, clearSaveTimers]);

  const sendCurrentScene = useCallback(() => {
    const state = lastState.current;
    if (!state || !saveProtocol.current.activeDocumentId) return false;

    const serialized = JSON.parse(serializeAsJSON(state.elements, state.appState, {}, "local"));
    serialized.files = {};
    const referencedFileIds = collectReferencedFileIds(state.elements);
    const files: SaveFilePayload[] = Object.values(state.files)
      .filter((file) => referencedFileIds.has(file.id))
      .map((file) => ({
        id: file.id,
        dataUrl: file.dataURL,
        mimeType: file.mimeType,
        created: file.created
      }));
    const request = saveProtocol.current.beginSave(createSaveId(), JSON.stringify(serialized), files);
    if (!request) return false;

    clearSaveTimers();
    setSaveStatus("saving");
    setSaveError("");
    postToHost("SceneChanged", request.payload, request.documentId, request.saveId);
    return true;
  }, [clearSaveTimers]);

  const finishSuccessfulSaveCycle = useCallback(() => {
    if (saveProtocol.current.hasUnsavedChanges) {
      sendCurrentScene();
      return;
    }

    setSaveStatus("saved");
    setSaveError("");
    completeFlushWaiters(true);
    if (externalMutation.current.isBlocking) {
      completeExternalMutationReady();
      return;
    }
    const pending = deferredOpen.current;
    deferredOpen.current = null;
    if (pending) applyOpenDocument(pending);
  }, [applyOpenDocument, completeExternalMutationReady, completeFlushWaiters, sendCurrentScene]);

  const flush = useCallback((requestId?: string, requestedDocumentId?: string) => {
    const activePath = saveProtocol.current.activeDocumentId;
    const waiterDocumentId = requestedDocumentId || activePath;
    if (requestId) {
      if (!activePath || (waiterDocumentId && waiterDocumentId !== activePath)) {
        postToHost("FlushResult", { revision: 0, skipped: true }, waiterDocumentId, requestId);
        return;
      }
      flushWaiters.current.push({ requestId, documentId: waiterDocumentId });
    }

    clearSaveTimers();
    if (!saveProtocol.current.hasUnsavedChanges && !saveProtocol.current.isSaving) {
      completeFlushWaiters(true);
      const pending = deferredOpen.current;
      deferredOpen.current = null;
      if (pending) applyOpenDocument(pending);
      return;
    }
    sendCurrentScene();
  }, [applyOpenDocument, clearSaveTimers, completeFlushWaiters, sendCurrentScene]);

  const scheduleSave = useCallback(() => {
    if (loadingRef.current) return;
    saveProtocol.current.markChanged();
    setSaveStatus((current) => current === "saving" ? current : "dirty");
    if (saveTimer.current !== null) window.clearTimeout(saveTimer.current);
    saveTimer.current = window.setTimeout(() => flush(), 2000);
    if (maxSaveTimer.current === null) maxSaveTimer.current = window.setTimeout(() => flush(), 10000);
  }, [flush]);

  const openDocument = useCallback((payload: OpenPayload) => {
    if (!api) return;
    const currentPath = saveProtocol.current.activeDocumentId;
    if (
      currentPath &&
      currentPath !== payload.path &&
      (saveProtocol.current.hasUnsavedChanges || saveProtocol.current.isSaving)
    ) {
      deferredOpen.current = payload;
      flush();
      return;
    }

    // A same-path OpenDocument is an authoritative host reload after an
    // external capture. Do not flush the older scene over that new revision.
    applyOpenDocument(payload);
  }, [api, applyOpenDocument, flush]);

  const beginExternalMutation = useCallback((requestId?: string, requestedDocumentId?: string) => {
    const activePath = saveProtocol.current.activeDocumentId;
    const targetPath = requestedDocumentId || activePath;
    if (!requestId || !activePath || targetPath !== activePath) {
      if (requestId) {
        postToHost(
          "ExternalMutationFailed",
          { reason: "document-not-active" },
          targetPath,
          requestId
        );
      }
      return;
    }
    if (!externalMutation.current.begin({ requestId, documentId: targetPath })) {
      postToHost("ExternalMutationFailed", { reason: "mutation-already-pending" }, targetPath, requestId);
      return;
    }

    // Stop accepting canvas changes before asking the host to mutate the same
    // scene. This closes the gap between the flush acknowledgement and reload.
    loadingRef.current = true;
    setExternalMutationBlocking(true);
    clearSaveTimers();
    if (!saveProtocol.current.hasUnsavedChanges && !saveProtocol.current.isSaving) {
      completeExternalMutationReady();
      return;
    }
    sendCurrentScene();
  }, [clearSaveTimers, completeExternalMutationReady, sendCurrentScene]);

  useEffect(() => {
    if (!api) return;
    const unsubscribe = subscribeToHost((message) => {
      if (message.type === "OpenDocument") openDocument(message.payload as OpenPayload);
      if (message.type === "SaveAck") {
        const payload = message.payload as Partial<SaveAckPayload> & { requestId?: string };
        const saveId = payload.saveId ?? payload.requestId ?? message.requestId;
        if (!saveId || !Number.isSafeInteger(payload.revision)) return;
        const result = saveProtocol.current.acknowledge({
          saveId,
          revision: payload.revision as number,
          acceptedFileIds: Array.isArray(payload.acceptedFileIds) ? payload.acceptedFileIds : undefined
        });
        if (result.matched) finishSuccessfulSaveCycle();
      }
      if (message.type === "SaveRejected") {
        const payload = message.payload as Partial<SaveRejectedPayload> & { requestId?: string };
        const saveId = payload.saveId ?? payload.requestId ?? message.requestId;
        if (!saveId) return;
        const rejection: SaveRejectedPayload = {
          saveId,
          reason: payload.reason ?? "save-failed",
          revision: payload.revision,
          error: payload.error
        };
        const result = saveProtocol.current.reject(rejection);
        if (result.matched) {
          const conflict = rejection.reason === "revision-conflict";
          setSaveStatus(conflict ? "conflict" : "failed");
          setSaveError(rejection.error ?? rejection.reason);
          completeFlushWaiters(false, rejection.reason);
          failExternalMutation(rejection.reason, rejection.error);
        }
      }
      if (message.type === "SetLanguage") {
        const payload = message.payload as { language?: string };
        setLanguage(payload.language === "zh-CN" ? "zh-CN" : "en");
      }
      if (message.type === "SetDocumentTitle") {
        const payload = message.payload as { title?: string };
        if (payload.title) setDocumentTitle(payload.title);
      }
      if (message.type === "CloseDocument") {
        clearSaveTimers();
        flushWaiters.current = [];
        deferredOpen.current = null;
        externalMutation.current.reset();
        setExternalMutationBlocking(false);
        loadingRef.current = true;
        documentPathRef.current = "";
        saveProtocol.current.closeDocument();
        lastState.current = null;
        lastPersistedSceneToken.current = "";
        setDocumentPath("");
        setDocumentTitle("");
        setSaveStatus("saved");
        setSaveError("");
        api.resetScene();
        api.updateScene({
          elements: [],
          appState: {
            ...api.getAppState(),
            selectedElementIds: {},
            viewBackgroundColor: "#ffffff"
          },
          collaborators: new Map()
        });
        window.setTimeout(() => {
          loadingRef.current = false;
          setLoading(false);
        }, 0);
      }
      if (message.type === "FocusElement") {
        const payload = message.payload as { elementId?: string; x?: number; y?: number };
        const elements = api.getSceneElements();
        const target = payload.elementId ? elements.find((item) => item.id === payload.elementId) : undefined;
        if (target) {
          api.scrollToContent(target, { fitToContent: true, animate: true, duration: 350 });
          api.updateScene({ appState: { selectedElementIds: { [target.id]: true } } });
        } else if (typeof payload.x === "number" && typeof payload.y === "number") {
          const appState = api.getAppState();
          const zoom = appState.zoom.value || 1;
          api.updateScene({
            appState: {
              scrollX: appState.width / (2 * zoom) - payload.x,
              scrollY: appState.height / (2 * zoom) - payload.y
            }
          });
        }
      }
      if (message.type === "Flush") flush(message.requestId, message.documentId);
      if (message.type === "BeginExternalMutation") {
        beginExternalMutation(message.requestId, message.documentId);
      }
      if (message.type === "Export") {
        const payload = message.payload as { kind: "png" | "svg" | "excalidraw" };
        const elements = api.getSceneElements();
        const appState = api.getAppState();
        const files = api.getFiles();
        void (async () => {
          try {
            if (payload.kind === "excalidraw") {
              postToHost(
                "ExportResult",
                { kind: payload.kind, text: serializeAsJSON(elements, appState, files, "local") },
                documentPathRef.current,
                message.requestId
              );
              return;
            }
            if (payload.kind === "svg") {
              const svg = await exportToSvg({ elements, appState, files });
              postToHost(
                "ExportResult",
                { kind: payload.kind, text: new XMLSerializer().serializeToString(svg) },
                documentPathRef.current,
                message.requestId
              );
              return;
            }
            const blob = await exportToBlob({ elements, appState, files, mimeType: "image/png" });
            const reader = new FileReader();
            reader.onload = () => postToHost(
              "ExportResult",
              { kind: payload.kind, dataUrl: reader.result },
              documentPathRef.current,
              message.requestId
            );
            reader.onerror = () => postToHost(
              "ExportFailed",
              { error: languageRef.current === "zh-CN" ? "无法读取导出的 PNG。" : "Could not read the exported PNG." },
              documentPathRef.current,
              message.requestId
            );
            reader.readAsDataURL(blob);
          } catch (error) {
            postToHost(
              "ExportFailed",
              { error: error instanceof Error ? error.message : String(error) },
              documentPathRef.current,
              message.requestId
            );
          }
        })();
      }
    });
    postToHost("Ready");
    return unsubscribe;
  }, [api, beginExternalMutation, clearSaveTimers, completeFlushWaiters, failExternalMutation, finishSuccessfulSaveCycle, flush, openDocument]);

  useEffect(() => () => clearSaveTimers(), [clearSaveTimers]);

  useEffect(() => {
    languageRef.current = language;
  }, [language]);

  const initialData = useMemo(
    () => ({
      appState: {
        viewBackgroundColor: "#ffffff",
        currentItemStrokeColor: "#1b1b1f",
        currentItemStrokeWidth: 2,
        currentItemRoughness: 0,
        currentItemStartArrowhead: null,
        currentItemEndArrowhead: "arrow"
      }
    }),
    []
  );

  const statusText = saveStatus === "saving"
    ? (language === "zh-CN" ? "正在保存…" : "Saving…")
    : saveStatus === "dirty"
      ? (language === "zh-CN" ? "有未保存的更改" : "Unsaved changes")
      : saveStatus === "conflict"
        ? (language === "zh-CN" ? "画板已在外部更新，正在等待重新载入" : "Board changed externally; waiting to reload")
        : (language === "zh-CN" ? "保存失败" : "Save failed");

  return (
    <main className="canvas-shell">
      {loading && (
        <div className="loading">
          <div className="loading-card">
            <span className="loading-spinner" aria-hidden="true" />
            <span className="loading-copy">
              <strong>{language === "zh-CN" ? "正在打开画板" : "Opening board"}</strong>
              <small>{language === "zh-CN" ? "正在恢复图片与布局…" : "Restoring images and layout…"}</small>
            </span>
          </div>
        </div>
      )}
      {externalMutationBlocking && !loading && (
        <div className="loading loading--mutation">
          <div className="loading-card">
            <span className="loading-spinner" aria-hidden="true" />
            <span className="loading-copy">
              <strong>{language === "zh-CN" ? "正在接收内容" : "Receiving content"}</strong>
              <small>{language === "zh-CN" ? "正在安全合并截图或文字…" : "Safely merging the new capture…"}</small>
            </span>
          </div>
        </div>
      )}
      {documentPath && saveStatus !== "saved" && (
        <div className={`save-status save-status--${saveStatus}`} role="status" title={saveError}>
          <span className="save-status__dot" aria-hidden="true" />
          <span>{statusText}</span>
          {(saveStatus === "failed" || saveStatus === "conflict") && (
            <button type="button" onClick={() => flush()}>
              {language === "zh-CN" ? "重试" : "Retry"}
            </button>
          )}
        </div>
      )}
      <Excalidraw
        excalidrawAPI={setApi}
        initialData={initialData as never}
        langCode={language}
        theme="light"
        name={documentTitle || documentPath || "Pinboard"}
        detectScroll={false}
        handleKeyboardGlobally
        onChange={(elements, appState, files) => {
          lastState.current = { elements, appState, files };
          const nextToken = persistedSceneToken(elements, appState);
          if (nextToken === lastPersistedSceneToken.current) return;
          lastPersistedSceneToken.current = nextToken;
          scheduleSave();
        }}
        UIOptions={{
          canvasActions: {
            loadScene: false,
            saveToActiveFile: false,
            toggleTheme: false,
            changeViewBackgroundColor: true,
            export: false
          }
        }}
      />
    </main>
  );
}
