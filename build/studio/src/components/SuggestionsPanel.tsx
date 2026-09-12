import { useEffect, useState } from "react";
import { Check, Copy, Download, Lightbulb, Link2, Maximize2, Trash2 } from "lucide-react";
import { api, type Suggestion } from "../api";
import { Empty, IconButton, cx, formatAgo } from "./ui";

/**
 * What the agent thinks would make the next wireframe easier to build.
 *
 * It is the only thing that finds out, mid-build, that there is no Timeline component or
 * that a prop it wanted does not exist -- and that used to die with the session. It now
 * writes a page into <project>/suggestions/ and this shows it. A folder of HTML rather
 * than a command and a schema: the agent already knows how to write a page, and a format
 * it has to be taught is a format it gets wrong.
 */
export function SuggestionsPanel({
  project,
  suggestions,
  suggestionsVersion,
  onChanged,
}: {
  project: string | null;
  suggestions: Suggestion[];
  /** Bumped when the watcher reports a change, so the iframe reloads rather than cache. */
  suggestionsVersion: number;
  onChanged: () => void;
}) {
  const [openName, setOpenName] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  // Which of the two copy buttons last fired, so only that one shows the tick.
  const [copied, setCopied] = useState<"source" | "path" | null>(null);

  // Selecting the newest by default means the tab is useful the moment it lights up.
  useEffect(() => {
    setOpenName((current) =>
      current && suggestions.some((s) => s.name === current) ? current : (suggestions[0]?.name ?? null)
    );
  }, [suggestions, project]);

  const open = suggestions.find((s) => s.name === openName) ?? null;

  useEffect(() => setCopied(null), [openName]);

  const copyText = async (what: "source" | "path", text: string) => {
    try {
      await navigator.clipboard.writeText(text);
      setCopied(what);
      setTimeout(() => setCopied(null), 1500);
    } catch (error) {
      console.error(error);
    }
  };

  // The page's source, not its rendered text: the useful thing to paste into an issue is
  // the document itself.
  const copySource = async () => {
    if (!project || !open) return;
    try {
      const response = await fetch(api.suggestionUrl(project, open.name, suggestionsVersion));
      await copyText("source", await response.text());
    } catch (error) {
      console.error(error);
    }
  };

  const remove = async (name: string) => {
    if (!project) return;
    setBusy(name);
    try {
      await api.deleteSuggestion(project, name);
      onChanged();
    } catch (error) {
      console.error(error);
    } finally {
      setBusy(null);
    }
  };

  if (!project || suggestions.length === 0) {
    return (
      <Empty icon={<Lightbulb size={20} />}>
        {project ? (
          <>
            Nothing yet. The agent writes a page into{" "}
            <span className="font-mono text-body-muted">suggestions/</span> when something
            would have made the build easier — a missing component or prop, or anything in{" "}
            <span className="font-mono text-body-muted">agent-readme</span> or its session
            instructions that was wrong, missing or misleading.
          </>
        ) : (
          "Pick a wireframe on the left."
        )}
      </Empty>
    );
  }

  return (
    <div className="flex h-full min-h-0">
      {/* A list beside the page, not a grid of cards: these are read one at a time, and
          the pane is short. */}
      <ul className="w-56 shrink-0 overflow-y-auto border-r border-edge py-1">
        {suggestions.map((suggestion) => {
          const active = suggestion.name === openName;
          return (
            <li key={suggestion.name} className="group/row relative">
              <button
                type="button"
                onClick={() => setOpenName(suggestion.name)}
                className={cx(
                  "flex w-full flex-col gap-0.5 border-l-2 px-2.5 py-1.5 pr-8 text-left transition-colors",
                  active
                    ? "border-brand bg-shell-raised"
                    : "border-transparent hover:bg-shell-raised/60"
                )}
              >
                <span
                  className={cx(
                    "line-clamp-2 text-[12px] leading-snug",
                    active ? "text-body" : "text-body-muted group-hover/row:text-body"
                  )}
                >
                  {suggestion.title}
                </span>
                <span className="text-[10px] text-body-faint">{formatAgo(suggestion.modified)}</span>
              </button>

              <button
                type="button"
                title={`Delete ${suggestion.name}`}
                aria-label={`Delete ${suggestion.name}`}
                disabled={busy === suggestion.name}
                onClick={() => void remove(suggestion.name)}
                className="absolute top-1.5 right-1.5 hidden size-6 items-center justify-center
                           rounded text-body-faint hover:bg-bad/20 hover:text-bad
                           group-hover/row:flex focus-visible:flex disabled:opacity-40"
              >
                <Trash2 size={12} />
              </button>
            </li>
          );
        })}
      </ul>

      <div className="flex min-h-0 min-w-0 flex-1 flex-col">
        {open && (
          <div className="flex h-8 shrink-0 items-center gap-2 border-b border-edge px-2.5">
            <span className="truncate font-mono text-[11px] text-body-faint" title={open.path}>
              {open.path}
            </span>
            <a
              href={api.suggestionUrl(project, open.name, suggestionsVersion)}
              target="_blank"
              rel="noreferrer"
              title="Open full size in a new tab"
              className="ml-auto inline-flex h-6 items-center rounded px-1.5 text-body-muted
                         hover:bg-shell-raised hover:text-body"
            >
              <Maximize2 size={13} />
            </a>
            <IconButton
              icon={
                copied === "path" ? <Check size={13} className="text-good" /> : <Link2 size={13} />
              }
              label={copied === "path" ? "Copied" : `Copy the file path — ${open.path}`}
              onClick={() => void copyText("path", open.path)}
            />
            <IconButton
              icon={
                copied === "source" ? <Check size={13} className="text-good" /> : <Copy size={13} />
              }
              label={copied === "source" ? "Copied" : "Copy the page source to the clipboard"}
              onClick={() => void copySource()}
            />
            <a
              href={api.suggestionUrl(project, open.name, suggestionsVersion)}
              download={open.name}
              title={`Save ${open.name}`}
              className="inline-flex h-6 items-center rounded px-1.5 text-body-muted
                         hover:bg-shell-raised hover:text-body"
            >
              <Download size={13} />
            </a>
            <IconButton
              icon={<Trash2 size={13} />}
              label="Delete this suggestion"
              onClick={() => void remove(open.name)}
              disabled={busy === open.name}
            />
          </div>
        )}

        {/* Sandboxed without allow-same-origin: this HTML was written by the agent, and
            the Studio page it would otherwise share an origin with can start sessions and
            delete wireframes. Scripts still run, in a throwaway origin of their own. */}
        <iframe
          key={`${open?.name}-${suggestionsVersion}`}
          src={open ? api.suggestionUrl(project, open.name, suggestionsVersion) : undefined}
          title={open?.title ?? "Suggestion"}
          sandbox="allow-scripts"
          className="min-h-0 flex-1 border-0 bg-white"
        />
      </div>
    </div>
  );
}
