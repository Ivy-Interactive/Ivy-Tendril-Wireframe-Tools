import { useCallback, useEffect, useRef, useState } from "react";
import { RotateCcw, TerminalSquare, TriangleAlert } from "lucide-react";
import { Terminal } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import { WebLinksAddon } from "@xterm/addon-web-links";
import { api } from "../api";
import { Empty, IconButton, Pane } from "./ui";

/**
 * A real Claude Code session, not a chat box.
 *
 * The backend attaches the CLI to a pseudo-terminal and relays it over a WebSocket, so
 * slash commands, plan mode, the TUI and permission prompts all behave exactly as they do
 * in a normal terminal.
 */

/** Reads the Studio's own theme tokens, so the terminal matches the chrome. */
function readTheme() {
  const style = getComputedStyle(document.documentElement);
  const token = (name: string, fallback: string) =>
    style.getPropertyValue(name).trim() || fallback;

  const light = document.documentElement.dataset.theme === "light";

  return {
    background: token("--color-shell", light ? "#fbfbfc" : "#1c1d22"),
    foreground: token("--color-body", light ? "#38393f" : "#d9d9de"),
    cursor: token("--color-brand", "#5b9bf8"),
    selectionBackground: light ? "rgba(91,155,248,.28)" : "rgba(91,155,248,.35)",
  };
}

