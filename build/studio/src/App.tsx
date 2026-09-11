import { useCallback, useEffect, useRef, useState } from "react";
import { Panel, PanelGroup } from "react-resizable-panels";
import { Monitor, Moon, PencilRuler, Sun } from "lucide-react";
import {
  api,
  subscribe,
  type PreviewStatus,
  type ProjectSummary,
  type Shot,
  type SourceFile,
} from "./api";
import { TerminalPanel } from "./components/TerminalPanel";
import { CodePanel } from "./components/CodePanel";
import { PreviewPanel } from "./components/PreviewPanel";
import { ProjectRail } from "./components/ProjectRail";
import { ShotsPanel } from "./components/ShotsPanel";
import { HHandle, IconButton, VHandle, cx } from "./components/ui";
import { useTheme } from "./theme";

// The server stamps this into index.html; it is the directory Studio is scanning.
const STUDIO_ROOT: string =
  (globalThis as unknown as { __STUDIO_ROOT__?: string }).__STUDIO_ROOT__ ?? "";

type BottomTab = "code" | "shots";

export default function App() {
  const [projects, setProjects] = useState<ProjectSummary[]>([]);
  const [selected, setSelected] = useState<string | null>(null);

  const [files, setFiles] = useState<SourceFile[]>([]);
  const [filesVersion, setFilesVersion] = useState(0);

  const [shots, setShots] = useState<Shot[]>([]);
  const [shotsVersion, setShotsVersion] = useState(0);

  const [preview, setPreview] = useState<PreviewStatus | null>(null);
  const [tab, setTab] = useState<BottomTab>("code");

  const theme = useTheme();

  // Read inside event handlers, which would otherwise close over a stale value.
  const selectedRef = useRef<string | null>(null);
  selectedRef.current = selected;

  const loadProjects = useCallback(async () => {
    try {
      const list = await api.projects();
      setProjects(list);
      setSelected((current) =>
        current && list.some((p) => p.name === current) ? current : (list[0]?.name ?? null)
      );
    } catch (error) {
      console.error(error);
    }
  }, []);

  useEffect(() => {
    void loadProjects();
  }, [loadProjects]);

  const loadFiles = useCallback(async (project: string) => {
    try {
      setFiles(await api.files(project));
      setFilesVersion((v) => v + 1);
    } catch (error) {
      console.error(error);
    }
  }, []);

  const loadShots = useCallback(async (project: string) => {
    try {
      setShots(await api.shots(project));
      setShotsVersion((v) => v + 1);
    } catch (error) {
      console.error(error);
    }
  }, []);

  // Selecting a project loads its detail and points the single preview server at it.
  useEffect(() => {
    if (!selected) {
      setFiles([]);
      setShots([]);
      setPreview(null);
      return;
    }
    void loadFiles(selected);
    void loadShots(selected);
    setPreview({ phase: "building", url: null, generation: 0, message: null });
    api.openPreview(selected).then(setPreview).catch(console.error);
  }, [selected, loadFiles, loadShots]);

  // One SSE stream drives every refresh, so nothing polls.
  useEffect(
    () =>
      subscribe((event) => {
        const current = selectedRef.current;
        switch (event.type) {
          case "projects":
            void loadProjects();
            break;
          case "preview":
            if (event.project === current) setPreview(event.status);
            break;
          case "files":
            if (event.project === current) void loadFiles(event.project);
            break;
          case "shots":
            if (event.project === current) void loadShots(event.project);
            break;
        }
      }),
    [loadProjects, loadFiles, loadShots]
  );

  const selectedProject = projects.find((p) => p.name === selected) ?? null;

  return (
    <div className="flex h-full flex-col">
      <header className="flex h-10 shrink-0 items-center gap-2.5 border-b border-edge bg-shell-sunken px-3">
        <PencilRuler size={15} className="text-brand" />
        <span className="text-[13px] font-semibold text-body">Wireframe Studio</span>
        {selectedProject && (
          <span className="truncate font-mono text-[11px] text-body-faint">
            {selectedProject.path}
          </span>
        )}
        <span className="ml-auto truncate font-mono text-[11px] text-body-faint">
          {STUDIO_ROOT}
        </span>
        <IconButton
          icon={
            theme.choice === "system" ? (
              <Monitor size={14} />
            ) : theme.choice === "light" ? (
              <Sun size={14} />
            ) : (
              <Moon size={14} />
            )
          }
          label={
            theme.choice === "system"
              ? `Theme: following the system (${theme.resolved}) — click for light`
              : theme.choice === "light"
                ? "Theme: light — click for dark"
                : "Theme: dark — click to follow the system"
          }
          onClick={theme.cycle}
        />
      </header>

      <PanelGroup direction="horizontal" autoSaveId="studio-h" className="min-h-0 flex-1">
        <Panel defaultSize={16} minSize={10} maxSize={28}>
          <ProjectRail
            projects={projects}
            selected={selected}
            onSelect={setSelected}
            onRefresh={() => void loadProjects()}
            root={STUDIO_ROOT}
          />
        </Panel>
        <VHandle />

        <Panel defaultSize={26} minSize={16} maxSize={44}>
          <TerminalPanel project={selected} themeKey={theme.resolved} />
        </Panel>
        <VHandle />

        <Panel defaultSize={58} minSize={30}>
          <PanelGroup direction="vertical" autoSaveId="studio-v" className="h-full">
            <Panel defaultSize={62} minSize={20}>
              <PreviewPanel
                project={selected}
                status={preview}
                onCaptured={() => {
                  if (selected) void loadShots(selected);
                  setTab("shots");
                }}
              />
            </Panel>
            <HHandle />
            <Panel defaultSize={38} minSize={12}>
              <div className="flex h-full min-h-0 flex-col">
                <nav className="flex h-9 shrink-0 items-center gap-px border-b border-edge px-2">
                  {(
                    [
                      ["code", "Code", files.length],
                      ["shots", "Screenshots", shots.length],
                    ] as const
                  ).map(([key, label, count]) => (
                    <button
                      key={key}
                      type="button"
                      onClick={() => setTab(key)}
                      className={cx(
                        "inline-flex items-center gap-1.5 rounded px-2.5 py-1 text-[11px]",
                        "font-semibold tracking-wider uppercase transition-colors",
                        tab === key
                          ? "bg-shell-raised text-body"
                          : "text-body-faint hover:text-body-muted"
                      )}
                    >
                      {label}
                      {count > 0 && (
                        <span className="font-mono text-[10px] font-normal tracking-normal opacity-60">
                          {count}
                        </span>
                      )}
                    </button>
                  ))}
                </nav>
                <div className="min-h-0 flex-1">
                  {/* Both stay mounted: remounting CodeMirror on every tab switch would
                      drop scroll position, undo history and any unsaved edit. */}
                  <div className={cx("h-full", tab !== "code" && "hidden")}>
                    <CodePanel project={selected} files={files} filesVersion={filesVersion} />
                  </div>
                  <div className={cx("h-full", tab !== "shots" && "hidden")}>
                    <ShotsPanel project={selected} shots={shots} shotsVersion={shotsVersion} />
                  </div>
                </div>
              </div>
            </Panel>
          </PanelGroup>
        </Panel>
      </PanelGroup>
    </div>
  );
}
