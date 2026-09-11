// Thin client over the Studio backend. Every mutation is a plain fetch; everything the
// server pushes (preview status, file changes, screenshots appearing) arrives on one SSE
// stream, so the UI never polls.

export type PreviewPhase = "idle" | "building" | "starting" | "running" | "failed";

export interface PreviewStatus {
  phase: PreviewPhase;
  url: string | null;
  /** Bumped on every restart. Used as the iframe `key` so a killed process's last paint
   *  is never left on screen. */
  generation: number;
  message: string | null;
}

export interface ProjectSummary {
  name: string;
  path: string;
  fileCount: number;
  screenshotCount: number;
  modified: string;
}

export interface SourceFile {
  path: string;
  bytes: number;
}

export interface Shot {
  name: string;
  width: number;
  height: number;
  bytes: number;
  modified: string;
}

export type ChatRole = "user" | "assistant" | "tool" | "system" | "error";

export interface ChatEntry {
  id: string;
  role: ChatRole;
  text: string;
  /** For tool entries: the tool name, e.g. "Edit" or "Bash". */
  tool?: string;
  detail?: string;
  pending?: boolean;
}

async function json<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, init);
  if (!response.ok) {
    throw new Error(`${response.status} ${response.statusText}: ${await response.text()}`);
  }
  return response.json() as Promise<T>;
}

/**
 * Turns a failed response into something readable. ProblemDetails carries a `detail`;
 * a model-binding failure does not, and surfacing a bare "400" tells the user nothing.
 */
async function describeFailure(response: Response): Promise<string> {
  const body = await response.text().catch(() => "");

  if (body) {
    try {
      const parsed = JSON.parse(body) as { detail?: string; title?: string; errors?: unknown };
      if (parsed.detail) return parsed.detail;
      if (parsed.errors) return `${parsed.title ?? "Bad request"}: ${JSON.stringify(parsed.errors)}`;
      if (parsed.title) return parsed.title;
    } catch {
      return body.slice(0, 300);
    }
  }

  return `${response.status} ${response.statusText}`.trim();
}

export const api = {
  projects: () => json<ProjectSummary[]>("/api/projects"),

  files: (project: string) =>
    json<SourceFile[]>(`/api/projects/${encodeURIComponent(project)}/files`),

  readFile: async (project: string, path: string) => {
    const response = await fetch(
      `/api/projects/${encodeURIComponent(project)}/file?path=${encodeURIComponent(path)}`
    );
    if (!response.ok) throw new Error(await response.text());
    return response.text();
  },

  writeFile: (project: string, path: string, content: string) =>
    fetch(`/api/projects/${encodeURIComponent(project)}/file?path=${encodeURIComponent(path)}`, {
      method: "PUT",
      headers: { "content-type": "text/plain; charset=utf-8" },
      body: content,
    }).then((r) => {
      if (!r.ok) throw new Error(`save failed: ${r.status}`);
    }),

  shots: (project: string) =>
    json<Shot[]>(`/api/projects/${encodeURIComponent(project)}/shots`),

  shotUrl: (project: string, name: string, bust: string | number = "") =>
    `/api/projects/${encodeURIComponent(project)}/shots/${encodeURIComponent(name)}` +
    (bust === "" ? "" : `?v=${encodeURIComponent(String(bust))}`),

  capture: (project: string, width: number, height: number) =>
    json<Shot>(`/api/projects/${encodeURIComponent(project)}/shots`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ width, height }),
    }),

  preview: (project: string) =>
    json<PreviewStatus>(`/api/projects/${encodeURIComponent(project)}/preview`),

  /** Starts (or re-targets) the preview server at this project. */
  openPreview: (project: string) =>
    json<PreviewStatus>(`/api/projects/${encodeURIComponent(project)}/preview`, {
      method: "POST",
    }),

  rebuild: (project: string) =>
    json<PreviewStatus>(`/api/projects/${encodeURIComponent(project)}/preview/rebuild`, {
      method: "POST",
    }),

  stopChat: (project: string) =>
    fetch(`/api/projects/${encodeURIComponent(project)}/chat/stop`, { method: "POST" }),

  /** Drops the agent's session, so the next turn starts a genuinely new conversation. */
  resetChat: (project: string) =>
    fetch(`/api/projects/${encodeURIComponent(project)}/chat/reset`, { method: "POST" }),

  /** Moves a project into <root>/.trash/ -- recoverable, not an unlink. */
  deleteProject: async (project: string) => {
    const response = await fetch(`/api/projects/${encodeURIComponent(project)}`, {
      method: "DELETE",
    });
    if (!response.ok) throw new Error(await describeFailure(response));
    return (await response.json()) as { trashedTo: string };
  },

  editorAvailable: () => json<{ available: boolean }>("/api/editor"),

  /** Opens a file (or the whole project, when path is omitted) in VS Code. */
  openInEditor: async (project: string, path?: string) => {
    const query = path ? `?path=${encodeURIComponent(path)}` : "";
    const response = await fetch(
      `/api/projects/${encodeURIComponent(project)}/open-editor${query}`,
      { method: "POST" }
    );
    if (!response.ok) throw new Error(await describeFailure(response));
  },
};

/** Server-pushed events, one stream for the whole app. */
export type StudioEvent =
  | { type: "projects" }
  | { type: "preview"; project: string; status: PreviewStatus }
  | { type: "files"; project: string }
  | { type: "shots"; project: string }
  | { type: "build"; project: string; ok: boolean; output: string };

export function subscribe(onEvent: (event: StudioEvent) => void): () => void {
  let source: EventSource | null = null;
  let closed = false;
  let retry: ReturnType<typeof setTimeout> | undefined;

  const connect = () => {
    if (closed) return;
    source = new EventSource("/api/events");
    source.onmessage = (e) => {
      try {
        onEvent(JSON.parse(e.data) as StudioEvent);
      } catch {
        // A malformed frame is not worth tearing the stream down for.
      }
    };
    source.onerror = () => {
      source?.close();
      if (!closed) retry = setTimeout(connect, 1000);
    };
  };

  connect();
  return () => {
    closed = true;
    clearTimeout(retry);
    source?.close();
  };
}

/**
 * Posts a chat turn and streams the agent's reply.
 *
 * The backend relays `claude --output-format stream-json` line by line, so this yields
 * one object per protocol event rather than buffering the whole turn.
 */
export async function* chat(
  project: string,
  message: string,
  signal: AbortSignal
): AsyncGenerator<Record<string, unknown>> {
  const response = await fetch(`/api/projects/${encodeURIComponent(project)}/chat`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ message }),
    signal,
  });

  if (!response.ok || !response.body) {
    throw new Error(`chat failed: ${response.status} ${await response.text()}`);
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";

  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });

    // NDJSON: the last element may be a partial line, so keep it in the buffer.
    const lines = buffer.split("\n");
    buffer = lines.pop() ?? "";

    for (const line of lines) {
      const trimmed = line.trim();
      if (!trimmed) continue;
      try {
        yield JSON.parse(trimmed) as Record<string, unknown>;
      } catch {
        // Non-JSON output from the CLI (a crash banner, say) is surfaced as text.
        yield { type: "raw", text: trimmed };
      }
    }
  }
}
