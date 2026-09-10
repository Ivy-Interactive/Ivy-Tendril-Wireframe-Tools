// Phase 2 gate. Proves, before any C# exists, that the shape the whole tool depends on
// actually works in a browser:
//
//   1. Bare specifiers ("react", "tendril-wireframes") resolve through an import map to
//      the prebuilt vendor bundle -- no node_modules, no bundled deps in this file.
//   2. There is exactly ONE React instance, so SketchProvider's context reaches the
//      widgets beneath it. (Two instances would throw "Invalid hook call" or silently
//      lose the context.)
//   3. rough.js actually draws: SketchFrame renders null until ResizeObserver reports a
//      non-zero box, so <path> elements existing at all proves measurement completed.
//   4. Balsamiq Sans loads from our own /fonts, not fonts.googleapis.com.
//
// This file is compiled by the real esbuild binary from artifacts/, with the vendor
// specifiers marked external -- exactly what ServeCommand will do at runtime.
import { useEffect, useState, version as reactVersion } from "react";
import { createRoot } from "react-dom/client";
import {
  SketchProvider,
  Card,
  Field,
  TextInput,
  Button,
  Badge,
  Callout,
  TextBlock,
  useSketchTheme,
} from "tendril-wireframes";

/** Reads SketchProvider's context. If React were duplicated this would fall back to the
 *  default theme instead of the one the provider set, so it doubles as an instance check. */
function ContextProbe({ onResult }: { onResult: (ok: boolean, seen: unknown) => void }) {
  const theme = useSketchTheme();
  useEffect(() => {
    // The provider below sets roughness to a distinctive value.
    onResult(theme?.roughness === 2.5, theme?.roughness);
  }, [theme, onResult]);
  return null;
}

function App() {
  const [name, setName] = useState("Acme Corp");
  const [ctxOk, setCtxOk] = useState<boolean | null>(null);

  useEffect(() => {
    (window as any).__proof = {
      ...(window as any).__proof,
      reactVersion,
      contextOk: ctxOk,
    };
  }, [ctxOk]);

  return (
    <SketchProvider roughness={2.5}>
      <ContextProbe onResult={(ok) => setCtxOk(ok)} />
      <div className="tendril p-8 grid grid-cols-2 gap-6 min-h-screen max-w-5xl mx-auto">
        <Card title="New project" description="Utilities below come from the superset sheet.">
          <Field label="Name" required>
            <TextInput value={name} onChange={(v) => setName(v ?? "")} width="100%" />
          </Field>
          <div className="mt-4 flex gap-4 items-center">
            <Button title="Create" icon="Rocket" />
            <Button title="Cancel" variant="Ghost" />
            <Badge title="draft" />
          </div>
        </Card>
        <div className="space-y-4">
          <Callout variant="Info" title="Import map">
            react, react-dom and tendril-wireframes all resolve to the prebuilt vendor bundle.
          </Callout>
          <TextBlock>
            grid-cols-2, space-y-4, mt-4, mx-auto and min-h-screen are all absent from the
            library's own stylesheet -- if this lays out correctly, the superset works.
          </TextBlock>
        </div>
      </div>
    </SketchProvider>
  );
}

createRoot(document.getElementById("root")!).render(<App />);
