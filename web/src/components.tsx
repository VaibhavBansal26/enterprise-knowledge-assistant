import { ApiError } from "./api/client";

export function ErrorMessage({ error }: { error: unknown }) {
  if (!error) return null;
  const e = error as ApiError;
  const fields = Object.entries(e.fieldErrors ?? {});
  return (
    <div role="alert" style={{ color: "#b00020", margin: "0.5rem 0" }}>
      {e.status === 409
        ? "Someone else changed this item. Reload and try again."
        : (e.message ?? "Something went wrong")}
      {fields.length > 0 && (
        <ul>
          {fields.map(([field, msgs]) => (
            <li key={field}>
              {field}: {msgs.join(", ")}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

export function Pager({
  page,
  pageSize,
  totalCount,
  onPage,
}: {
  page: number;
  pageSize: number;
  totalCount: number;
  onPage: (p: number) => void;
}) {
  const pages = Math.max(1, Math.ceil(totalCount / pageSize));
  return (
    <div style={{ display: "flex", gap: "0.5rem", alignItems: "center", marginTop: "0.75rem" }}>
      <button disabled={page <= 1} onClick={() => onPage(page - 1)}>
        Prev
      </button>
      <span>
        Page {page} of {pages} ({totalCount} total)
      </span>
      <button disabled={page >= pages} onClick={() => onPage(page + 1)}>
        Next
      </button>
    </div>
  );
}
