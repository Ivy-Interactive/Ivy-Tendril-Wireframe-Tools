// Build-time only. Bundles proof/app.tsx with the REAL esbuild binary from artifacts/
// and serves it the same way the C# tool will, so Phase 2 exercises the production shape
// rather than a node-flavoured approximation of it.
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";

const here = path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, "$1"));
const artifacts = path.resolve(here, "../../artifacts");
const outDir = path.join(here, ".out");

const manifest = JSON.parse(fs.readFileSync(path.join(artifacts, "vendor.manifest.json"), "utf8"));
const specifiers = Object.keys(manifest.specifiers);

const esbuild = path.join(
  artifacts,
  "esbuild",
  process.platform === "win32" ? "win-x64/esbuild.exe" : "linux-x64/esbuild"
);

fs.mkdirSync(outDir, { recursive: true });

// The externals list comes from the same manifest that produces the import map -- one
// source of truth, so the two can never drift apart.
execFileSync(
  esbuild,
  [
    path.join(here, "app.tsx"),
    "--bundle",
    `--outfile=${path.join(outDir, "app.js")}`,
    "--format=esm",
    "--target=es2022",
    "--jsx=automatic",
    "--sourcemap=linked",
    ...specifiers.map((s) => `--external:${s}`),
  ],
  { stdio: "inherit" }
);

const importMap = JSON.stringify({ imports: manifest.specifiers }, null, 2);

const html = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>wireframe proof</title>
<link rel="stylesheet" href="/__wireframe/css/tendril.css">
<link rel="stylesheet" href="/__wireframe/css/wireframe-utilities.css">
<link rel="stylesheet" href="/__wireframe/css/fonts.css">
<script type="importmap">${importMap}</script>
</head>
<body>
<div id="root"></div>
<script type="module" src="/app.js"></script>
</body>
</html>
`;

const MIME = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".map": "application/json",
  ".woff2": "font/woff2",
  ".json": "application/json",
};

const roots = [
  { prefix: "/__wireframe/css/", dir: path.join(artifacts, "css") },
  { prefix: "/__wireframe/fonts/", dir: path.join(artifacts, "fonts") },
  { prefix: "/__wireframe/vendor/", dir: path.join(artifacts, "vendor") },
  { prefix: "/", dir: outDir },
];

const server = http.createServer((req, res) => {
  const url = decodeURIComponent(req.url.split("?")[0]);
  if (url === "/" || url === "/index.html") {
    res.writeHead(200, { "content-type": MIME[".html"], "cache-control": "no-store" });
    return res.end(html);
  }
  for (const { prefix, dir } of roots) {
    if (!url.startsWith(prefix)) continue;
    const file = path.join(dir, url.slice(prefix.length));
    if (fs.existsSync(file) && fs.statSync(file).isFile()) {
      res.writeHead(200, {
        "content-type": MIME[path.extname(file)] ?? "application/octet-stream",
        "cache-control": "no-store",
      });
      return res.end(fs.readFileSync(file));
    }
  }
  res.writeHead(404).end("not found");
});

server.listen(Number(process.env.PROOF_PORT) || 0, "127.0.0.1", () => {
  console.log(`http://127.0.0.1:${server.address().port}/`);
});
