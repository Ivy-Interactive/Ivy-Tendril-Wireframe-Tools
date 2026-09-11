namespace Ivy.Tendril.Wireframe.Console.Project;

/// <summary>
/// The files `wireframe setup` writes into a new project.
///
/// The whole app lives under src/ -- index.html, the entry point, the components and any
/// static assets. The project root holds only configuration (tsconfig.json, .gitignore),
/// generated output (screenshots/, .wireframe/) and docs.
///
/// These are the first thing an agent reads, so they double as documentation: the starter
/// App.tsx demonstrates the conventions (named-enum props, sizing values, the
/// SketchProvider/.tendril wrappers) that the component library expects.
/// </summary>
public static class ScaffoldTemplates
{
    public const string IndexHtml =
        """
        <!doctype html>
        <html lang="en">
          <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <title>{{TITLE}}</title>
            <!--
              Stylesheets are injected by the dev server, in this order:
                1. tendril.css             the library sheet (carries Tailwind's preflight)
                2. wireframe-utilities.css the Tailwind utility superset
                3. fonts.css               self-hosted Balsamiq Sans
              Order matters: the utility sheet must come after the library sheet so its
              rules win within the shared @layer utilities.
            -->
          </head>
          <body>
            <div id="root"></div>
          </body>
        </html>

        """;

    public const string MainTsx =
        """
        import { createRoot } from "react-dom/client";
        import { SketchProvider } from "tendril-wireframes";
        import { signalWireframeReady } from "./wireframe-ready";
        import App from "./App";

        // SketchProvider mounts the shared SVG filters and sets the pencil (roughness,
        // bowing, stroke width) for everything beneath it. The `tendril` class applies the
        // handwriting font and ink colour. Both are required -- without them the components
        // render, but not as a hand-drawn wireframe.
        createRoot(document.getElementById("root")!).render(
          <SketchProvider>
            <div className="tendril">
              <App />
            </div>
          </SketchProvider>
        );

        // Tells `wireframe screenshot` when the sketch has finished drawing. Leave this in.
        signalWireframeReady();

        """;

    public const string AppTsx =
        """
        import {
          Button,
          Card,
          Field,
          TextInput,
          SelectInput,
          Callout,
          TextBlock,
          Separator,
        } from "tendril-wireframes";
        import { useState } from "react";

        // Props are named enums rather than booleans plus utility classes:
        //   variant="Destructive"  density="Small"  borderRadius="Full"
        // Sizes accept CSS lengths ("20rem"), fractions ("1/2") and bare numbers, which
        // mean quarter-rem steps. Run `wireframe agent-readme` for the full reference.
        export default function App() {
          const [name, setName] = useState("");
          const [plan, setPlan] = useState<string | null>("team");

          return (
            <div className="mx-auto max-w-4xl p-8">
              <TextBlock variant="H2">New workspace</TextBlock>
              <TextBlock variant="Muted">
                Replace this with your wireframe. Layout is plain flexbox and CSS grid;
                everything that should look drawn comes from a Tendril component.
              </TextBlock>

              <Separator />

              <div className="mt-6 grid grid-cols-2 gap-6">
                <Card title="Details">
                  <Field label="Workspace name" required>
                    <TextInput
                      value={name}
                      onChange={(v) => setName(v ?? "")}
                      placeholder="Acme Corp"
                      width="100%"
                    />
                  </Field>
                  <Field label="Plan">
                    <SelectInput
                      value={plan}
                      onChange={setPlan}
                      options={[
                        { value: "solo", label: "Solo" },
                        { value: "team", label: "Team" },
                        { value: "enterprise", label: "Enterprise" },
                      ]}
                      width="100%"
                    />
                  </Field>
                  <div className="mt-4 flex gap-3">
                    <Button title="Create" icon="Rocket" />
                    <Button title="Cancel" variant="Ghost" />
                  </div>
                </Card>

                <Callout variant="Info" title="Hot reload is on">
                  Edit any file under src/ and the browser reloads. Build errors appear both
                  here and in the terminal.
                </Callout>
              </div>
            </div>
          );
        }

        """;

