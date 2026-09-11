// Builds the Studio's React + Tailwind UI into ../../artifacts/studio/.
//
// Unlike build/vendor, this bundle is self-contained: React and every dependency are
// bundled straight in, because the Studio UI is one fixed app rather than a host for
// code an agent writes. There is no import map and nothing is external.
//
// Note this is a completely separate React instance from the wireframe vendor bundle.
// That is intentional -- the Studio chrome is a real application UI, and the wireframes
// it previews are sandboxed in an iframe with their own copy.
import * as esbuild from "esbuild";
import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { pathToFileURL } from "node:url";

const here = path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, "$1"));
const outDir = path.resolve(here, "../../artifacts/studio");

const watch = process.argv.includes("--watch");

async function buildJs() {
  const options = {
    entryPoints: [path.join(here, "src/main.tsx")],
    outfile: path.join(outDir, "app.js"),
    bundle: true,
    format: "esm",
    platform: "browser",
    target: ["es2022"],
    jsx: "automatic",
    minify: !watch,
    sourcemap: watch ? "inline" : false,
    legalComments: "none",
    define: { "process.env.NODE_ENV": watch ? '"development"' : '"production"' },
    loader: { ".svg": "dataurl" },
    logLevel: "info",
  };

  if (watch) {
    const ctx = await esbuild.context(options);
    await ctx.watch();
    return ctx;
  }
  await esbuild.build(options);
  return null;
}

function buildCss() {
  // Tailwind scans src/ for the classes actually used, so the Studio sheet stays small --
  // this is a fixed app, not a host for arbitrary authored markup.
  const cli = path.join(here, "node_modules/@tailwindcss/cli/dist/index.mjs");
  const args = [
    cli,
    "--input", path.join(here, "src/app.css"),
    "--output", path.join(outDir, "app.css"),
  ];
  if (!watch) args.push("--minify");
  execFileSync(process.execPath, args, { cwd: here, stdio: ["ignore", "pipe", "inherit"] });
}

export async function build() {
  fs.mkdirSync(outDir, { recursive: true });
  const ctx = await buildJs();
  buildCss();

  const sizes = fs.readdirSync(outDir).map((f) => ({
    f,
    bytes: fs.statSync(path.join(outDir, f)).size,
  }));
  return { sizes, ctx };
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  const { sizes, ctx } = await build();
  for (const { f, bytes } of sizes.sort((a, b) => b.bytes - a.bytes)) {
    console.log(`  ${(bytes / 1024).toFixed(1).padStart(8)} KB  studio/${f}`);
  }
  if (ctx) {
    console.log("\n  watching; ctrl+c to stop");
    await new Promise(() => {});
  }
}
