# pbi-visual-export (browser client)

The browser half of [PbiVisualExport](../../README.md). It reads an embedded Power BI
visual's fields, filters, slicer selections and sort through powerbi-client, sends them
to the PbiVisualExport ASP.NET Core module, and saves the CSV. Where a Save As dialog is
available, it streams the CSV to disk without holding it in memory. It has no runtime
dependencies, and works with the `Report` your page already embeds.

## Loading it

The .NET module serves this bundle, so client and server versions always match:

| URL | Format |
|---|---|
| `{prefix}/client.js` | IIFE; sets the global `PbiVisualExport` |
| `{prefix}/client.mjs` | ES module |

Both are anonymous and revalidated with an ETag (`Cache-Control: no-cache`), so a
deployment takes effect right away. `{prefix}` is where the host mapped the
module (`app.MapPbiVisualExport("/pbi-export")`).

```html
<script src="https://cdn.jsdelivr.net/npm/powerbi-client@2.23.1/dist/powerbi.min.js"></script>
<!-- Required: adds visual.getDataFields / getCapabilities. Load it after powerbi-client. -->
<script src="https://cdn.jsdelivr.net/npm/powerbi-report-authoring@3.0.0/dist/powerbi-report-authoring.js"></script>
<script src="/pbi-export/client.js"></script>
```

```js
import { createExporter } from "/pbi-export/client.mjs";
```

To bundle it yourself, build this folder (`npm ci && npm run build`) and import
`dist/pbi-visual-export.mjs`. The types are in `dist/index.d.ts`. The package is
private and isn't published to npm.

## Usage

```js
const report = powerbi.embed(container, config);
const exporter = PbiVisualExport.createExporter({
  report,
  apiBase: "/pbi-export",
  // For bearer-token APIs (a function is called before every request):
  headers: async () => ({ Authorization: "Bearer " + await getApiToken() }),
});

// "Export all data (CSV)" in every visual's "More options" menu.
report.on("loaded", () => exporter.addVisualMenuCommand({
  onError: e => alert(e.message),
}));

// Or your own UI: a dropdown of visuals and an Export button.
const visuals = await exporter.listVisuals();          // [{ key, label, page, visual, title }]
exportButton.onclick = async () => {
  const { visual, page } = visuals[select.selectedIndex];
  const result = await exporter.exportVisual(visual, {
    page,
    formatted: true,          // apply the model's format strings (default)
    sort: false,              // ORDER BY the visual's sort (default false; costs capacity memory)
    onProgress: p => (status.textContent = p.message),
  });
  if (result.status === "cancelled") status.textContent = "Cancelled.";
};
```

### How the file is saved

`exportVisual` picks a transfer method (`target`):

| `target` | Behavior |
|---|---|
| `auto` (default) | `file` where the File System Access API is available, else `download`. |
| `file` | A Save As dialog, then `POST {prefix}/csv` piped into the chosen file, so nothing is held in memory. Desktop Chrome and Edge only; needs a user gesture. |
| `download` | `POST {prefix}/prepare` checks the export, then `POST {prefix}/csv` is collected into a Blob and saved through an `<a download>` link. Works everywhere, but the browser holds the whole CSV until the export completes, and the file appears in the browser's downloads only then. |

The Save As dialog needs the click's user gesture, so call `exportVisual` directly from
the click handler. Don't `await` other work first. A visual-menu click happens inside
the report's iframe, which gives the page no gesture, so menu commands always use
`download`.

