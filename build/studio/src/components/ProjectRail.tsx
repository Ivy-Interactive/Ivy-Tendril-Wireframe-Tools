import { FolderOpen, Image, Layers, RefreshCw } from "lucide-react";
import type { ProjectSummary } from "../api";
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
  return (
    <Pane
      title="Wireframes"
      subtitle={projects.length ? `${projects.length}` : undefined}
      actions={<IconButton icon={<RefreshCw size={14} />} label="Rescan" onClick={onRefresh} />}
      bodyClassName="overflow-y-auto"
    >
      {projects.length === 0 ? (
        <Empty icon={<FolderOpen size={20} />}>
          No wireframe projects under <span className="font-mono">{root}</span>. Create one with{" "}
          <span className="font-mono text-body-muted">wireframe setup &lt;path&gt;</span>.
        </Empty>
      ) : (
        <ul className="py-1">
          {projects.map((project) => {
            const active = project.name === selected;
            return (
              <li key={project.path}>
                <button
                  type="button"
                  onClick={() => onSelect(project.name)}
                  className={cx(
                    "group flex w-full flex-col gap-0.5 border-l-2 px-3 py-2 text-left transition-colors",
                    active
                      ? "border-brand bg-shell-raised"
                      : "border-transparent hover:bg-shell-raised/60"
                  )}
                >
                  <span
                    className={cx(
                      "truncate text-[13px] font-medium",
                      active ? "text-body" : "text-body-muted group-hover:text-body"
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
              </li>
            );
          })}
        </ul>
      )}
    </Pane>
  );
}
