# Test reports (`infra/reports/`)

Hand-authored **modern PBIR** (enhanced report format) reports that exercise the
CSV exporter against deliberately varied semantic-model features. Every report
here is enhanced PBIR — `definition/pages/**/visuals/**/visual.json` per visual,
never a single legacy `report.json` blob — so report design is **inspectable**
(see the *Report-level measures* note below).

Deploy or update any of these with `infra/deploy-model.ps1 -Kind reports`, which
base64-encodes every part under the folder. To **replace an existing report in
place** (keeping its id so embedding keeps working) pass `-ItemId <id>`
(`updateDefinition`); add `-UpdateMetadata` to also apply the `.platform`
description (`updateDefinition?updateMetadata=true`). Without `-ItemId` it creates
a new report.

## Reports

| Folder | Bound model | Format | Exercises | Visual types |
|---|---|---|---|---|
| `StoreSales` | `StoreSales` (`ba3d016a-…`) | Enhanced PBIR | Variety of visuals + **report-level measures** | tableEx, matrix (`pivotTable`), clusteredColumnChart, clusteredBarChart, lineChart, donutChart, card, slicer |
| `FieldParamReport` | `FieldParamTest` (`cbfd0189-…`) | Enhanced PBIR | **Field parameter** — a slicer selects which fields `fpTable` projects; `table1` shows the parameter column itself | tableEx, slicer |
| `DqParamReport` | `DqParamTest` (`2272685b-…`) | Enhanced PBIR | **Dynamic M query parameter** over a DirectQuery model | tableEx, slicer |
| `LargeExportReport` | `LargeExportTest` (`13bf1c3f-…`) | Enhanced PBIR | **2M rows × 31 columns**, every data type + many format strings; native vs API export comparison | tableEx, matrix (`pivotTable`), card, slicer |

> The dataset a report binds to is set in its `definition.pbir`
> (`datasetReference.byConnection.pbiModelDatabaseName`). If a model is dropped and
> recreated its id changes — update the `.pbir` and redeploy, or the service
> returns `PowerBIEntityNotFound` on `updateDefinition`.

Each report's `.platform` carries a `metadata.displayName` **and** a
`metadata.description`; the description round-trips to the workspace when you
deploy with `updateMetadata=true`. Keep descriptions accurate — they document
*why* the report exists.

### StoreSales — the variety report

Three pages over the StoreSales retail model:

- **Page 1 "Sales Overview"** — `tableEx` (Chain, Store Type, TotalSales,
  TotalUnits), `clusteredColumnChart` (Category × TotalSales), `clusteredBarChart`
  (Territory × TotalSales), `card` (Total Stores), `slicer` (Segment, dropdown).
- **Page 2 "Trends & Mix"** — `matrix`/`pivotTable` (Chain rows × FiscalYear
  columns × TotalSales), `lineChart` (FiscalMonth × TotalSales), `donutChart`
  (Segment × TotalSales).