export function TerminalPanel({
  project,
  themeKey,
}: {
  project: string | null;
  /** Changes when the Studio theme flips, so the terminal repaints to match. */
  themeKey: string;
}) {
  const hostRef = useRef<HTMLDivElement | null>(null);
  const termRef = useRef<Terminal | null>(null);
  const fitRef = useRef<FitAddon | null>(null);
  const socketRef = useRef<WebSocket | null>(null);

  const [status, setStatus] = useState<"idle" | "connecting" | "live" | "detached">("idle");
  const [unavailable, setUnavailable] = useState<string | null>(null);
  // Forces a fresh terminal + socket when the user restarts the session.
  const [generation, setGeneration] = useState(0);

  useEffect(() => {
    let cancelled = false;
    fetch("/api/terminal")
      .then((r) => r.json() as Promise<{ available: boolean; reason: string | null }>)
      .then((info) => {
        if (!cancelled) setUnavailable(info.available ? null : (info.reason ?? "unavailable"));
      })
      .catch(() => {
        if (!cancelled) setUnavailable("Could not reach the Studio server.");
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const attach = useCallback((node: HTMLDivElement | null) => {
    hostRef.current = node;
  }, []);

  // One terminal and one socket per (project, generation). Both are torn down together:
  // a terminal holding a dead socket looks alive but swallows every keystroke.
  useEffect(() => {
    const host = hostRef.current;
    if (!host || !project || unavailable) return;

    const term = new Terminal({
      convertEol: false,
      cursorBlink: true,
      fontFamily:
        'ui-monospace, "Cascadia Code", "Source Code Pro", Menlo, Consolas, monospace',
      fontSize: 12.5,
      lineHeight: 1.25,
      scrollback: 10_000,
      allowProposedApi: true,
      theme: readTheme(),
    });

    // Ctrl/Cmd+V, and Ctrl+Shift+C to copy.
    //
    // Left alone, xterm treats Ctrl+V as an ordinary control key and sends 0x16 to the
    // PTY, so pasting does nothing at all. Returning false hands the event back to the
    // browser, whose native paste xterm then turns into input -- which needs no clipboard
    // permission, unlike reading the clipboard ourselves.
    //
    // Copy cannot be Ctrl+C: that is interrupt, and a terminal that cannot interrupt is
    // worse than one that cannot copy. Ctrl+Shift+C is the usual terminal binding, and
    // selecting with the mouse still works either way.
    term.attachCustomKeyEventHandler((event) => {
      if (event.type !== "keydown") return true;

      const modifier = event.ctrlKey || event.metaKey;
      if (!modifier) return true;

      const key = event.key.toLowerCase();

      if (key === "v") return false;

      if (key === "c" && event.shiftKey) {
        const selection = term.getSelection();
        if (selection) {
          void navigator.clipboard.writeText(selection).catch(() => {
            // Denied or unavailable; the browser's own copy still works on a selection.
          });
          return false;
        }
      }

      return true;
    });

    const fit = new FitAddon();
    term.loadAddon(fit);
    term.loadAddon(new WebLinksAddon());
    term.open(host);
    fit.fit();

    termRef.current = term;
    fitRef.current = fit;

    setStatus("connecting");
    const protocol = location.protocol === "https:" ? "wss" : "ws";
    const socket = new WebSocket(
      `${protocol}://${location.host}/api/projects/${encodeURIComponent(project)}/pty`
    );
    socket.binaryType = "arraybuffer";
    socketRef.current = socket;

    const sendResize = () => {
      if (socket.readyState !== WebSocket.OPEN) return;
      socket.send(JSON.stringify({ type: "resize", cols: term.cols, rows: term.rows }));
    };

    socket.onopen = () => {
      setStatus("live");
      sendResize();
      term.focus();
    };

    socket.onmessage = (event) => {
      if (event.data instanceof ArrayBuffer) term.write(new Uint8Array(event.data));
      else term.write(String(event.data));
    };

    socket.onclose = () => {
      // Not "ended": the session keeps running on the server and reattaches on the way
      // back. Saying otherwise would suggest context had been lost when it has not.
      setStatus("detached");
    };

    socket.onerror = () => setStatus("detached");

    // Keystrokes go as binary; control messages as text. Keeping them on different frame
    // types means a user typing JSON can never be mistaken for a resize.
    const keys = term.onData((data) => {
      if (socket.readyState === WebSocket.OPEN) {
        socket.send(new TextEncoder().encode(data));
      }
    });

    // Refit on pane resize, and tell the PTY so the CLI reflows its TUI.
    const observer = new ResizeObserver(() => {
      try {
        fit.fit();
        sendResize();
      } catch {
        // Fires while the pane is collapsed to zero; nothing to do.
      }
    });
    observer.observe(host);

    return () => {
      observer.disconnect();
      keys.dispose();
      socket.close();
      term.dispose();
      termRef.current = null;
      fitRef.current = null;
      socketRef.current = null;
    };
  }, [project, generation, unavailable]);

  // Repaint on a theme flip rather than rebuilding: disposing the terminal would throw
  // away the session's scrollback and, worse, the running CLI.
  useEffect(() => {
    if (termRef.current) termRef.current.options.theme = readTheme();
  }, [themeKey]);

  // Ending the session is explicit. Simply remounting would reconnect to the one already
  // running on the server, which is the behaviour that keeps context across a switch.
  const restart = async () => {
    if (!project) return;
    try {
      await api.restartTerminal(project);
    } catch (error) {
      console.error(error);
    }
    setGeneration((n) => n + 1);
  };

  return (
    <Pane
      title="Agent"
      subtitle={
        unavailable
          ? undefined
          : status === "live"
            ? "claude"
            : status === "connecting"
              ? "starting…"
              : status === "detached"
                ? "detached"
                : undefined
      }
      actions={
        <IconButton
          icon={<RotateCcw size={14} />}
          label="Restart the session"
          onClick={() => void restart()}
          disabled={!project || !!unavailable}
        />
      }
      bodyClassName="min-h-0"
    >
      {unavailable ? (
        <Empty icon={<TriangleAlert size={20} />}>
          {unavailable}
          <br />
          <span className="mt-1 inline-block">
            Install it with{" "}
            <span className="font-mono text-body-muted">
              npm install -g @anthropic-ai/claude-code
            </span>
            , then run <span className="font-mono text-body-muted">claude</span> once to sign in.
          </span>
        </Empty>
      ) : !project ? (
        <Empty icon={<TerminalSquare size={20} />}>Pick a wireframe on the left.</Empty>
      ) : (
        // p-2 rather than padding on the xterm element: xterm measures its host to work
        // out rows and columns, and padding inside it throws that measurement off.
        <div className="h-full bg-shell p-2">
          <div ref={attach} className="h-full w-full" />
        </div>
      )}
    </Pane>
  );
}
