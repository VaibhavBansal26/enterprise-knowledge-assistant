import { Link, Route, Routes } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { useAuth } from "./auth/AuthProvider";
import { AuditPage } from "./pages/AuditPage";
import { ChatPage } from "./pages/ChatPage";
import { WorkspaceDetailPage } from "./pages/WorkspaceDetailPage";
import { WorkspacesPage } from "./pages/WorkspacesPage";

function BackendStatus() {
  const { data, isError } = useQuery({
    queryKey: ["status", "ready"],
    queryFn: async () => (await fetch("/health/ready")).ok,
    refetchInterval: 10_000,
    retry: false,
  });
  const label = isError ? "API unreachable" : data ? "API ready" : "API degraded";
  return <small style={{ color: data ? "#2e7d32" : "#b00020" }}>● {label}</small>;
}

export function App() {
  const { username, roles, isAdmin, signOut } = useAuth();

  return (
    <main style={{ fontFamily: "system-ui", maxWidth: 760, margin: "2rem auto", padding: "0 1rem" }}>
      <header style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
        <h1>Knowledge Assistant</h1>
        <BackendStatus />
      </header>
      <div
        style={{ display: "flex", gap: "1rem", alignItems: "baseline", marginBottom: "1rem", color: "#444" }}
      >
        <span data-testid="whoami">
          Signed in as <strong>{username}</strong> ({roles.join(", ") || "no role"})
        </span>
        <Link to="/">Workspaces</Link>
        <Link to="/chat">Chat</Link>
        {isAdmin && <Link to="/audit">Audit log</Link>}
        <button onClick={signOut}>Sign out</button>
      </div>
      <Routes>
        <Route path="/" element={<WorkspacesPage />} />
        <Route path="/workspaces/:id" element={<WorkspaceDetailPage />} />
        <Route path="/chat" element={<ChatPage />} />
        <Route path="/audit" element={<AuditPage />} />
      </Routes>
    </main>
  );
}
