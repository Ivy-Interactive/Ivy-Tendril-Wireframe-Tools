import { useEffect, useRef, useState } from "react";
import {
  FolderOpen,
  Image,
  Layers,
  Plus,
  RefreshCw,
  SquareArrowOutUpRight,
  Trash2,
  TriangleAlert,
} from "lucide-react";
import { api, type ProjectSummary } from "../api";
import { Empty, IconButton, Pane, cx, formatAgo } from "./ui";

export function ProjectRail({
  projects,
  selected,
  onSelect,
  onRefresh,
  root,
}: {
  projects: ProjectSummary[];
  selected: string | null;
  onSelect: (name: string) => void;
  onRefresh: () => void;
  root: string;
}) {
  // Two-step delete: the row turns into a confirm strip rather than firing a
  // window.confirm(), which is easy to dismiss by reflex.
  const [confirming, setConfirming] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const [creating, setCreating] = useState(false);
  const [newName, setNewName] = useState("");
  const nameRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (creating) nameRef.current?.focus();
  }, [creating]);

  const create = async () => {
    const name = newName.trim();
    if (!name) return;

    setBusy("<new>");
    setError(null);
    try {
      const project = await api.createProject(name);
      setCreating(false);
      setNewName("");
      onRefresh();
      // Select it straight away: creating one and then having to find it in the rail is
      // an obvious missing step.
      onSelect(project.name);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(null);
    }
  };

  const remove = async (name: string) => {
    setBusy(name);
    setError(null);
    try {
      await api.deleteProject(name);
      setConfirming(null);
      onRefresh();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(null);
    }
  };

  return (
    <Pane
      title="Wireframes"
      subtitle={projects.length ? `${projects.length}` : undefined}
      actions={
        <>
          <IconButton
            icon={<Plus size={15} />}
            label="New wireframe"
            onClick={() => {
              setError(null);
              setCreating(true);
            }}
            disabled={creating}
          />
          <IconButton
            icon={<SquareArrowOutUpRight size={14} />}
            label="Open the selected project folder in VS Code"
            onClick={() => selected && void api.openInEditor(selected).catch(console.error)}
            disabled={!selected}
          />
          <IconButton icon={<RefreshCw size={14} />} label="Rescan" onClick={onRefresh} />
        </>
      }
      bodyClassName="overflow-y-auto"
    >
      {error && (
        <div className="flex items-start gap-2 border-b border-edge bg-bad/10 px-3 py-2
                        text-[11.5px] leading-relaxed text-bad">
          <TriangleAlert size={13} className="mt-0.5 shrink-0" />
          <span className="flex-1">{error}</span>
          <button
            type="button"
            onClick={() => setError(null)}
            className="shrink-0 text-bad/70 hover:text-bad"
          >
            dismiss
          </button>
        </div>
      )}

      {creating && (
        <div className="border-b border-edge bg-shell-raised px-3 py-2">
          <input
            ref={nameRef}
            value={newName}
            onChange={(e) => setNewName(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter") void create();
              if (e.key === "Escape") {
                setCreating(false);
                setNewName("");
              }
            }}
            placeholder="wireframe name"
            disabled={busy === "<new>"}
            className="w-full rounded border border-edge bg-shell px-2 py-1 text-[12.5px]
                       text-body placeholder:text-body-faint focus:border-brand focus:outline-none
                       disabled:opacity-50"
          />
          <div className="mt-1.5 flex items-center gap-1.5">
            <button
              type="button"
              onClick={() => void create()}
              disabled={!newName.trim() || busy === "<new>"}
              className="rounded bg-brand px-2 py-1 text-[11px] font-medium text-shell
                         hover:brightness-110 disabled:opacity-40"
            >
              {busy === "<new>" ? "Creating…" : "Create"}
            </button>
            <button
              type="button"
              onClick={() => {
                setCreating(false);
                setNewName("");
              }}
              className="rounded border border-edge px-2 py-1 text-[11px] text-body-muted
                         hover:border-edge-bright hover:text-body"
            >
              Cancel
            </button>
            <span className="ml-auto text-[10px] text-body-faint">Enter to create</span>
          </div>
        </div>
      )}

      {projects.length === 0 && !creating ? (
        <Empty icon={<FolderOpen size={20} />}>
          No wireframes under <span className="font-mono">{root}</span> yet. Use the{" "}
          <span className="text-body-muted">+</span> button above, or run{" "}
          <span className="font-mono text-body-muted">wireframe setup &lt;path&gt;</span>.
        </Empty>
      ) : projects.length === 0 ? null : (
        <ul className="py-1">
          {projects.map((project) => {
            const active = project.name === selected;
            const isConfirming = confirming === project.name;
            const isBusy = busy === project.name;

            if (isConfirming) {
              return (
                <li
                  key={project.path}
                  className="border-l-2 border-bad bg-bad/10 px-3 py-2 text-[11.5px]"
                >
                  <p className="leading-relaxed text-body">
                    Delete <span className="font-medium">{project.name}</span>?
                  </p>
                  <p className="mt-0.5 leading-relaxed text-body-faint">
                    {project.fileCount} source {project.fileCount === 1 ? "file" : "files"} and{" "}
                    {project.screenshotCount}{" "}
                    {project.screenshotCount === 1 ? "screenshot" : "screenshots"} move to{" "}
                    <span className="font-mono">.trash/</span>, so you can still get it back.
                  </p>
                  <div className="mt-2 flex gap-1.5">
                    <button
                      type="button"
                      onClick={() => void remove(project.name)}
                      disabled={isBusy}
                      className="rounded bg-bad px-2 py-1 text-[11px] font-medium text-shell
                                 hover:brightness-110 disabled:opacity-50"
                    >
                      {isBusy ? "Deleting…" : "Delete"}
                    </button>
                    <button
                      type="button"
                      onClick={() => setConfirming(null)}
                      disabled={isBusy}
                      className="rounded border border-edge px-2 py-1 text-[11px] text-body-muted
                                 hover:border-edge-bright hover:text-body disabled:opacity-50"
                    >
                      Cancel
                    </button>
                  </div>
                </li>
              );
            }

            return (
              <li key={project.path} className="group/row relative">
                <button
                  type="button"
                  onClick={() => onSelect(project.name)}
                  className={cx(
                    "flex w-full flex-col gap-0.5 border-l-2 px-3 py-2 pr-9 text-left transition-colors",
                    active
                      ? "border-brand bg-shell-raised"
                      : "border-transparent hover:bg-shell-raised/60"
                  )}
                >
                  <span
                    className={cx(
                      "truncate text-[13px] font-medium",
                      active ? "text-body" : "text-body-muted group-hover/row:text-body"
                    )}
                  >
                    {project.name}
                  </span>
                  <span className="flex items-center gap-2.5 text-[11px] text-body-faint">
                    <span className="inline-flex items-center gap-1">
                      <Layers size={11} />
                      {project.fileCount}
                    </span>
                    <span className="inline-flex items-center gap-1">
                      <Image size={11} />
                      {project.screenshotCount}
                    </span>
                    <span className="ml-auto">{formatAgo(project.modified)}</span>
                  </span>
                </button>

                {/* Outside the row button so it is not a nested <button>. Hidden until
                    hover or focus, so a rail of projects is not a wall of trash icons. */}
                <button
                  type="button"
                  title={`Delete ${project.name}`}
                  aria-label={`Delete ${project.name}`}
                  onClick={() => {
                    setError(null);
                    setConfirming(project.name);
                  }}
                  className="absolute top-2 right-2 hidden size-6 items-center justify-center rounded
                             text-body-faint hover:bg-bad/20 hover:text-bad
                             group-hover/row:flex focus-visible:flex"
                >
                  <Trash2 size={13} />
                </button>
              </li>
            );
          })}
        </ul>
      )}
    </Pane>
  );
}
