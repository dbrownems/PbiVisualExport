# AGENTS.md — PbiVisualExport

Guidance for AI coding agents working in this repo. The **full** curated context
lives in [`.github/copilot-instructions.md`](.github/copilot-instructions.md) —
read it for architecture, the export pipeline, auth, TMDL/Fabric notes, and the
task→file map. This file is the quick start.

## What it is

A web service that exports a **selected Power BI visual to CSV**, bypassing the
report's native "Export data" row limits. The browser embeds a report and gathers
the visual's fields + filters via the Power BI Client JS API; the .NET back end
loads the semantic model into **TOM**, builds **one DAX query**
(`SUMMARIZECOLUMNS` + optional `FORMAT()`), runs it over the workspace **XMLA
endpoint** (ADOMD.NET — no paging, no row caps), and streams the rows as CSV.

## Run it

```pwsh
az login                                   # default auth mode is AzureCli (dev "super user")
dotnet user-secrets set PowerBI:WorkspaceId <workspace-guid> --project src/PbiVisualExport.Web  # once
dotnet run --project src/PbiVisualExport.Web   # launchSettings: Development, http://localhost:5099
```

Requires the **.NET 10 SDK**. There's no app-level `.sln` (the one under `_sample/`
is the upstream clone, not this app). In dev mode the first export triggers a
one-time **XMLA device-code sign-in** (XMLA rejects Azure CLI tokens); the UI
shows the code.

**Settings cascade** (later wins): `appsettings.json` (committed, IDs empty) <
user secrets (Development only; the local secret store) < environment variables
(deployed, e.g. `PowerBI__WorkspaceId`, `AzureAd__AuthenticationMode=ServicePrincipal`)
< command line. Never commit environment IDs. See "Configuration" in
[`.github/copilot-instructions.md`](.github/copilot-instructions.md).

## Layout (essentials)

- `src/PbiVisualExport.Web/` — the app.
  - `Services/DaxQueryBuilder.cs` — request + TOM → single DAX query (the core logic).
  - `Services/SemanticModelMetadataService.cs` — Fabric `getDefinition(TMSL)` → TOM.
  - `Services/MetadataCache.cs` — in-memory + single-flight + on-disk TOM cache (`MetadataCache` config).
  - `Services/CsvExportService.cs` — orchestrates; streams the XMLA reader to CSV.
  - `Services/XmlaConnectionFactory.cs` — ADOMD.NET connections (XMLA takes **names**, not IDs).
  - `Services/PowerBiCatalog.cs` — cached ID → name lookups used to validate requests.
  - `Auth/PbiTokenProvider.cs` — one credential, PBI vs Fabric token audiences.
  - `Auth/XmlaTokenProvider.cs` — XMLA credential (`Shared` for SP, device code in dev).
  - `Controllers/` — `/embedinfo/reports`, `/embedinfo/getembedinfo?reportId=`, `/export/csv`, `/export/xmla-auth`.
  - `wwwroot/js/app.js` + `Views/Home/Index.cshtml` — front end + debug panel.
- `infra/` — Fabric TMDL/PBIR authoring; `deploy-model.ps1` deploys models/reports
  (workspace from the settings cascade via `settings.ps1`; resolves
  `{{SemanticModelId:…}}` / `{{WarehouseServer:…}}` tokens at deploy time; supports `-WhatIf`);
  `generate-large-export.py` generates the 2M-row test model + report;
  `compare-exports.py` diffs API vs native exports.
  Test reports are modern enhanced PBIR — see `infra/reports/README.md`.
- `tools/` — `WhSql` (seed a Warehouse), `XmlaSmoke` (legacy TOM + executeQueries smoke test).
- `_sample/` — untouched Microsoft "Embed for your customers/AppOwnsData" clone (reference only; git-ignored, not committed).

## Rules of the road

- **Two token audiences, don't mix**: Power BI (`analysis.windows.net/powerbi/api`)
  for embedding, the catalog and XMLA; Fabric (`api.fabric.microsoft.com`) for
  `getDefinition`. Use `PbiTokenProvider.GetPowerBiTokenAsync` / `GetFabricTokenAsync`.
- **Export requests carry IDs *and* names** (workspace, dataset, report): metadata
  is fetched by ID, data by name over XMLA. `ExportController` validates that they
  agree — keep that check when changing the contract.
- **Keep changes surgical** and match existing style: `sealed` types, records for
  DTOs, nullable on, strongly-typed `IOptions<T>` config (add to `Options/Options.cs`).
- **Don't** enable `InvariantGlobalization` — DAX `FORMAT()`/CSV rely on culture.
- Register services in `Program.cs` (singletons for metadata/token/builder; scoped
  for per-request services).
- Validate real changes by running the app and exporting a visual (compare with the
  **Native export** button / `infra/compare-exports.py`) — don't just eyeball
  generated DAX. For type/format checks ~2,000 rows is enough.
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
them at deploy time, via the settings cascade and `{{SemanticModelId:…}}` /
`{{WarehouseServer:…}}` tokens. Only git history still has the old ones. See
"Before making this repo public" in
[`.github/copilot-instructions.md`](.github/copilot-instructions.md).
