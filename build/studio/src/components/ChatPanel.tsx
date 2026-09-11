import { useEffect, useRef, useState } from "react";
import {
  ArrowUp,
  Bot,
  CircleStop,
  MessageSquarePlus,
  Terminal,
  TriangleAlert,
  User,
  Wrench,
} from "lucide-react";
import { api, chat, type ChatEntry } from "../api";
import { Empty, IconButton, Pane, cx } from "./ui";

let nextId = 0;
const newId = () => `e${++nextId}`;

/** A tool_use block, reduced to one readable line. */
function summariseTool(name: string, input: Record<string, unknown>): string {
  const pick = (key: string) => (typeof input[key] === "string" ? (input[key] as string) : "");
  switch (name) {
    case "Read":
    case "Write":
    case "Edit":
      return pick("file_path").split(/[\\/]/).pop() ?? "";
    case "Bash":
      return pick("command").slice(0, 120);
    case "Glob":
    case "Grep":
      return pick("pattern");
    default: {
      const first = Object.values(input).find((v) => typeof v === "string") as string | undefined;
      return (first ?? "").slice(0, 120);
    }
  }
}

export function ChatPanel({ project }: { project: string | null }) {
  const [entries, setEntries] = useState<ChatEntry[]>([]);
  const [draft, setDraft] = useState("");
  const [busy, setBusy] = useState(false);
  const abortRef = useRef<AbortController | null>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  const textareaRef = useRef<HTMLTextAreaElement>(null);

  // Switching projects clears the visible transcript but deliberately leaves the server
  // session alone: each project keeps its own, so coming back still has context.
  useEffect(() => {
    abortRef.current?.abort();
    setEntries([]);
    setBusy(false);
  }, [project]);

  useEffect(() => {
    const element = scrollRef.current;
    if (element) element.scrollTop = element.scrollHeight;
  }, [entries]);

  const push = (entry: Omit<ChatEntry, "id">) => {
    const withId = { ...entry, id: newId() };
    setEntries((previous) => [...previous, withId]);
    return withId.id;
  };

  const send = async (text: string) => {
    if (!project || !text.trim() || busy) return;

    push({ role: "user", text: text.trim() });
    setDraft("");
    setBusy(true);

    const controller = new AbortController();
    abortRef.current = controller;

    try {
      for await (const event of chat(project, text.trim(), controller.signal)) {
        const type = event.type as string;

        if (type === "assistant") {
          const message = event.message as { content?: { type: string; [k: string]: unknown }[] };
          for (const block of message?.content ?? []) {
            if (block.type === "text" && typeof block.text === "string" && block.text.trim()) {
              push({ role: "assistant", text: block.text });
            } else if (block.type === "tool_use") {
              const name = String(block.name ?? "tool");
              push({
                role: "tool",
                tool: name,
                text: summariseTool(name, (block.input ?? {}) as Record<string, unknown>),
              });
            }
          }
        } else if (type === "result") {
          if (event.is_error) {
            push({ role: "error", text: String(event.result ?? "The agent reported an error.") });
          }
        } else if (type === "raw") {
          push({ role: "system", text: String(event.text ?? "") });
        } else if (type === "error") {
          push({ role: "error", text: String(event.message ?? "Unknown error.") });
        }
      }
    } catch (error) {
      if (!controller.signal.aborted) {
        push({ role: "error", text: error instanceof Error ? error.message : String(error) });
      }
    } finally {
      setBusy(false);
      abortRef.current = null;
    }
  };

  const stop = () => {
    abortRef.current?.abort();
    if (project) void api.stopChat(project);
    setBusy(false);
  };

  /**
   * Clearing the transcript on its own is not enough. The backend resumes a per-project
   * session id on every turn, so the agent would still remember everything that had
   * scrolled out of view -- which is worse than not offering the button at all.
   */
  const startNewConversation = async () => {
    abortRef.current?.abort();
    if (project) {
      try {
        await api.resetChat(project);
      } catch (error) {
        console.error(error);
      }
    }
    setEntries([]);
    setBusy(false);
  };

  const onKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      void send(draft);
    }
  };

  return (
    <Pane
      title="Agent"
      subtitle={busy ? "working…" : undefined}
      actions={
        <>
          {busy && (
            <IconButton icon={<CircleStop size={14} />} label="Stop" onClick={stop} tone="bad" />
          )}
          <IconButton
            icon={<MessageSquarePlus size={14} />}
            label="New conversation (forgets the current context)"
            onClick={() => void startNewConversation()}
            disabled={busy || entries.length === 0}
          />
        </>
      }
      bodyClassName="flex min-h-0 flex-col"
    >
      <div ref={scrollRef} className="min-h-0 flex-1 overflow-y-auto">
        {entries.length === 0 ? (
          <Empty icon={<Bot size={20} />}>
            {project
              ? "Describe a change and the agent will edit this wireframe, then screenshot it."
              : "Pick a wireframe on the left."}
          </Empty>
        ) : (
          <ul className="space-y-2 p-3">
            {entries.map((entry) => (
              <li key={entry.id}>
                <Entry entry={entry} />
              </li>
            ))}
            {busy && (
              <li className="flex items-center gap-2 px-1 text-[11px] text-body-faint">
                <span className="size-1.5 animate-pulse rounded-full bg-brand" />
                thinking…
              </li>
            )}
          </ul>
        )}
      </div>

      <div className="shrink-0 border-t border-edge p-2">
        <div
          className="flex items-end gap-1.5 rounded border border-edge bg-shell-sunken px-2 py-1.5
                     focus-within:border-brand"
        >
          <textarea
            ref={textareaRef}
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            onKeyDown={onKeyDown}
            disabled={!project || busy}
            rows={2}
            placeholder={project ? "Describe a change…  (Enter to send)" : "No wireframe selected"}
            className="min-h-0 flex-1 resize-none bg-transparent text-[12.5px] leading-relaxed
                       text-body placeholder:text-body-faint focus:outline-none disabled:opacity-50"
          />
          <button
            type="button"
            onClick={() => void send(draft)}
            disabled={!project || busy || !draft.trim()}
            title="Send"
            className="mb-0.5 inline-flex size-6 shrink-0 items-center justify-center rounded
                       bg-brand text-shell transition-opacity hover:brightness-110
                       disabled:cursor-not-allowed disabled:opacity-30"
          >
            <ArrowUp size={14} />
          </button>
        </div>
        <p className="mt-1 px-1 text-[10px] leading-relaxed text-body-faint">
          Runs the Claude CLI in this project, limited to editing <span className="font-mono">src/</span>{" "}
          and taking screenshots.
        </p>
      </div>
    </Pane>
  );
}

