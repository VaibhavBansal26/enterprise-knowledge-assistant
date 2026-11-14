export interface Workspace {
  id: string;
  name: string;
  description: string | null;
  createdAt: string;
  version: number;
}

export interface DocumentItem {
  id: string;
  workspaceId: string;
  title: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  status: "Pending" | "Processing" | "Ready" | "Failed";
  chunkCount: number;
  failureReason: string | null;
  metadata: Record<string, string>;
  createdAt: string;
  version: number;
}

export interface Member {
  userId: string;
  role: "Viewer" | "Editor";
  createdAt: string;
}

export interface AuditEntry {
  id: string;
  userId: string | null;
  action: string;
  entityType: string;
  entityId: string;
  changedFields: string | null;
  correlationId: string | null;
  occurredAt: string;
}

export interface Citation {
  number: number;
  documentId: string;
  workspaceId: string;
  documentTitle: string;
  chunkIndex: number;
  snippet: string;
}

export interface Conversation {
  id: string;
  workspaceId: string | null;
  title: string;
  createdAt: string;
  updatedAt: string;
}

export interface ChatMessage {
  id: string;
  role: "user" | "assistant";
  content: string;
  citations: Citation[];
  model: string | null;
  promptVersion: string | null;
  fromCache: boolean;
  feedback: "up" | "down" | null;
  createdAt: string;
}

export interface ConversationDetail {
  conversation: Conversation;
  messages: ChatMessage[];
}

export interface AnswerDone {
  messageId: string;
  content: string;
  citations: Citation[];
  model: string;
  promptVersion: string;
  fromCache: boolean;
  grounded: boolean;
  inputTokens: number;
  outputTokens: number;
}

export interface Paged<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface ListParams {
  page: number;
  pageSize: number;
  q?: string;
  sort?: string;
}

/** Error carrying the RFC 7807 ProblemDetails returned by the API. */
export class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
    public fieldErrors: Record<string, string[]> = {},
  ) {
    super(message);
  }
}

interface AuthHandlers {
  getToken: () => Promise<string | undefined>;
  onUnauthorized: () => void;
}

let auth: AuthHandlers | null = null;

/** Wired up by the AuthProvider so the client can attach tokens without depending on React. */
export function setAuthHandlers(handlers: AuthHandlers) {
  auth = handlers;
}

export const getAccessToken = () => auth?.getToken();

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const token = await auth?.getToken();
  const res = await fetch(path, {
    ...init,
    headers: {
      "Content-Type": "application/json",
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
      ...init?.headers,
    },
  });

  if (res.status === 401) auth?.onUnauthorized(); // token missing/expired: sign in again

  if (res.status === 204) return undefined as T;

  const body = await res.json().catch(() => null);
  if (!res.ok) {
    throw new ApiError(
      res.status,
      body?.detail ?? body?.title ?? `Request failed (${res.status})`,
      body?.errors ?? {},
    );
  }
  return body as T;
}

function qs(p: ListParams): string {
  const s = new URLSearchParams({ page: String(p.page), pageSize: String(p.pageSize) });
  if (p.q) s.set("q", p.q);
  if (p.sort) s.set("sort", p.sort);
  return s.toString();
}

const ws = "/api/v1/workspaces";

export const api = {
  listWorkspaces: (p: ListParams) => request<Paged<Workspace>>(`${ws}?${qs(p)}`),
  getWorkspace: (id: string) => request<Workspace>(`${ws}/${id}`),
  // Creates carry an Idempotency-Key so a retry (flaky network, double click) cannot create duplicates.
  createWorkspace: (b: { name: string; description: string | null }) =>
    request<Workspace>(ws, {
      method: "POST",
      headers: { "Idempotency-Key": crypto.randomUUID() },
      body: JSON.stringify(b),
    }),
  updateWorkspace: (id: string, b: { name: string; description: string | null; version: number }) =>
    request<Workspace>(`${ws}/${id}`, { method: "PUT", body: JSON.stringify(b) }),
  deleteWorkspace: (id: string) => request<void>(`${ws}/${id}`, { method: "DELETE" }),

  listDocuments: (workspaceId: string, p: ListParams) =>
    request<Paged<DocumentItem>>(`${ws}/${workspaceId}/documents?${qs(p)}`),
  reprocessDocument: (workspaceId: string, id: string) =>
    request<DocumentItem>(`${ws}/${workspaceId}/documents/${id}/reprocess`, { method: "POST" }),
  updateDocument: (
    workspaceId: string,
    id: string,
    b: { title: string; metadata: Record<string, string> | null; version: number },
  ) =>
    request<DocumentItem>(`${ws}/${workspaceId}/documents/${id}`, {
      method: "PUT",
      body: JSON.stringify(b),
    }),
  deleteDocument: (workspaceId: string, id: string) =>
    request<void>(`${ws}/${workspaceId}/documents/${id}`, { method: "DELETE" }),

  listMembers: (workspaceId: string) => request<Member[]>(`${ws}/${workspaceId}/members`),
  upsertMember: (workspaceId: string, userId: string, role: Member["role"]) =>
    request<Member>(`${ws}/${workspaceId}/members/${encodeURIComponent(userId)}`, {
      method: "PUT",
      body: JSON.stringify({ role }),
    }),
  removeMember: (workspaceId: string, userId: string) =>
    request<void>(`${ws}/${workspaceId}/members/${encodeURIComponent(userId)}`, { method: "DELETE" }),

  listConversations: (page = 1) => request<Paged<Conversation>>(`/api/v1/conversations?page=${page}&pageSize=50`),
  createConversation: (workspaceId: string | null) =>
    request<Conversation>("/api/v1/conversations", { method: "POST", body: JSON.stringify({ workspaceId, title: null }) }),
  getConversation: (id: string) => request<ConversationDetail>(`/api/v1/conversations/${id}`),
  deleteConversation: (id: string) => request<void>(`/api/v1/conversations/${id}`, { method: "DELETE" }),
  setFeedback: (conversationId: string, messageId: string, rating: "up" | "down" | "none") =>
    request<void>(`/api/v1/conversations/${conversationId}/messages/${messageId}/feedback`, {
      method: "PUT",
      body: JSON.stringify({ rating }),
    }),

  listAudit: (p: { page: number; pageSize: number; entityType?: string }) => {
    const s = new URLSearchParams({ page: String(p.page), pageSize: String(p.pageSize) });
    if (p.entityType) s.set("entityType", p.entityType);
    return request<Paged<AuditEntry>>(`/api/v1/audit?${s}`);
  },
};

