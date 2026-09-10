namespace Ivy.Tendril.Wireframe.Console.Hosting;

/// <summary>
/// The browser half of hot reload. Served at /__wireframe/client.js in serve mode only.
///
/// Full-page reload rather than React Fast Refresh: Fast Refresh needs a Babel/SWC
/// transform esbuild does not ship, and it would be actively wrong for this library
/// anyway. Tendril memoises rough.js paths against ResizeObserver-measured boxes, so a
/// partial refresh leaves stale SVG strokes sized for the old layout sitting behind
/// freshly-rendered content -- which reads as a rendering bug in the library rather than a
/// stale frame. A reload against a loopback server with a warm vendor bundle is ~200 ms
/// and always correct.
/// </summary>
public static class LiveReloadClient
{
    public const string Source =
        """
        (() => {
          const ENDPOINT = `ws://${location.host}/__wireframe/hmr`;
          const SCROLL_KEY = "__wireframe_scroll";

          // Restore scroll across the reload so editing feels continuous.
          try {
            const saved = sessionStorage.getItem(SCROLL_KEY);
            if (saved !== null) {
              sessionStorage.removeItem(SCROLL_KEY);
              addEventListener("load", () => scrollTo(0, parseInt(saved, 10) || 0));
            }
          } catch {}

          function reload() {
            try { sessionStorage.setItem(SCROLL_KEY, String(scrollY)); } catch {}
            location.reload();
          }

          // --- error overlay ------------------------------------------------------
          // In a shadow root so the app's CSS reset and the .tendril font cannot
          // restyle it, and so it never collides with the wireframe's own markup.
          let overlay = null;

          function ensureOverlay() {
            if (overlay) return overlay;
            overlay = document.createElement("div");
            overlay.style.cssText = "position:fixed;inset:0;z-index:2147483647";
            overlay.attachShadow({ mode: "open" }).innerHTML = `
              <style>
                .wrap {
                  position: fixed; inset: 0; overflow: auto; box-sizing: border-box;
                  padding: 28px 32px;
                  background: rgba(24,24,27,.94); color: #fafafa;
                  font: 13px/1.55 ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
                }
                h1 { margin: 0 0 4px; font-size: 12px; font-weight: 700; color: #fca5a5;
                     letter-spacing: .08em; text-transform: uppercase; }
                .loc  { margin: 0 0 18px; color: #a1a1aa; }
                pre   { margin: 0; white-space: pre-wrap; word-break: break-word; }
                .hint { margin-top: 22px; color: #71717a; }
              </style>
              <div class="wrap">
                <h1>build failed</h1>
                <p class="loc"></p>
                <pre></pre>
                <p class="hint">The last working version is still running underneath. Press Esc to dismiss.</p>
              </div>`;
            document.body.appendChild(overlay);
            return overlay;
          }

          function showError(location, text) {
            const el = ensureOverlay();
            el.shadowRoot.querySelector(".loc").textContent = location || "";
            el.shadowRoot.querySelector("pre").textContent = text || "";
            el.style.display = "block";
          }

          function hideError() {
            if (overlay) overlay.style.display = "none";
          }

          addEventListener("keydown", (e) => { if (e.key === "Escape") hideError(); });

          // --- surface page errors in the terminal --------------------------------
          // The whole point of this tool is that the user may never open devtools.
          function report(kind, detail) {
            try {
              navigator.sendBeacon(
                "/__wireframe/report",
                new Blob([JSON.stringify({ kind, detail })], { type: "application/json" })
              );
            } catch {}
          }
          addEventListener("error", (e) =>
            report("error", e.error ? `${e.error.message}\n${e.error.stack ?? ""}` : e.message));
          addEventListener("unhandledrejection", (e) =>
            report("unhandledrejection", String(e.reason?.stack ?? e.reason)));

          // --- socket -------------------------------------------------------------
          let everConnected = false;

          function connect() {
            const ws = new WebSocket(ENDPOINT);

            ws.onopen = () => {
              // A reconnect means the server restarted, so our bundle is stale.
              if (everConnected) return reload();
              everConnected = true;
            };

            ws.onmessage = (event) => {
              const msg = JSON.parse(event.data);
              // On error we deliberately do NOT reload: the last good bundle stays
              // mounted underneath, so the page keeps working while the typo is fixed.
              if (msg.type === "reload") { hideError(); reload(); }
              else if (msg.type === "error") showError(msg.location, msg.text);
              else if (msg.type === "ok") hideError();
            };

            ws.onclose = () => setTimeout(connect, 500);
            ws.onerror = () => ws.close();
          }

          connect();
        })();

        """;
}
