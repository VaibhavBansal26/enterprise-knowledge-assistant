import { useState } from "react";
import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { api } from "../api/client";
import { ErrorMessage, Pager } from "../components";

const PAGE_SIZE = 15;

export function AuditPage() {
  const [page, setPage] = useState(1);
  const audit = useQuery({
    queryKey: ["audit", page],
    queryFn: () => api.listAudit({ page, pageSize: PAGE_SIZE }),
  });

  return (
    <section>
      <Link to="/">← Workspaces</Link>
      <h2>Audit log</h2>
      <ErrorMessage error={audit.error} />
      {audit.isLoading && <p>Loading…</p>}

      <table style={{ width: "100%", borderCollapse: "collapse", fontSize: "0.9rem" }}>
        <thead>
          <tr style={{ textAlign: "left", borderBottom: "1px solid #ccc" }}>
            <th>When</th>
            <th>Action</th>
            <th>Entity</th>
            <th>Changed</th>
            <th>User</th>
          </tr>
        </thead>
        <tbody>
          {audit.data?.items.map((a) => (
            <tr key={a.id} style={{ borderBottom: "1px solid #eee" }}>
              <td>{new Date(a.occurredAt).toLocaleString()}</td>
              <td>{a.action}</td>
              <td>
                {a.entityType} <code>{a.entityId.slice(0, 8)}</code>
              </td>
              <td>{a.changedFields ?? "—"}</td>
              <td>
                <code>{a.userId?.slice(0, 8) ?? "—"}</code>
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      {audit.data && (
        <Pager
          page={audit.data.page}
          pageSize={audit.data.pageSize}
          totalCount={audit.data.totalCount}
          onPage={setPage}
        />
      )}
    </section>
  );
}
