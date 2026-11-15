import { useRef, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, uploadDocument, type DocumentItem } from "../api/client";
import { useIngestionUpdates } from "../api/ingestionHub";
import { useAuth } from "../auth/AuthProvider";
import { ErrorMessage, Pager } from "../components";
import { MembersPanel } from "./MembersPanel";

const PAGE_SIZE = 10;
const ACCEPT = ".pdf,.docx,.md,.markdown,.txt";

const STATUS_COLORS: Record<DocumentItem["status"], string> = {
  Pending: "#8a6d00",
  Processing: "#0b5cad",
  Ready: "#2e7d32",
  Failed: "#b00020",
};

function StatusBadge({ doc }: { doc: DocumentItem }) {
  return (
    <span
      title={doc.failureReason ?? undefined}
      style={{ color: STATUS_COLORS[doc.status], fontWeight: 600, whiteSpace: "nowrap" }}
    >
      {doc.status}
      {doc.status === "Ready" && ` · ${doc.chunkCount} chunks`}
    </span>
  );
}

function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

export function WorkspaceDetailPage() {
  const { id = "" } = useParams();
  const qc = useQueryClient();
  const { canWrite, isAdmin } = useAuth();
  const [page, setPage] = useState(1);
  const [q, setQ] = useState("");
  const [title, setTitle] = useState("");
  const [progress, setProgress] = useState<number | null>(null);
  const [editing, setEditing] = useState<DocumentItem | null>(null);
  const fileInput = useRef<HTMLInputElement>(null);

  const invalidate = () => qc.invalidateQueries({ queryKey: ["documents", id] });

  // Live updates from the ingestion worker; polling only runs while the live channel is down.
  const live = useIngestionUpdates(id, invalidate);

  const workspace = useQuery({ queryKey: ["workspace", id], queryFn: () => api.getWorkspace(id) });
  const docs = useQuery({
    queryKey: ["documents", id, { page, q }],
    queryFn: () => api.listDocuments(id, { page, pageSize: PAGE_SIZE, q, sort: "-createdAt" }),
    refetchInterval: (query) => {
      const inFlight = query.state.data?.items.some((d) => d.status === "Pending" || d.status === "Processing");
      return inFlight && !live ? 3000 : false;
    },
  });

  const upload = useMutation({
    mutationFn: (file: File) => uploadDocument(id, file, title.trim() || undefined, setProgress),
    onSettled: () => {
      setProgress(null);
      if (fileInput.current) fileInput.current.value = "";
    },
    onSuccess: () => {
      setTitle("");
      setPage(1);
      invalidate();
    },
  });

  const update = useMutation({
    mutationFn: (d: DocumentItem) =>
      api.updateDocument(id, d.id, { title: d.title, metadata: d.metadata, version: d.version }),
    onSuccess: () => {
      setEditing(null);
      invalidate();
    },
  });

  const reprocess = useMutation({ mutationFn: (docId: string) => api.reprocessDocument(id, docId), onSuccess: invalidate });
  const remove = useMutation({ mutationFn: (docId: string) => api.deleteDocument(id, docId), onSuccess: invalidate });

  if (workspace.isError) {
    return (
      <section>
        <Link to="/">← Workspaces</Link>
        <ErrorMessage error={workspace.error} />
      </section>
    );
  }

  return (
    <section>
      <Link to="/">← Workspaces</Link>
      <h2>{workspace.data?.name ?? "…"}</h2>
      <p style={{ color: "#666" }}>{workspace.data?.description}</p>

      <h3>
        Documents{" "}
        <small style={{ fontWeight: 400, color: live ? "#2e7d32" : "#8a6d00" }}>
          {live ? "● live updates" : "○ polling"}
        </small>
      </h3>

      {canWrite && (
        <form
          onSubmit={(e) => {
            e.preventDefault();
            const file = fileInput.current?.files?.[0];
            if (file) upload.mutate(file);
          }}
          style={{ display: "flex", gap: "0.5rem", flexWrap: "wrap", alignItems: "center" }}
        >
          <input ref={fileInput} type="file" accept={ACCEPT} required />
          <input placeholder="Title (optional)" value={title} onChange={(e) => setTitle(e.target.value)} />
          <button type="submit" disabled={upload.isPending}>
            Upload
          </button>
          {progress !== null && (
            <progress value={progress} max={100} aria-label="Upload progress">
              {progress}%
            </progress>
          )}
        </form>
      )}
      {canWrite && <small style={{ color: "#666" }}>PDF, DOCX, Markdown or text, up to 25 MB.</small>}
      <ErrorMessage error={upload.error} />

      <input
        style={{ margin: "1rem 0", display: "block" }}
        placeholder="Search documents…"
        value={q}
        onChange={(e) => {
          setQ(e.target.value);
          setPage(1);
        }}
      />

      {docs.isLoading && <p>Loading…</p>}
      <ErrorMessage error={docs.error} />
      {docs.data && docs.data.items.length === 0 && <p>No documents yet.</p>}

      <ul style={{ listStyle: "none", padding: 0 }}>
        {docs.data?.items.map((d) => (
          <li key={d.id} style={{ padding: "0.5rem 0", borderBottom: "1px solid #ddd" }}>
            {editing?.id === d.id ? (
              <span style={{ display: "flex", gap: "0.5rem" }}>
                <input value={editing.title} onChange={(e) => setEditing({ ...editing, title: e.target.value })} />
                <button onClick={() => update.mutate(editing)}>Save</button>
                <button onClick={() => setEditing(null)}>Cancel</button>
              </span>
            ) : (
              <>
                <span style={{ display: "flex", gap: "0.75rem", alignItems: "baseline" }}>
                  <strong>{d.title}</strong>
                  <span style={{ color: "#666", flex: 1 }}>
                    {d.fileName} · {formatSize(d.sizeBytes)}
                  </span>
                  <StatusBadge doc={d} />
                  {canWrite && (d.status === "Failed" || d.status === "Ready") && (
                    <button onClick={() => reprocess.mutate(d.id)} title="Run ingestion again">
                      Reprocess
                    </button>
                  )}
                  {canWrite && <button onClick={() => setEditing(d)}>Edit</button>}
                  {canWrite && (
                    <button
                      onClick={() => {
                        if (confirm(`Delete document "${d.title}"?`)) remove.mutate(d.id);
                      }}
                    >
                      Delete
                    </button>
                  )}
                </span>
                {d.status === "Failed" && d.failureReason && (
                  <div role="alert" style={{ color: "#b00020", fontSize: "0.9rem", marginTop: "0.25rem" }}>
                    {d.failureReason}
                  </div>
                )}
              </>
            )}
          </li>
        ))}
      </ul>
      <ErrorMessage error={update.error ?? remove.error ?? reprocess.error} />

      {docs.data && (
        <Pager
          page={docs.data.page}
          pageSize={docs.data.pageSize}
          totalCount={docs.data.totalCount}
          onPage={setPage}
        />
      )}

      {isAdmin && <MembersPanel workspaceId={id} />}
    </section>
  );
}
