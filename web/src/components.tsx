import { ApiError } from "./api/client";

function messageFor(e: ApiError): string {
  switch (e.status) {
    case 403:
      return "You don't have permission to do that.";
    case 409:
      return "Someone else changed this item. Reload and try again.";
    case 429:
      return "Too many requests. Please wait a moment and try again.";
    default:
      return e.message ?? "Something went wrong";
  }
}

export function ErrorMessage({ error }: { error: unknown }) {
  if (!error) return null;
  const e = error as ApiError;
  const fields = Object.entries(e.fieldErrors ?? {});
  return (
    <div role="alert" style={{ color: "#b00020", margin: "0.5rem 0" }}>
      {messageFor(e)}
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
