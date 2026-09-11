import { useEffect, useMemo, useRef, useState } from "react";
import {
  Camera,
  ExternalLink,
  Maximize2,
  MonitorSmartphone,
  RefreshCw,
  TriangleAlert,
} from "lucide-react";
import type { PreviewStatus } from "../api";
import { api } from "../api";
import { Button, Dot, Empty, IconButton, Pane, cx } from "./ui";

/** Viewports worth having one click away; 1440x900 is what `screenshot` defaults to. */
const VIEWPORTS = [
  { label: "1440 × 900", width: 1440, height: 900 },
  { label: "1440 × 860", width: 1440, height: 860 },
  { label: "1280 × 800", width: 1280, height: 800 },
  { label: "1920 × 1080", width: 1920, height: 1080 },
  { label: "420 × 900", width: 420, height: 900 },
  { label: "Fit", width: 0, height: 0 },
];

function describe(status: PreviewStatus): { text: string; tone: "good" | "warn" | "bad" | "idle" } {
  switch (status.phase) {
    case "running":
      return { text: status.url ?? "running", tone: "good" };
    case "building":
      return { text: "bundling…", tone: "warn" };
    case "starting":
      return { text: "starting preview…", tone: "warn" };
    case "failed":
      return { text: "build failed", tone: "bad" };
    default:
      return { text: "no preview", tone: "idle" };
  }
}

export function PreviewPanel({
  project,
  status,
  onCaptured,
}: {
  project: string | null;
  status: PreviewStatus | null;
  onCaptured?: () => void;
}) {
  const [viewport, setViewport] = useState(VIEWPORTS[0]);
  const [capturing, setCapturing] = useState(false);
  const [scale, setScale] = useState(1);
  const frameRef = useRef<HTMLDivElement>(null);

  const fit = viewport.width === 0;

  // Scale the iframe down to fit the pane rather than clipping it, so a 1920-wide
  // wireframe is still reviewable in a 700px panel. The iframe keeps its real pixel
  // dimensions, so the layout inside is exactly what a screenshot would capture.
  useEffect(() => {
    if (fit) {
      setScale(1);
      return;
    }
    const element = frameRef.current;
    if (!element) return;

    const measure = () => {
      const { width, height } = element.getBoundingClientRect();
      if (!width || !height) return;
      setScale(Math.min(1, (width - 24) / viewport.width, (height - 24) / viewport.height));
    };

    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    return () => observer.disconnect();
    // status.phase matters: the container only exists once the preview is running, so an
    // effect that does not depend on it runs against a null ref and never measures.
  }, [fit, viewport.width, viewport.height, status?.phase, status?.generation]);

  const { text, tone } = useMemo(
    () => (status ? describe(status) : { text: "no preview", tone: "idle" as const }),
    [status]
  );

  const capture = async () => {
    if (!project) return;
    setCapturing(true);
    try {
      const size = fit ? VIEWPORTS[0] : viewport;
      await api.capture(project, size.width, size.height);
      onCaptured?.();
    } catch (error) {
      console.error(error);
    } finally {
      setCapturing(false);
    }
  };

  const actions = (
    <>
      <select
        aria-label="Viewport"
        value={viewport.label}
        onChange={(e) =>
          setViewport(VIEWPORTS.find((v) => v.label === e.target.value) ?? VIEWPORTS[0])
        }
        className="h-7 rounded border border-edge bg-shell px-1.5 text-xs text-body-muted
                   hover:border-edge-bright focus:outline-none"
      >
        {VIEWPORTS.map((v) => (
          <option key={v.label} value={v.label}>
            {v.label}
          </option>
        ))}
      </select>
      <IconButton
        icon={<Camera size={14} />}
        label={capturing ? "Capturing…" : "Screenshot this viewport"}
        onClick={capture}
        disabled={!project || capturing || status?.phase !== "running"}
      />
      <IconButton
        icon={<RefreshCw size={14} className={status?.phase === "building" ? "animate-spin" : ""} />}
        label="Rebuild"
        onClick={() => project && api.rebuild(project)}
        disabled={!project || status?.phase === "building" || status?.phase === "starting"}
      />
      <IconButton
        icon={<ExternalLink size={14} />}
        label="Open in a browser tab"
        onClick={() => status?.url && window.open(status.url, "_blank")}
        disabled={!status?.url}
      />
    </>
  );

  let body: React.ReactNode;

  if (!project) {
    body = <Empty icon={<MonitorSmartphone size={20} />}>Pick a wireframe on the left.</Empty>;
  } else if (status?.phase === "failed") {
    body = (
      <div className="flex h-full flex-col">
        <div className="flex items-center gap-2 border-b border-edge bg-bad/10 px-3 py-2 text-xs text-bad">
          <TriangleAlert size={14} />
          <span className="font-medium">The bundle did not build.</span>
          <span className="text-body-faint">The last working version is still shown below.</span>
        </div>
        <pre className="min-h-0 flex-1 overflow-auto bg-shell-sunken p-3 font-mono text-[11.5px]
                        leading-relaxed whitespace-pre-wrap text-body-muted">
          {status.message ?? "No output."}
        </pre>
      </div>
    );
  } else if (status?.phase === "running" && status.url) {
    body = (
      <div
        ref={frameRef}
        className="flex h-full items-center justify-center overflow-auto bg-shell-sunken p-3"
      >
        {/* Two boxes on purpose. A CSS transform does not change an element's layout
            size, so scaling the iframe directly would leave it occupying its full
            unscaled footprint and spilling out of the pane. The outer box is sized to
            the SCALED dimensions and owns the layout; the inner one is the real
            viewport being shrunk into it from its top-left corner. */}
        <div
          className="shrink-0 overflow-hidden rounded bg-white shadow-2xl ring-1 ring-edge-bright"
          style={
            fit
              ? { width: "100%", height: "100%" }
              : {
                  width: Math.round(viewport.width * scale),
                  height: Math.round(viewport.height * scale),
                }
          }
        >
          <div
            style={
              fit
                ? { width: "100%", height: "100%" }
                : {
                    width: viewport.width,
                    height: viewport.height,
                    transform: `scale(${scale})`,
                    transformOrigin: "top left",
                  }
            }
          >
            {/* `key` is the generation counter: a restarted preview must remount, or the
                browser keeps painting the dead process's last frame. */}
            <iframe
              key={status.generation}
              src={status.url}
              title="Wireframe preview"
              className="size-full border-0"
              sandbox="allow-scripts allow-same-origin allow-forms allow-popups"
            />
          </div>
        </div>
      </div>
    );
  } else {
    body = (
      <Empty icon={<Maximize2 size={20} />}>
        {status?.phase === "building" || status?.phase === "starting"
          ? "Bundling the wireframe…"
          : "Starting the preview server…"}
      </Empty>
    );
  }

  return (
    <Pane
      title="Live app"
      subtitle={
        <span className="inline-flex items-center gap-1.5">
          <Dot tone={tone} />
          <span className={cx(tone === "bad" && "text-bad")}>{text}</span>
          {!fit && scale < 0.999 && status?.phase === "running" && (
            <span className="text-body-faint">· {Math.round(scale * 100)}%</span>
          )}
        </span>
      }
      actions={actions}
      bodyClassName="min-h-0"
    >
      {body}
    </Pane>
  );
}
