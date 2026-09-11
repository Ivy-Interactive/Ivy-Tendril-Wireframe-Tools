import { useEffect, useState } from "react";
import { Download, Image as ImageIcon, X } from "lucide-react";
import { api, type Shot } from "../api";
import { Empty, IconButton, cx, formatAgo, formatBytes } from "./ui";

export function ShotsPanel({
  project,
  shots,
  shotsVersion,
}: {
  project: string | null;
  shots: Shot[];
  /** Bumped when a capture lands, so the <img> cache busts. */
  shotsVersion: number;
}) {
  const [zoomed, setZoomed] = useState<Shot | null>(null);

  // Close the lightbox when switching projects, or it shows a stale image.
  useEffect(() => setZoomed(null), [project]);

  useEffect(() => {
    if (!zoomed) return;
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && setZoomed(null);
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [zoomed]);

  return (
    <div className="h-full overflow-y-auto">
      {!project || shots.length === 0 ? (
        <Empty icon={<ImageIcon size={20} />}>
          {project
            ? "No screenshots yet. Use the camera button above the live app."
            : "Pick a wireframe on the left."}
        </Empty>
      ) : (
        <ul className="grid grid-cols-2 gap-2 p-2 xl:grid-cols-3">
          {shots.map((shot) => (
            <li key={shot.name}>
              <button
                type="button"
                onClick={() => setZoomed(shot)}
                className="group block w-full overflow-hidden rounded border border-edge
                           bg-shell-sunken text-left transition-colors hover:border-brand"
              >
                <div
                  className="relative overflow-hidden bg-white"
                  style={{ aspectRatio: `${shot.width} / ${shot.height}` }}
                >
                  <img
                    src={api.shotUrl(project, shot.name, `${shotsVersion}-${shot.modified}`)}
                    alt={shot.name}
                    loading="lazy"
                    className="size-full object-cover object-top"
                  />
                </div>
                <div className="flex items-baseline gap-1.5 px-2 py-1.5">
                  <span className="truncate font-mono text-[11px] text-body-muted group-hover:text-body">
                    {shot.name.replace(/\.png$/, "")}
                  </span>
                  <span className="ml-auto shrink-0 text-[10px] text-body-faint">
                    {formatBytes(shot.bytes)}
                  </span>
                </div>
              </button>
            </li>
          ))}
        </ul>
      )}

      {zoomed && project && (
        <div
          className="fixed inset-0 z-50 flex flex-col bg-shell-sunken/95 backdrop-blur-sm"
          onClick={() => setZoomed(null)}
        >
          <header
            className="flex h-10 shrink-0 items-center gap-3 border-b border-edge px-4"
            onClick={(e) => e.stopPropagation()}
          >
            <span className="font-mono text-xs text-body">{zoomed.name}</span>
            <span className="text-[11px] text-body-faint">
              {zoomed.width} × {zoomed.height} CSS · {formatBytes(zoomed.bytes)} ·{" "}
              {formatAgo(zoomed.modified)}
            </span>
            <div className="ml-auto flex items-center gap-1">
              <a
                href={api.shotUrl(project, zoomed.name)}
                download={zoomed.name}
                title="Download"
                className="inline-flex h-7 items-center rounded px-2 text-body-muted
                           hover:bg-shell-raised hover:text-body"
              >
                <Download size={14} />
              </a>
              <IconButton icon={<X size={16} />} label="Close" onClick={() => setZoomed(null)} />
            </div>
          </header>
          <div className="min-h-0 flex-1 overflow-auto p-4">
            <img
              src={api.shotUrl(project, zoomed.name, `${shotsVersion}-${zoomed.modified}`)}
              alt={zoomed.name}
              onClick={(e) => e.stopPropagation()}
              className="mx-auto max-w-full rounded bg-white shadow-2xl ring-1 ring-edge-bright"
            />
          </div>
        </div>
      )}
    </div>
  );
}
