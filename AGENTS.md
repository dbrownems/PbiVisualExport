# AGENTS.md — PbiVisualExport

Guidance for AI coding agents working in this repo. The **full** curated context
lives in [`.github/copilot-instructions.md`](.github/copilot-instructions.md) —
read it for architecture, the export pipeline, auth, TMDL/Fabric notes, and the
task→file map. **Integrators** (adding the module to a host app) should read
[`README.md`](README.md). This file is the quick start.

## What it is

A web service that exports a **selected Power BI visual to CSV**, bypassing the
report's native "Export data" row limits. The browser embeds a report and gathers
the visual's fields + filters via the Power BI Client JS API; the .NET back end
loads the semantic model into **TOM**, builds **one DAX query**
(`SUMMARIZECOLUMNS` + optional `FORMAT()`), runs it over the workspace **XMLA
endpoint** (ADOMD.NET — no paging, no row caps), and streams the rows as CSV.

It's a **sample and reference implementation** for exporting Power BI visuals.
Docs should say so, and point to the README's *Known gaps*.

## Run it

```pwsh
az login                                   # default credential mode is AzureCli (dev "super user")
$p = 'samples/PbiVisualExport.SampleHost'
dotnet user-secrets set PowerBI:WorkspaceId <workspace-guid> --project $p   # once
dotnet run --project $p                    # launchSettings: Development, http://localhost:5099
dotnet build PbiVisualExport.sln           # library (net8.0 + net10.0) + sample + tests
dotnet test PbiVisualExport.sln            # offline unit/golden/endpoint tests (no Power BI)
cd src/pbi-visual-export-js; npm ci; npm test; npm run build   # JS client (build copies bundles into the library)
```

Requires the **.NET 10 SDK** (the library also targets net8.0). In dev mode the
first export triggers a one-time **XMLA device-code sign-in** (XMLA rejects Azure
CLI tokens); the UI shows the code.

**Settings cascade** (later wins): `appsettings.json` (committed, IDs empty) <
user secrets (Development only; the local secret store) < environment variables
(deployed, e.g. `PowerBI__WorkspaceId`, `PbiVisualExport__Credential__Mode=ServicePrincipal`)
< command line. Never commit environment IDs. Module settings live under
`PbiVisualExport` (`PbiVisualExportOptions`); the sample's own embed settings
under `PowerBI`.

## Layout (essentials)

The repo ships a **module**: a .NET class library (NuGet package) that a host
ASP.NET Core app adds with `services.AddPbiVisualExport(config)` +
`app.MapPbiVisualExport("/pbi-export")`, plus a JS client bundle (embedded in the
library and served at `{prefix}/client.js`). The sample host shows the wiring.

- `PbiVisualExport.sln` — library + sample + tests.
- `src/PbiVisualExport/` — the library (net8.0 + net10.0): DI/endpoint extensions,
  options, `Extensibility.cs` seams, `Services/` (export pipeline; the core is
  `DaxQueryBuilder*.cs` + `DaxPlan.cs`), `Auth/` (token providers), `Client/` (**generated** JS
  bundles; don't hand-edit).
- `src/pbi-visual-export-js/` — the TypeScript client; `npm run build` writes `dist/`
  and the library's `Client/` bundles.
- `tests/PbiVisualExport.Tests/` — xUnit: golden `.dax` files (`UPDATE_GOLDEN=1` to
  regenerate), unit tests, and endpoint tests (`WebApplication` + `UseTestServer`
  with fakes). Fixtures load from `infra/models` and `infra/reports`.
- `samples/PbiVisualExport.SampleHost/` — reference host (embedding is the host's job).
- `infra/` — Fabric TMDL/PBIR test artifacts and scripts: `deploy-all.ps1`
  (deploys the Warehouse, models and reports to a workspace and refreshes them;
  rerunnable, updates in place), `deploy-model.ps1`
  (workspace from a subset of the settings cascade via `settings.ps1`; `{{…}}`
  tokens resolved at deploy time), `smoke-export.ps1` (exports StoreSales `table1`
  through a running host: the setup check), `generate-large-export.py`, `compare-exports.py`.
- `tools/` — `WhSql` (seed a Warehouse).
- `_sample/` — untouched Microsoft "Embed for your customers/AppOwnsData" clone
  (reference only; git-ignored).

File-by-file detail: the guide's *Repository layout* and *Task → file map*; test
reports: `infra/reports/README.md`.

## Rules of the road

- **Two token audiences, don't mix**: Power BI (`analysis.windows.net/powerbi/api`)
  for embedding, the catalog and XMLA; Fabric (`api.fabric.microsoft.com`) for
  `getDefinition`. Use `PbiTokenProvider.GetPowerBiTokenAsync` / `GetFabricTokenAsync`.
- **Only `workspaceId` + `reportId` are required** in a request: metadata is fetched
  by ID, data by name over XMLA. `ExportRequestResolver` resolves the dataset and
  names from the catalog and rejects (400) any ID/name the client sent that
  disagrees — keep that check when changing the contract.
- **Keep the library's public surface small**: implementation types are
  `internal`; hosts use the DI/endpoint extensions, options and `Extensibility.cs`
  seams. Bump `ExportHandler.ContractVersion` (and the JS `CONTRACT_VERSION`) on
  breaking wire changes; keep `Models.cs` and the client's `types.ts` in sync.
- **Keep changes surgical** and match existing style: `sealed` types, records for
  DTOs, nullable on, strongly-typed options (add to `PbiVisualExportOptions.cs`).
- **Don't** enable `InvariantGlobalization` — DAX `FORMAT()`/CSV rely on culture.
- Register library services in `AddPbiVisualExport` (singletons for
  metadata/token/builder; scoped for per-request services), not in the sample.
- Validate real changes by running the app and exporting a visual (compare with the
  **Native export** button / `infra/compare-exports.py`) — don't just eyeball
  generated DAX. For type/format checks ~2,000 rows is enough.
- **JS changes**: run `npm run build` and commit the regenerated
  `src/PbiVisualExport/Client/*` bundles — CI rebuilds them and fails on a diff.
- **DAX changes**: expect golden-file diffs; review them, then `UPDATE_GOLDEN=1 dotnet test`.
- CI (`.github/workflows/ci.yml`) builds/tests on windows + ubuntu, and a `v*` tag
  packs and publishes the NuGet package to GitHub Packages.
- PBIR: never set `"active": true` on a `tableEx` projection (the table renders
  only that column). See `infra/reports/README.md`.

## Source control

The code lives in the private repo `dbrownems/PbiVisualExport`. The `.gitignore`
excludes these:

- `_sample/`: re-clone it from microsoft/PowerBI-Developer-Samples if needed.
- `nuget.config`: the local copy points at the Microsoft package feed proxy,
  because managed devices block nuget.org and PyPI. Everywhere else, restore
  uses nuget.org.
- Build output.

## ⚠️ Before making this repo public

App, tool and infra config no longer commit environment IDs. `infra/` resolves
them at deploy time, via a subset of the settings cascade (`infra/settings.ps1`) and `{{SemanticModelId:…}}` /
`{{WarehouseServer:…}}` tokens. Git history was rewritten to replace the old ones
with placeholders; only the binary `infra/samples/StoreSales.pbix` still embeds
its original IDs. See
"Before making this repo public" in
[`.github/copilot-instructions.md`](.github/copilot-instructions.md).