Because `download` holds the whole file, very large exports can fail in browsers without
the Save As dialog (Firefox, Safari, mobile, cross-origin iframes) and from the visual
menu. If your users export hundreds of MB there, consider staging exports in Azure Blob
Storage and handing the browser a short-lived SAS URL. The
[module README](../../README.md#5-large-exports-without-save-as) describes the design
and its trade-offs.

## API

`createExporter(options)` returns an `Exporter`. The options are:

| Option | Default | |
|---|---|---|
| `report` | required | The embedded `Report`. |
| `apiBase` | `/pbi-export` | The module's prefix; it can be absolute for a cross-origin API. |
| `workspaceId` | `groupId` from the embed URL | |
| `reportId` | `report.getId()` | |
| `datasetId` | `report.config.datasetId` | Optional: the server resolves it from the report. |
| `headers` | none | Extra headers, or a (possibly async) function that returns them. |
| `credentials` | `same-origin` | `fetch` credentials; use `include` for a cross-origin API that uses cookies. |
| `onWarning` | `console.warn` | Non-fatal problems, such as a client/server contract mismatch. |

`Exporter` methods:

| Method | |
|---|---|
| `listVisuals()` | Exportable visuals on the visible pages. Text boxes, images, shapes, buttons, cards, bookmark navigators and filter visuals are skipped. |
| `getVisual(pageName, visualName)` | Finds a visual by page name and visual name. |
| `buildRequest(visual, options)` | The request `exportVisual` would send, without sending it (for previews and debugging). |
| `exportVisual(visual, options)` | Exports the visual. Resolves with an `ExportResult` (see below). |
| `addVisualMenuCommand(options)` | Adds the visual-menu command and handles its clicks. Returns a function that removes it. This **replaces** the report's `settings.extensions`. |
| `visualMenuCommand(options)` / `handleCommand(event, options)` | Use these instead if you build `settings.extensions` yourself. |
| `getSignInStatus()` / `waitForSignIn()` | For dev XMLA device-code sign-in (see below). |
| `url(path)` | The URL of a module endpoint. |
| `dispose()` | Removes the event handlers the exporter added. |

`exportVisual` options (`buildRequest` accepts the first five):

| Option | |
|---|---|
| `formatted` | Default true. |
| `sort` | Default false. |
| `page` | The visual's page. Default: `visual.page`. |
| `onProgress` | Called with `{ phase, message, bytes?, elapsedMs }`. |
| `signal` | An `AbortSignal` that cancels the export. |
| `target` | See the table above. |
| `fileName` | The file name: suggested in the Save As dialog, or the browser download's. |
| `onRequest` | Called with the request just before it's sent. |

The `ExportResult` fields depend on how the file was transferred:

| `status` / `method` | Fields |
|---|---|
| `cancelled` (Save As dismissed) | `status`, `elapsedMs` |
| `completed` / `file` | `status`, `method`, `fileName`, `bytes`, `elapsedMs`, `request` |
| `completed` / `download` | `status`, `method`, `fileName`, `bytes`, `elapsedMs`, `request`, `prepared` (the `prepare` result, with any `warnings`). The file has been handed to the browser's downloads. |

### Errors

Failures reject with a `PbiExportError`, which has a `code`:

| `code` | Meaning |
|---|---|
| `http` | The server refused. See `status` (400 bad request, 403 not allowed, 429 too many exports). `problems` lists the details, only for request-validation 400s. |
| `downloadFailed` | The transfer failed or stopped part way. A Save As file may keep partial content (it's removed only if still empty); a browser download saves nothing. |
| `signInRequired` | Dev only: the server needs an XMLA device-code sign-in. Show `signIn.verificationUri` and `signIn.userCode`, `await exporter.waitForSignIn()`, then retry. |
| `slicerUnreadable` | A slicer's selection (or the page's list of visuals, needed to find its slicers) couldn't be read. The client stops rather than export too many rows. |
| `filtersUnreadable` | The report, page or visual filters couldn't be read, from the SDK or from the bookmark. The client stops rather than export too many rows. |
| `authoringApiMissing` | `powerbi-report-authoring` isn't loaded. |
| `noFields`, `missingIds`, `pickerUnavailable`, `network`, `aborted` | See `src/errors.ts`. |

## What's gathered

- **Fields**: `getCapabilities` → `getDataFields(role)`, in visual order. Fields hidden
  on the visual are sent with `hidden: true`: the server computes them but doesn't
  write them. Hierarchy levels are sent as `{ table, hierarchy, hierarchyLevel }`.
  Implicit aggregations keep the SDK's names: `Count` is **Count (Distinct)**, and
  `CountNonNull` is Count.
- **Filters**: report, page and visual filters, plus the page's slicer selections. If a
  slicer isn't rendered yet, the client activates its page. If the SDK can't serialize
  a slicer, or a level's filters (a filter-pane card in the "Select all" state makes
  `getFilters()` fail for its whole level), the client falls back to the bookmark
  state. It translates `In` / `NotIn` value lists (including multi-column exclusions
  from hierarchy slicers) and "Select all" from there, and
  stops on anything else. "Select all" is sent as an `All` filter marked
  `selectAll: true`, so the server can set a bound dynamic M parameter to its
  select-all value.
- **Time zone**: the browser's IANA time zone (`timeZone`). The SDK sends date filter
  values as UTC instants of the browser's local time; the server converts them back.
- **Sort** (only with `sort: true`): read from `bookmarksManager.capture()`, which holds
  the live sort or the saved default.

## Development

```pwsh
npm ci
npm run build     # typecheck, emit .d.ts, bundle → dist/ and ../PbiVisualExport/Client/
npm test          # node --test with a mocked powerbi-client
```

`npm run build` copies the bundles into the .NET library, which embeds them. Commit
both. CI fails if `src/PbiVisualExport/Client` is out of date. Bump `version` in
`package.json` and `VersionPrefix` in `PbiVisualExport.csproj` together.
