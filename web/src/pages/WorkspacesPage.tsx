import { useState } from "react";
import { Link } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, type Workspace } from "../api/client";
import { useAuth } from "../auth/AuthProvider";
import { ErrorMessage, Pager } from "../components";

const PAGE_SIZE = 10;

export function WorkspacesPage() {
  const qc = useQueryClient();
  const { canWrite, isAdmin } = useAuth();
  const [page, setPage] = useState(1);
  const [q, setQ] = useState("");
  const [sort, setSort] = useState("-createdAt");
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [editing, setEditing] = useState<Workspace | null>(null);

  const list = useQuery({
    queryKey: ["workspaces", { page, q, sort }],
    queryFn: () => api.listWorkspaces({ page, pageSize: PAGE_SIZE, q, sort }),
  });

  const invalidate = () => qc.invalidateQueries({ queryKey: ["workspaces"] });

  const create = useMutation({
    mutationFn: () => api.createWorkspace({ name, description: description || null }),
    onSuccess: () => {
      setName("");
      setDescription("");
      invalidate();
    },
  });

  const update = useMutation({
    mutationFn: (w: Workspace) =>
      api.updateWorkspace(w.id, { name: w.name, description: w.description, version: w.version }),
    onSuccess: () => {
      setEditing(null);
      invalidate();
    },
  });

  const remove = useMutation({
    mutationFn: (id: string) => api.deleteWorkspace(id),
    onSuccess: invalidate,
  });

  return (
    <section>
      <h2>Workspaces</h2>

      {canWrite && (
      <form
        onSubmit={(e) => {
          e.preventDefault();
          create.mutate();
        }}
        style={{ display: "flex", gap: "0.5rem", flexWrap: "wrap" }}
      >
        <input placeholder="Name" value={name} onChange={(e) => setName(e.target.value)} />
        <input
          placeholder="Description (optional)"
          value={description}
          onChange={(e) => setDescription(e.target.value)}
        />
        <button type="submit" disabled={create.isPending}>
          Create
        </button>
      </form>
      )}
      <ErrorMessage error={create.error} />

      <div style={{ display: "flex", gap: "0.5rem", margin: "1rem 0" }}>
        <input
          placeholder="Search…"
          value={q}
          onChange={(e) => {
            setQ(e.target.value);
            setPage(1);
          }}
        />
        <select value={sort} onChange={(e) => setSort(e.target.value)}>
          <option value="-createdAt">Newest</option>
          <option value="createdAt">Oldest</option>
          <option value="name">Name A–Z</option>
          <option value="-name">Name Z–A</option>
        </select>
      </div>

      {list.isLoading && <p>Loading…</p>}
      <ErrorMessage error={list.error} />
      {list.data && list.data.items.length === 0 && (
        <p>{isAdmin ? "No workspaces yet." : "No workspaces yet. Ask an admin to add you to one."}</p>
      )}

      <ul style={{ listStyle: "none", padding: 0 }}>
        {list.data?.items.map((w) => (
          <li key={w.id} style={{ padding: "0.5rem 0", borderBottom: "1px solid #ddd" }}>
            {editing?.id === w.id ? (
              <span style={{ display: "flex", gap: "0.5rem" }}>
                <input
                  value={editing.name}
                  onChange={(e) => setEditing({ ...editing, name: e.target.value })}
                />
                <input
                  value={editing.description ?? ""}
                  onChange={(e) => setEditing({ ...editing, description: e.target.value || null })}
                />
                <button onClick={() => update.mutate(editing)}>Save</button>
                <button onClick={() => setEditing(null)}>Cancel</button>
              </span>
            ) : (
              <span style={{ display: "flex", gap: "0.75rem", alignItems: "baseline" }}>
                <Link to={`/workspaces/${w.id}`}>
                  <strong>{w.name}</strong>
                </Link>
                <span style={{ color: "#666", flex: 1 }}>{w.description}</span>
                {canWrite && <button onClick={() => setEditing(w)}>Edit</button>}
                {isAdmin && (
                  <button
                    onClick={() => {
                      if (confirm(`Delete workspace "${w.name}"?`)) remove.mutate(w.id);
                    }}
                  >
                    Delete
                  </button>
                )}
              </span>
            )}
          </li>
        ))}
      </ul>
      <ErrorMessage error={update.error ?? remove.error} />

      {list.data && (
        <Pager
          page={list.data.page}
          pageSize={list.data.pageSize}
          totalCount={list.data.totalCount}
          onPage={setPage}
        />
      )}
    </section>
  );
}