/**
 * Uploads a file with progress reporting. `fetch` cannot report upload progress, so this uses XMLHttpRequest.
 * The Idempotency-Key makes a retry of the same upload safe (the server replays the first result).
 */
export async function uploadDocument(
  workspaceId: string,
  file: File,
  title: string | undefined,
  onProgress: (percent: number) => void,
): Promise<DocumentItem> {
  const token = await auth?.getToken();
  const form = new FormData();
  form.append("file", file);
  if (title) form.append("title", title);

  return new Promise<DocumentItem>((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open("POST", `${ws}/${workspaceId}/documents`);
    if (token) xhr.setRequestHeader("Authorization", `Bearer ${token}`);
    xhr.setRequestHeader("Idempotency-Key", crypto.randomUUID());

    xhr.upload.onprogress = (e) => {
      if (e.lengthComputable) onProgress(Math.round((e.loaded / e.total) * 100));
    };
    xhr.onerror = () => reject(new ApiError(0, "Network error while uploading"));
    xhr.onload = () => {
      if (xhr.status === 401) auth?.onUnauthorized();
      let body: { detail?: string; title?: string; errors?: Record<string, string[]> } | DocumentItem | null = null;
      try {
        body = JSON.parse(xhr.responseText);
      } catch {
        body = null;
      }
      if (xhr.status >= 200 && xhr.status < 300) {
        resolve(body as DocumentItem);
      } else {
        const problem = body as { detail?: string; title?: string; errors?: Record<string, string[]> } | null;
        reject(new ApiError(xhr.status, problem?.detail ?? problem?.title ?? `Upload failed (${xhr.status})`, problem?.errors));
      }
    };
    xhr.send(form);
  });
}

export interface AnswerHandlers {
  onSources: (sources: Citation[]) => void;
  onDelta: (text: string) => void;
  onDone: (done: AnswerDone) => void;
  onError: (message: string) => void;
}

/**
 * Asks a question and streams the answer. The response is Server-Sent Events over a POST, which `EventSource`
 * cannot do (no body, no auth header), so the stream is read with `fetch` and parsed here. Aborting the signal
 * closes the connection, which the server propagates all the way to the model call.
 */
export async function streamAnswer(
  conversationId: string,
  content: string,
  handlers: AnswerHandlers,
  signal: AbortSignal,
): Promise<void> {
  const token = await auth?.getToken();
  const res = await fetch(`/api/v1/conversations/${conversationId}/messages`, {
    method: "POST",
    signal,
    headers: {
      "Content-Type": "application/json",
      Accept: "text/event-stream",
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
    },
    body: JSON.stringify({ content }),
  });

  if (res.status === 401) auth?.onUnauthorized();
  if (!res.ok || !res.body) {
    const problem = await res.json().catch(() => null);
    throw new ApiError(res.status, problem?.detail ?? problem?.title ?? `Request failed (${res.status})`, problem?.errors ?? {});
  }

  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";

  const dispatch = (raw: string) => {
    let name = "";
    let data = "";
    for (const line of raw.split("\n")) {
      if (line.startsWith("event: ")) name = line.slice(7);
      else if (line.startsWith("data: ")) data += line.slice(6);
    }
    if (!name || !data) return;
    const payload = JSON.parse(data);
    if (name === "sources") handlers.onSources(payload.sources);
    else if (name === "delta") handlers.onDelta(payload.text);
    else if (name === "done") handlers.onDone(payload);
    else if (name === "error") handlers.onError(payload.message);
  };

  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });
    let end: number;
    while ((end = buffer.indexOf("\n\n")) >= 0) {
      dispatch(buffer.slice(0, end));
      buffer = buffer.slice(end + 2);
    }
  }
}
