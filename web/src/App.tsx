import { Route, Routes } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { WorkspacesPage } from "./pages/WorkspacesPage";
import { WorkspaceDetailPage } from "./pages/WorkspaceDetailPage";

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
  return (
    <main style={{ fontFamily: "system-ui", maxWidth: 760, margin: "2rem auto", padding: "0 1rem" }}>
      <header style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
        <h1>Knowledge Assistant</h1>
        <BackendStatus />
      </header>
      <Routes>
        <Route path="/" element={<WorkspacesPage />} />
        <Route path="/workspaces/:id" element={<WorkspaceDetailPage />} />
      </Routes>
    </main>
  );
}
