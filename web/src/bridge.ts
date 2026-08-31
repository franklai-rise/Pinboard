export type HostMessage = {
  version: number;
  type: string;
  requestId?: string;
  documentId?: string;
  payload?: unknown;
};

export type WebMessage = Required<Pick<HostMessage, "version" | "type">> &
  Pick<HostMessage, "documentId" | "requestId" | "payload">;

type WebViewBridge = {
  postMessage(message: unknown): void;
  addEventListener(type: "message", listener: (event: MessageEvent<HostMessage>) => void): void;
  removeEventListener(type: "message", listener: (event: MessageEvent<HostMessage>) => void): void;
};

declare global {
  interface Window {
    chrome?: { webview?: WebViewBridge };
  }
}

export function createWebMessage(type: string, payload: unknown = {}, documentId?: string, requestId?: string): WebMessage {
  return { version: 1, type, documentId, requestId, payload };
}

export function isHostMessage(value: unknown): value is HostMessage {
  if (!value || typeof value !== "object") return false;
  const candidate = value as Partial<HostMessage>;
  return candidate.version === 1 && typeof candidate.type === "string" && candidate.type.length > 0;
}

export function postToHost(type: string, payload: unknown = {}, documentId?: string, requestId?: string) {
  window.chrome?.webview?.postMessage(createWebMessage(type, payload, documentId, requestId));
}

export function subscribeToHost(listener: (message: HostMessage) => void) {
  const bridge = window.chrome?.webview;
  if (!bridge) return () => undefined;
  const handler = (event: MessageEvent<HostMessage>) => {
    if (isHostMessage(event.data)) listener(event.data);
  };
  bridge.addEventListener("message", handler);
  return () => bridge.removeEventListener("message", handler);
}
