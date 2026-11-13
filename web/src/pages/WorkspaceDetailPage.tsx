import { useState } from "react";
import { Link, useParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, type DocumentItem } from "../api/client";
import { ErrorMessage, Pager } from "../components";

const PAGE_SIZE = 10;

export function WorkspaceDetailPage() {
  const { id = "" } = useParams();
  const qc = useQueryClient();
  const [page, setPage] = useState(1);
  const [q, setQ] = useState("");
  const [title, setTitle] = useState("");
  const [fileName, setFileName] = useState("");
  const [editing, setEditing] = useState<DocumentItem | null>(null);

  const workspace = useQuery({ queryKey: ["workspace", id], queryFn: () => api.getWorkspace(id) });
  const docs = useQuery({
    queryKey: ["documents", id, { page, q }],
    queryFn: () => api.listDocuments(id, { page, pageSize: PAGE_SIZE, q, sort: "-createdAt" }),
  });

  const invalidate = () => qc.invalidateQueries({ queryKey: ["documents", id] });

  // File upload arrives in Phase 4; for now a document is a metadata record.
  const create = useMutation({
    mutationFn: () =>
      api.createDocument(id, {
        title,
        fileName: fileName || `${title}.pdf`,
        contentType: "application/pdf",
        sizeBytes: 1,
      }),
    onSuccess: () => {
      setTitle("");
      setFileName("");
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

  const remove = useMutation({
    mutationFn: (docId: string) => api.deleteDocument(id, docId),
    onSuccess: invalidate,
  });

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

      <h3>Documents</h3>
      <form
        onSubmit={(e) => {
          e.preventDefault();
          create.mutate();
        }}
        style={{ display: "flex", gap: "0.5rem", flexWrap: "wrap" }}
      >
        <input placeholder="Title" value={title} onChange={(e) => setTitle(e.target.value)} />
        <input
          placeholder="File name (optional)"
          value={fileName}
          onChange={(e) => setFileName(e.target.value)}
        />
        <button type="submit" disabled={create.isPending}>
          Add
        </button>
      </form>
      <ErrorMessage error={create.error} />

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
                <input
                  value={editing.title}
                  onChange={(e) => setEditing({ ...editing, title: e.target.value })}
                />
                <button onClick={() => update.mutate(editing)}>Save</button>
                <button onClick={() => setEditing(null)}>Cancel</button>
              </span>
            ) : (
              <span style={{ display: "flex", gap: "0.75rem", alignItems: "baseline" }}>
                <strong>{d.title}</strong>
                <span style={{ color: "#666", flex: 1 }}>
                  {d.fileName} · {d.status}
                </span>
                <button onClick={() => setEditing(d)}>Edit</button>
                <button
                  onClick={() => {
                    if (confirm(`Delete document "${d.title}"?`)) remove.mutate(d.id);
                  }}
                >
                  Delete
                </button>
              </span>
            )}
          </li>
        ))}
      </ul>
      <ErrorMessage error={update.error ?? remove.error} />

      {docs.data && (
        <Pager
          page={docs.data.page}
          pageSize={docs.data.pageSize}
          totalCount={docs.data.totalCount}
          onPage={setPage}
        />
      )}
    </section>
  );
}