- **Page 3 "Report Measures"** — `rmTable` tableEx (Chain, Store Type, TotalSales,
  and the report measures `Sales per Store` and `Sales per Store (K)`) with a saved
  sort on **`Sales per Store` descending**, so the sort key is itself a report
  measure; `rmCard` card (`Sales per Store`; cards aren't offered for export).

It is the default report the app embeds (`PowerBI:ReportId`, set in user secrets
locally or `PowerBI__ReportId` when deployed) because it renders a bit of everything.

### FieldParamReport — the field-parameter report

One page over `FieldParamTest`, whose `Fields` calc table is a Desktop-shaped field
parameter (Category / Segment / Product Name → `Product[Category|Segment|Product]`):

- **`slicer1`** — dropdown on `Fields[Fields]` with *Select all*. Its filter
  targets the hidden `Fields[Fields Fields]` column (values like `'Product'[Segment]`).
- **`fpTable`** — "Sales by selected field(s)": a tableEx whose `Values` role has a
  `fieldParameters` entry, so its columns **swap** with the slicer selection
  (`getDataFields` returns the real `Product` columns + the two measures).
- **`table1`** — the parameter column itself plus measures (one row per label,
  identical totals). Exporting it requires grouping on the `relatedColumnDetails`
  key column, which `DaxQueryBuilder` adds implicitly.

### LargeExportReport — the scale / fidelity report

Generated (with its model `infra/models/LargeExportTest`) by
**`infra/generate-large-export.py`** — edit the column spec there and re-run;
don't hand-edit the output. The `Orders` table is a deterministic DAX calculated
table (2,000,000 rows), so exports can be diffed row-by-row on `Row Id`.

- **Page 1 "Wide Detail"** — `detail1` tableEx with **all 31 columns** (int64,
  decimal, double, currency, percent, scientific, custom `°C`, zero-padded text
  codes, date/time/datetime with several format strings, boolean, nullable,
  CSV-hostile strings, int64 > 2^53, and one **model-hidden** column, `Internal Hash`,
  which the visual still shows, so both native and API exports include it) and a saved
  `sortDefinition` (**Row Id ascending** — exercises sort detection), `card1` (Order
  Count), `card2` (Total Sales), `slicer1` (Region, dropdown).
- **Page 2 "Summary"** — `summary1` tableEx (Region, Category, implicit Sum/Average
  and explicit measures; **no saved sort**, so it exercises the implicit-order
  path — reorder columns or click a header in-session to test live sort/order),
  `matrix1` (Order Month × Region × Total Sales), `slicer2` (Order Year, list).
- **Page 3 "Select All Slicers"** — slicers with *Select all* enabled (all,
  all-but-one, a year subset, and a Region › Category **hierarchy** slicer with one
  exclusion) over `table3` (titled with the expected row count, 70).

Compare the API export with Power BI's own export using
`python infra/compare-exports.py api.csv native.csv`. A ~2,000-row native export
is enough to check data types and format strings. Expected, by-design differences
are listed under *API vs native exportData* in `.github/copilot-instructions.md`.

## Report-level measures (`reportExtensions.json`)

`StoreSales/definition/reportExtensions.json` defines two **report-scoped** measures
on the `Sales` table:

```
Sales per Store     = DIVIDE ( [TotalSales], [Total Stores] )           -- \$#,0.00;(\$#,0.00);\$#,0.00
Sales per Store (K) = // report measure built on another report measure
                      [Sales per Store] / 1000                           -- \$#,0.0"K"
```

These measures live in the *report*, not the semantic model — they are only
visible by reading the report definition (enhanced PBIR), which is exactly why
every report here is authored in that format.

The exporter supports them: when a visual uses a measure the model doesn't have,
it downloads (and caches) the report definition, finds the measure in
`reportExtensions.json`, and adds it — with the report measures it references,
dependencies first — to the export query as `DEFINE MEASURE 'Sales'[…] = …`.
`Sales per Store (K)` exists to test exactly that dependency, and its leading `//`
comment tests that a commented expression doesn't break the generated DAX. A
report in PBIR-Legacy format fails with a 400 asking to convert it. See
*Report-level measures* in `.github/copilot-instructions.md`.

## Authoring notes (proven here)

- Match the **schema versions** the service accepts. Working set: `definition.pbir`
  `version` `"4.0"` and **no** top-level `$schema`; `version.json`
  `versionMetadata/1.0.0`; `report.json` `report/1.0.0`; `page.json` `page/1.0.0`;
  `pages.json` `pagesMetadata/1.0.0`; `visual.json` `visualContainer/1.0.0`;
  `reportExtensions.json` `reportExtension/1.0.0`. Newer versions (e.g. `4.0.0`,
  `2.0.0`) fail with `Workload_FailedToParseFile` / "Can't resolve schema".
- A **visual** needs only its `query.queryState` to render; the large `objects` /
  `visualContainerObjects` styling blocks are optional. Projection **roles** by
  visual: table/card/slicer → `Values`; cartesian & line/pie/donut charts →
  `Category` + `Y` (+ optional `Series`); matrix (`pivotTable`) → `Rows` +
  `Columns` + `Values`. Each projection carries `field` (`Column` or `Measure`
  with `Expression.SourceRef.Entity` + `Property`), `queryRef` (`Entity.Property`)
  and `nativeQueryRef` (the display name).
- **Never set `"active": true` on a `tableEx` (table) Values projection.** The
  table then renders **only that column** — the others remain in the definition
  (so the API still exports them) but are invisible, and native `exportData`
  fails with "Visual has no data". `active` is only for drill levels: slicer
  `Values`, matrix `Rows`, chart `Category`.
- `getDefinition?format=PBIR` **fails on PBIX-imported (legacy) reports** — author
  enhanced PBIR from these templates rather than round-tripping a legacy report.
