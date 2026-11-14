import { Fragment, useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  api,
  ApiError,
  streamAnswer,
  type ChatMessage,
  type Citation,
} from "../api/client";
import { ErrorMessage } from "../components";

/** A message being shown: either saved (has an id) or still streaming in. */
interface ViewMessage extends Omit<ChatMessage, "id" | "createdAt"> {
  id: string | null;
  streaming?: boolean;
  stopped?: boolean;
  error?: string;
  sourcesSeen?: number;
}

/** Turns "[1]" markers into clickable chips for the sources the answer actually cited. */
function AnswerText({ text, citations, onCite }: { text: string; citations: Citation[]; onCite: (n: number) => void }) {
  const known = new Set(citations.map((c) => c.number));
  return (
    <>
      {text.split(/(\[\d{1,3}\])/g).map((part, i) => {
        const match = /^\[(\d{1,3})\]$/.exec(part);
        if (match && known.has(Number(match[1]))) {
          return (
            <button
              key={i}
              onClick={() => onCite(Number(match[1]))}
              title="Show source"
              style={{ border: "1px solid #9bb7e0", background: "#eef4ff", borderRadius: 4, padding: "0 4px", margin: "0 1px", cursor: "pointer", fontSize: "0.8em" }}
            >
              {match[1]}
            </button>
          );
        }
        return <Fragment key={i}>{part}</Fragment>;
      })}
    </>
  );
}

function Sources({ citations, highlighted }: { citations: Citation[]; highlighted: number | null }) {
  if (citations.length === 0) return null;
  return (
    <ol style={{ margin: "0.5rem 0 0", paddingLeft: "1.25rem", fontSize: "0.85rem", color: "#444" }}>
      {citations.map((c) => (
        <li
          key={c.number}
          value={c.number}
          style={{ marginBottom: "0.35rem", background: highlighted === c.number ? "#fff6d6" : undefined, borderRadius: 4, padding: "2px 4px" }}
        >
          <Link to={`/workspaces/${c.workspaceId}`}>
            <strong>{c.documentTitle}</strong>
          </Link>
          <div style={{ color: "#666" }}>{c.snippet}</div>
        </li>
      ))}
    </ol>
  );
}

