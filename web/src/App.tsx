import { useQuery } from "@tanstack/react-query";

async function fetchStatus(path: string): Promise<string> {
  const res = await fetch(path);
  return res.ok ? "healthy" : `unhealthy (${res.status})`;
}

function StatusRow({ label, path }: { label: string; path: string }) {
  const { data, isLoading, isError } = useQuery({
    queryKey: ["status", path],
    queryFn: () => fetchStatus(path),
    refetchInterval: 10_000,
    retry: false,
  });

  const text = isLoading ? "checking…" : isError ? "unreachable" : data;
  return (
    <li>
      {label}: <strong>{text}</strong>
    </li>
  );
}

export function App() {
  return (
    <main style={{ fontFamily: "system-ui", maxWidth: 640, margin: "4rem auto" }}>
      <h1>Knowledge Assistant</h1>
      <p>Phase 0 skeleton. Backend status:</p>
      <ul>
        <StatusRow label="Liveness" path="/health/live" />
        <StatusRow label="Readiness (Postgres + Redis)" path="/health/ready" />
      </ul>
    </main>
  );
}
