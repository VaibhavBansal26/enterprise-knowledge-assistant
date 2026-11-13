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
  status: string;
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
  createDocument: (
    workspaceId: string,
    b: { title: string; fileName: string; contentType: string; sizeBytes: number },
  ) =>
    request<DocumentItem>(`${ws}/${workspaceId}/documents`, {
      method: "POST",
      headers: { "Idempotency-Key": crypto.randomUUID() },
      body: JSON.stringify({ ...b, metadata: null }),
    }),
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

  listAudit: (p: { page: number; pageSize: number; entityType?: string }) => {
    const s = new URLSearchParams({ page: String(p.page), pageSize: String(p.pageSize) });
    if (p.entityType) s.set("entityType", p.entityType);
    return request<Paged<AuditEntry>>(`/api/v1/audit?${s}`);
  },
};
