// Builds the browser client: dist/pbi-visual-export.mjs (ESM) and dist/pbi-visual-export.js
// (IIFE, global `PbiVisualExport`), then copies both into the .NET library, which embeds
// them and serves them at {prefix}/client.mjs and {prefix}/client.js.
import { build } from "esbuild";
import { copyFile, mkdir, readFile } from "node:fs/promises";

const pkg = JSON.parse(await readFile(new URL("./package.json", import.meta.url), "utf8"));
const common = {
    entryPoints: ["src/index.ts"],
    bundle: true,
    target: "es2020",
    charset: "utf8",
    legalComments: "none",
    banner: { js: `/*! ${pkg.name} ${pkg.version} | MIT | https://github.com/dbrownems/PbiVisualExport */` },
    define: { __VERSION__: JSON.stringify(pkg.version) },
    logLevel: "warning",
};
await build({ ...common, format: "esm", outfile: "dist/pbi-visual-export.mjs" });
await build({ ...common, format: "iife", globalName: "PbiVisualExport", outfile: "dist/pbi-visual-export.js" });

const client = new URL("../PbiVisualExport/Client/", import.meta.url);
await mkdir(client, { recursive: true });
for (const f of ["pbi-visual-export.js", "pbi-visual-export.mjs"])
    await copyFile(new URL(`./dist/${f}`, import.meta.url), new URL(f, client));
console.log(`Built ${pkg.name} ${pkg.version} -> dist/ and src/PbiVisualExport/Client/`);
