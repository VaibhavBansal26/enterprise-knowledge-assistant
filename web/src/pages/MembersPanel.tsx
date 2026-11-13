import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, type Member } from "../api/client";
import { ErrorMessage } from "../components";

/** Workspace membership management. Rendered for tenant admins only (the API enforces this too). */
export function MembersPanel({ workspaceId }: { workspaceId: string }) {
  const qc = useQueryClient();
  const [userId, setUserId] = useState("");
  const [role, setRole] = useState<Member["role"]>("Viewer");

  const members = useQuery({
    queryKey: ["members", workspaceId],
    queryFn: () => api.listMembers(workspaceId),
  });
  const invalidate = () => qc.invalidateQueries({ queryKey: ["members", workspaceId] });

  const upsert = useMutation({
    mutationFn: () => api.upsertMember(workspaceId, userId.trim(), role),
    onSuccess: () => {
      setUserId("");
      invalidate();
    },
  });
  const remove = useMutation({
    mutationFn: (id: string) => api.removeMember(workspaceId, id),
    onSuccess: invalidate,
  });

  return (
    <section style={{ marginTop: "2rem" }}>
      <h3>Members</h3>
      <p style={{ color: "#666", marginTop: 0 }}>
        Non-admin users can only see workspaces they are a member of. Use the user's ID (the token's
        <code> sub</code>).
      </p>

      <form
        onSubmit={(e) => {
          e.preventDefault();
          if (userId.trim()) upsert.mutate();
        }}
        style={{ display: "flex", gap: "0.5rem", flexWrap: "wrap" }}
      >
        <input placeholder="User ID" value={userId} onChange={(e) => setUserId(e.target.value)} />
        <select value={role} onChange={(e) => setRole(e.target.value as Member["role"])}>
          <option value="Viewer">Viewer</option>
          <option value="Editor">Editor</option>
        </select>
        <button type="submit" disabled={upsert.isPending}>
          Add / update
        </button>
      </form>
      <ErrorMessage error={upsert.error ?? remove.error ?? members.error} />

      <ul style={{ listStyle: "none", padding: 0 }}>
        {members.data?.map((m) => (
          <li key={m.userId} style={{ display: "flex", gap: "0.75rem", padding: "0.35rem 0" }}>
            <code style={{ flex: 1 }}>{m.userId}</code>
            <span>{m.role}</span>
            <button onClick={() => remove.mutate(m.userId)}>Remove</button>
          </li>
        ))}
        {members.data?.length === 0 && <li>No members.</li>}
      </ul>
    </section>
  );
}