function Entry({ entry }: { entry: ChatEntry }) {
  if (entry.role === "tool") {
    return (
      <div className="flex items-baseline gap-1.5 px-1 font-mono text-[11px] text-body-faint">
        <Wrench size={11} className="mt-0.5 shrink-0" />
        <span className="shrink-0 text-body-muted">{entry.tool}</span>
        <span className="truncate">{entry.text}</span>
      </div>
    );
  }

  if (entry.role === "error") {
    return (
      <div className="flex gap-2 rounded border border-bad/40 bg-bad/10 px-2.5 py-2 text-[12px] text-bad">
        <TriangleAlert size={13} className="mt-0.5 shrink-0" />
        <span className="whitespace-pre-wrap">{entry.text}</span>
      </div>
    );
  }

  if (entry.role === "system") {
    return (
      <pre className="overflow-x-auto rounded bg-shell-sunken px-2.5 py-1.5 font-mono text-[11px]
                      whitespace-pre-wrap text-body-faint">
        {entry.text}
      </pre>
    );
  }

  const isUser = entry.role === "user";
  return (
    <div className={cx("flex gap-2", isUser && "flex-row-reverse")}>
      <div
        className={cx(
          "mt-0.5 flex size-5 shrink-0 items-center justify-center rounded-full",
          isUser ? "bg-brand/20 text-brand" : "bg-shell-raised text-body-muted"
        )}
      >
        {isUser ? <User size={11} /> : <Terminal size={11} />}
      </div>
      <div
        className={cx(
          "max-w-[85%] rounded px-2.5 py-1.5 text-[12.5px] leading-relaxed whitespace-pre-wrap",
          isUser ? "bg-brand/15 text-body" : "bg-shell-raised text-body"
        )}
      >
        {entry.text}
      </div>
    </div>
  );
}
