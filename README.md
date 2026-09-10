# Ivy Tendril Wireframe Tools

A .NET global tool for building React wireframe mockups with
[`tendril-wireframes`](https://github.com/Ivy-Interactive/Ivy-Tendril-Wireframe-Components) —
the hand-drawn, Balsamiq-style component library.

**No node. No npm. No `node_modules`. No network.** React, the component library, a
Tailwind utility sheet, the fonts and the bundler all ship inside the tool.

```bash
dotnet tool install -g Ivy.Tendril.Wireframe.Console

wireframe setup ./mock          # scaffold a project
wireframe serve ./mock --open   # live preview with hot reload
wireframe screenshot ./mock     # -> ./mock/screenshots/1440x900.png
wireframe agent-readme          # the reference to hand an AI agent
```

## Why

Getting an agent to draw a wireframe used to mean standing up Vite + React + Tailwind:
a package manager, a dependency install, and a pile of config to get right before the
first box appears. This removes all of it. `setup` takes under a second, works offline,
and produces a project an agent can edit immediately.

## Commands

### `wireframe setup <path>`

Scaffolds `src/App.tsx`, `src/main.tsx`, `index.html`, a `tsconfig.json` and a
`.wireframe/` workspace holding the TypeScript definitions. Existing files are never
overwritten. Editors get full IntelliSense with no `node_modules`, because VS Code ships
its own tsserver and `tsconfig` `paths` point at the vendored `.d.ts` files.

### `wireframe serve <path> [--open]`

Binds a free loopback port and serves the wireframe with hot reload. Editing anything
under `src/` triggers an incremental rebuild (5–15 ms) and a browser reload. Build errors
appear both in the terminal and as an overlay in the page — and on error the last working
version stays mounted underneath, so the page keeps working while you fix the typo.

`--print-url` prints just the URL, for scripting.

### `wireframe screenshot <path>`

| Flag | Default | Meaning |
| --- | --- | --- |
| `-w`, `--width` | 1440 | Viewport width in CSS pixels |
| `--height` | 900 | Viewport height in CSS pixels |
| `-s`, `--scale` | 2 | Device pixel ratio |
| `--full` | off | Capture the whole page height |
| `--url` | — | Capture a running URL instead of building |
| `--repeat` | 1 | Capture N times and assert every PNG is byte-identical |

Builds, serves and captures on its own — you do not need `serve` running. Dimensions are
CSS pixels, so the default writes `screenshots/1440x900.png` at 2880×1800. The 2× default
is deliberate: every border is a 1.4 px hand-drawn stroke, and at 1× those alias into
muddy grey.

**On determinism.** Repeated captures on the same machine and browser version are
byte-identical, which `--repeat` verifies. Across machines or Chrome major versions they
are near-identical but not guaranteed — Skia's rasterization changes. If you need
cross-machine byte equality for visual regression testing, pin a container image.

### `wireframe agent-readme`

Prints the complete reference — CLI usage, the library's conventions, and all 98
components with their props — as ~40 KB of markdown. `--component <Name>` prints a full
prop table for one component; `--list` prints just the names.

## Driving it with an AI agent

`scripts/agent-wireframe.ps1` runs the whole loop: scaffold, hand Claude the
`agent-readme`, let it author and iterate against its own screenshots, then capture and
verify the result.

```powershell
./scripts/agent-wireframe.ps1 "A project management dashboard with a sidebar, a kanban board and a stats row"
./scripts/agent-wireframe.ps1 "Mobile checkout flow" -Width 420 -Height 900 -Serve
```

It needs the [Claude Code CLI](https://www.anthropic.com/claude-code) on `PATH`. This is
also the project's acceptance test: the agent gets `agent-readme` and nothing else, so if
it cannot build a wireframe from that alone, the reference is what needs fixing.

## How it works

The npm work happens once, at tool-build time, on a machine that has node:

```
BUILD TIME (ours)                        RUNTIME (yours)
─────────────────                        ───────────────
esbuild → vendor/*.js  ┐                 setup:      unpack scaffold + types
tailwind → utilities   ├→ artifacts/  →  serve:      esbuild --watch (your src only)
Balsamiq Sans woff2    │   (embedded)    screenshot: build → serve → CDP → PNG
.d.ts closure          ┘
```

At runtime, only your own `.tsx` is bundled. `react`, `react-dom` and `tendril-wireframes`
are marked external and resolved in the browser through an
[import map](https://developer.mozilla.org/en-US/docs/Web/HTML/Element/script/type/importmap),
so a rebuild never walks the dependency tree.

Screenshots drive an installed Chrome or Edge over the DevTools Protocol. That is
necessary rather than convenient: Tendril generates every stroke with rough.js *after* a
`ResizeObserver` measures the element, so the first painted frame of a wireframe contains
no strokes at all. A capture has to wait for fonts, then images, then a quiet period with
no layout changes — which is what `signalWireframeReady()` in the scaffold reports.

### Tailwind

A precompiled Tailwind v4 sheet ships inside the tool, generated by real Tailwind over an
explicit safelist. The common surface is present — spacing, `grid-cols-*`, flex, type
scale, the theme palette, and the `sm:`/`hover:` style variants.

**Arbitrary values like `w-[347px]` are not generated**, because the sheet is compiled
ahead of time. Use an inline style or a component's own size prop instead:

```tsx
<div style={{ width: 347 }} />   // instead of className="w-[347px]"
<Card width="20rem" />
```

Classes that produce no CSS are reported by `serve` and `screenshot` rather than failing
silently, with the nearest available step:

```
! src/App.tsx:24 — `mt-13` produces no CSS. Nearest available: mt-12, mt-14, mt-11.
```

If you do need the full surface, `wireframe setup <path> --tailwind jit` downloads the
official Tailwind standalone CLI (~107 MB, cached once) and generates the sheet from your
source instead. The mode is recorded in `wireframe.json`, so `serve` and `screenshot` pick
it up automatically.

## Development

```bash
dotnet build Ivy.Tendril.Wireframe.slnx
dotnet test  Ivy.Tendril.Wireframe.slnx
```

`artifacts/` is committed so neither CI nor contributors need node. To regenerate it after
bumping a pinned package:

```bash
cd build/vendor
npm install
node build-all.mjs
```

The esbuild binaries are the one exception — 74 MB across 7 platforms is not something to
keep in git history, so `dotnet build` and CI fetch them with
`node build/vendor/fetch-esbuild.mjs`.

`build/proof/` is a standalone check that the import-map + vendor-bundle arrangement works
in a real browser: one React instance, local fonts, and rough.js actually drawing.

```bash
cd build/proof && node serve-proof.mjs
```

## Licence

MIT. Balsamiq Sans is redistributed under the
[SIL Open Font License 1.1](https://openfontlicense.org/); see
`artifacts/fonts/OFL.txt`.