    /// <summary>
    /// The screenshot readiness contract. The ordering here is load-bearing -- see the
    /// comments inline; getting it wrong produces screenshots of half-drawn wireframes.
    /// </summary>
    public const string WireframeReadyTs =
        """
        // Signals when the wireframe has finished drawing, so `wireframe screenshot` knows
        // when to capture. Import and call this once from main.tsx.
        //
        // Why this is more involved than "wait for load": every border and fill in Tendril
        // is an SVG path that rough.js generates AFTER a ResizeObserver measures the
        // element's box. A frame with no measurement yet renders nothing at all, so the
        // first painted frame of any wireframe is empty.

        type ReadyState = {
          version: 1;
          ready: boolean;
          reason: string | null;
          deterministic: boolean;
          whenReady: () => Promise<void>;
        };

        declare global {
          interface Window {
            __wireframe?: ReadyState;
          }
        }

        const QUIET_MS = 120;      // no ResizeObserver callback for this long => settled
        const HARD_CAP_MS = 10_000; // never hang, even if something animates forever

        export function signalWireframeReady(root: HTMLElement = document.body): void {
          let resolve!: () => void;
          const promise = new Promise<void>((r) => (resolve = r));

          const state: ReadyState = {
            version: 1,
            ready: false,
            reason: "starting",
            deterministic: true,
            whenReady: () => promise,
          };
          window.__wireframe = state;

          let quiet: ReturnType<typeof setTimeout> | undefined;
          const observer = new ResizeObserver(() => bump("layout still settling"));

          function observeAll() {
            observer.disconnect();
            root.querySelectorAll<HTMLElement>("*").forEach((el) => observer.observe(el));
          }

          function bump(reason: string) {
            state.reason = reason;
            clearTimeout(quiet);
            quiet = setTimeout(finish, QUIET_MS);
          }

          function finish() {
            observer.disconnect();
            // A focused input blinks its caret, which is a one-pixel diff between two
            // otherwise identical screenshots.
            if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
            state.ready = true;
            state.reason = null;
            document.documentElement.setAttribute("data-wireframe-ready", "true");
            resolve();
          }

          void (async () => {
            // 1. Fonts FIRST, and awaited alone. Swapping in Balsamiq Sans changes text
            //    metrics, which resizes every frame, which regenerates every rough.js path.
            //    Capturing between those two states gives strokes sized for the fallback
            //    font wrapped around correctly-sized text.
            state.reason = "waiting for fonts";
            await document.fonts.ready;

            // 2. Images change measured boxes too.
            state.reason = "waiting for images";
            await Promise.all(
              [...document.images]
                .filter((img) => !img.complete)
                .map((img) => new Promise((r) => {
                  img.onload = r;
                  img.onerror = r;
                }))
            );

            // 3. Let React commit the post-measurement render, then let it paint.
            await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));

            // 4. Only now start the quiet clock. The rAFs above guarantee one paint has
            //    happened, but nested frames settle in a cascade -- a Table inside a Card
            //    inside a SidebarLayout can take three rounds -- so the ResizeObserver
            //    quiet period is the signal that actually matters.
            observeAll();
            bump("waiting for quiet period");
          })();

          setTimeout(() => {
            if (!state.ready) {
              state.reason = "timed out";
              finish();
            }
          }, HARD_CAP_MS);
        }

        """;

    public const string TsConfig =
        """
        {
          // Editor support only -- nothing runs tsc. The real type definitions live in
          // .wireframe/types/, which `wireframe setup` regenerates; do not edit them.
          "extends": "./.wireframe/tsconfig.base.json",
          "include": ["src"]
        }

        """;

    /// <summary>
    /// Written into .wireframe/. `paths` is what lets an editor resolve "react" and
    /// "tendril-wireframes" with no node_modules on disk -- VS Code ships its own tsserver,
    /// so this gives IntelliSense without node installed.
    /// </summary>
    public const string TsConfigBase =
        """
        {
          "//": "GENERATED by `wireframe setup` -- do not edit. Regenerated on every run.",
          "compilerOptions": {
            "target": "ES2022",
            "module": "ESNext",
            "moduleResolution": "Bundler",
            "jsx": "react-jsx",
            "jsxImportSource": "react",
            "strict": true,
            "noEmit": true,
            "allowSyntheticDefaultImports": true,
            "esModuleInterop": true,
            "resolveJsonModule": true,
            "isolatedModules": true,
            "skipLibCheck": true,
            "forceConsistentCasingInFileNames": true,
            "lib": ["ES2022", "DOM", "DOM.Iterable"],
            "//types": "Empty types/typeRoots stops TS hunting for a node_modules/@types that will never exist.",
            "types": [],
            "typeRoots": [],
            "baseUrl": "..",
            "paths": {
        {{PATHS}}
            }
          }
        }

        """;
}