export function ChatPage() {
  const qc = useQueryClient();
  const [activeId, setActiveId] = useState<string | null>(null);
  const [scope, setScope] = useState<string>(""); // "" = everything I can read
  const [messages, setMessages] = useState<ViewMessage[]>([]);
  const [input, setInput] = useState("");
  const [busy, setBusy] = useState(false);
  const [highlight, setHighlight] = useState<{ message: number; n: number } | null>(null);
  const abort = useRef<AbortController | null>(null);
  const bottom = useRef<HTMLDivElement>(null);

  const conversations = useQuery({ queryKey: ["conversations"], queryFn: () => api.listConversations() });
  const workspaces = useQuery({
    queryKey: ["workspaces", "all"],
    queryFn: () => api.listWorkspaces({ page: 1, pageSize: 100, sort: "name" }),
  });

  // Braces matter: an effect must return nothing or a cleanup function, never the result of a call.
  useEffect(() => {
    bottom.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages]);
  useEffect(
    () => () => {
      abort.current?.abort(); // leaving the page stops an in-flight answer
    },
    [],
  );

  const open = async (id: string) => {
    abort.current?.abort();
    setBusy(false);
    setActiveId(id);
    const detail = await api.getConversation(id);
    setScope(detail.conversation.workspaceId ?? "");
    setMessages(detail.messages.map((m) => ({ ...m })));
  };

  const startNew = () => {
    abort.current?.abort();
    setBusy(false);
    setActiveId(null);
    setMessages([]);
  };

  const remove = useMutation({
    mutationFn: (id: string) => api.deleteConversation(id),
    onSuccess: (_, id) => {
      if (id === activeId) startNew();
      qc.invalidateQueries({ queryKey: ["conversations"] });
    },
  });

  const rate = async (index: number, rating: "up" | "down") => {
    const m = messages[index];
    if (!activeId || !m.id) return;
    const next = m.feedback === rating ? "none" : rating;
    setMessages((all) => all.map((x, i) => (i === index ? { ...x, feedback: next === "none" ? null : next } : x)));
    await api.setFeedback(activeId, m.id, next).catch(() => undefined);
  };

  const patchLast = (patch: (m: ViewMessage) => ViewMessage) =>
    setMessages((all) => all.map((m, i) => (i === all.length - 1 ? patch(m) : m)));

  const ask = async () => {
    const question = input.trim();
    if (!question || busy) return;
    setInput("");
    setBusy(true);

    const controller = new AbortController();
    abort.current = controller;

    setMessages((all) => [
      ...all,
      { id: null, role: "user", content: question, citations: [], model: null, promptVersion: null, fromCache: false, feedback: null },
      { id: null, role: "assistant", content: "", citations: [], model: null, promptVersion: null, fromCache: false, feedback: null, streaming: true },
    ]);

    try {
      let conversationId = activeId;
      if (!conversationId) {
        const created = await api.createConversation(scope || null);
        conversationId = created.id;
        setActiveId(created.id);
      }

      await streamAnswer(
        conversationId,
        question,
        {
          onSources: (sources) => patchLast((m) => ({ ...m, sourcesSeen: sources.length })),
          onDelta: (text) => patchLast((m) => ({ ...m, content: m.content + text })),
          onDone: (d) =>
            patchLast((m) => ({
              ...m,
              id: d.messageId,
              content: d.content,
              citations: d.citations,
              model: d.model,
              promptVersion: d.promptVersion,
              fromCache: d.fromCache,
              streaming: false,
            })),
          onError: (message) => patchLast((m) => ({ ...m, streaming: false, error: message })),
        },
        controller.signal,
      );
      patchLast((m) => (m.streaming ? { ...m, streaming: false } : m));
    } catch (e) {
      if (controller.signal.aborted) {
        patchLast((m) => ({ ...m, streaming: false, stopped: true }));
      } else {
        const message =
          e instanceof ApiError && e.status === 429
            ? "You're asking too quickly. Please wait a moment and try again."
            : e instanceof Error
              ? e.message
              : "Something went wrong.";
        patchLast((m) => ({ ...m, streaming: false, error: message }));
      }
    } finally {
      setBusy(false);
      abort.current = null;
      qc.invalidateQueries({ queryKey: ["conversations"] });
    }
  };

  return (
    <section style={{ display: "grid", gridTemplateColumns: "220px 1fr", gap: "1.5rem" }}>
      <aside>
        <button onClick={startNew} style={{ width: "100%", marginBottom: "0.75rem" }}>
          + New chat
        </button>
        <label style={{ fontSize: "0.85rem", color: "#555" }}>
          Search in
          <select
            value={scope}
            onChange={(e) => setScope(e.target.value)}
            disabled={activeId !== null}
            style={{ width: "100%", marginTop: 4 }}
            title={activeId ? "A conversation keeps its scope" : undefined}
          >
            <option value="">All my workspaces</option>
            {workspaces.data?.items.map((w) => (
              <option key={w.id} value={w.id}>
                {w.name}
              </option>
            ))}
          </select>
        </label>
        <ul style={{ listStyle: "none", padding: 0, marginTop: "1rem" }}>
          {conversations.data?.items.map((c) => (
            <li key={c.id} style={{ display: "flex", gap: 4, marginBottom: 4 }}>
              <button
                onClick={() => open(c.id)}
                style={{ flex: 1, textAlign: "left", fontWeight: c.id === activeId ? 700 : 400, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}
                title={c.title || "New conversation"}
              >
                {c.title || "New conversation"}
              </button>
              <button onClick={() => remove.mutate(c.id)} aria-label={`Delete ${c.title}`} title="Delete">
                ×
              </button>
            </li>
          ))}
        </ul>
        <ErrorMessage error={conversations.error ?? remove.error} />
      </aside>

      <div>
        <h2 style={{ marginTop: 0 }}>Ask your documents</h2>
        {messages.length === 0 && (
          <p style={{ color: "#666" }}>
            Ask a question. Answers are based only on documents you can access, with numbered sources you can check.
          </p>
        )}

        {messages.map((m, i) => (
          <div
            key={i}
            style={{
              margin: "0.75rem 0",
              padding: "0.6rem 0.8rem",
              borderRadius: 8,
              background: m.role === "user" ? "#f1f3f5" : "#fff",
              border: m.role === "user" ? "none" : "1px solid #e1e4e8",
              whiteSpace: "pre-wrap",
            }}
          >
            <div style={{ fontSize: "0.75rem", color: "#777", marginBottom: 4 }}>{m.role === "user" ? "You" : "Assistant"}</div>
            {m.role === "assistant" ? (
              <>
                <AnswerText text={m.content} citations={m.citations} onCite={(n) => setHighlight({ message: i, n })} />
                {m.streaming && m.content === "" && (
                  <em style={{ color: "#777" }}>{m.sourcesSeen ? `Reading ${m.sourcesSeen} passages…` : "Searching your documents…"}</em>
                )}
                {m.streaming && m.content !== "" && <span aria-label="typing"> ▍</span>}
                {m.stopped && <em style={{ color: "#8a6d00" }}> (stopped)</em>}
                {m.error && (
                  <div role="alert" style={{ color: "#b00020", marginTop: 4 }}>
                    {m.error}
                  </div>
                )}
                <Sources citations={m.citations} highlighted={highlight?.message === i ? highlight.n : null} />
                {m.id && !m.streaming && (
                  <div style={{ display: "flex", gap: 8, alignItems: "center", marginTop: 8, fontSize: "0.75rem", color: "#777" }}>
                    <button onClick={() => rate(i, "up")} aria-pressed={m.feedback === "up"} title="Helpful" style={{ opacity: m.feedback === "up" ? 1 : 0.5 }}>
                      👍
                    </button>
                    <button onClick={() => rate(i, "down")} aria-pressed={m.feedback === "down"} title="Not helpful" style={{ opacity: m.feedback === "down" ? 1 : 0.5 }}>
                      👎
                    </button>
                    {m.fromCache && <span title="Reused from an earlier identical question">⚡ cached</span>}
                    <span>
                      {m.model} · {m.promptVersion}
                    </span>
                  </div>
                )}
              </>
            ) : (
              m.content
            )}
          </div>
        ))}
        <div ref={bottom} />

        <form
          onSubmit={(e) => {
            e.preventDefault();
            void ask();
          }}
          style={{ display: "flex", gap: 8, marginTop: "1rem" }}
        >
          <input
            value={input}
            onChange={(e) => setInput(e.target.value)}
            placeholder="Ask a question about your documents…"
            maxLength={4000}
            style={{ flex: 1, padding: "0.5rem" }}
            disabled={busy}
          />
          {busy ? (
            <button type="button" onClick={() => abort.current?.abort()}>
              ■ Stop
            </button>
          ) : (
            <button type="submit" disabled={!input.trim()}>
              Ask
            </button>
          )}
        </form>
      </div>
    </section>
  );
}
