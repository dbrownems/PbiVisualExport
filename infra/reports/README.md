# Test reports (`infra/reports/`)

Hand-authored **modern PBIR** (enhanced report format) reports that exercise the
CSV exporter against deliberately varied semantic-model features. Every report
here is enhanced PBIR — `definition/pages/**/visuals/**/visual.json` per visual,
never a single legacy `report.json` blob — so report design is **inspectable**
(see the *Report-level measures* note below).

To deploy everything (Warehouse, models, reports, refresh) to a workspace, run
`infra/deploy-all.ps1`; it matches items by display name and updates existing ones
in place. Then start the sample host and run `infra/smoke-export.ps1` to check an
export end to end. Deploy or update a single report with `infra/deploy-model.ps1 -Kind reports`, which
base64-encodes every part under the folder. The target workspace comes from the
`PowerBI:WorkspaceId` setting (user secrets or `PowerBI__WorkspaceId`), unless you
pass `-Workspace`. To **replace an existing report in
place** (keeping its id so embedding keeps working) pass `-ItemId <id>`
(`updateDefinition`); add `-UpdateMetadata` to also apply the `.platform`
description (`updateDefinition?updateMetadata=true`). Without `-ItemId` it creates
a new report.

## Reports

| Folder | Bound model (display name) | Format | Exercises | Visual types |
|---|---|---|---|---|
| `StoreSales` | `StoreSales` | Enhanced PBIR | Variety of visuals + **report-level measures** | tableEx, matrix (`pivotTable`), clusteredColumnChart, clusteredBarChart, lineChart, donutChart, card, slicer |
| `FieldParamReport` | `FieldParamTest` | Enhanced PBIR | **Field parameter** — a slicer selects which fields `fpTable` projects; `table1` shows the parameter column itself | tableEx, slicer |
| `DqParamReport` | `DqParamReport` (deployed from `models/DqParamTest`) | Enhanced PBIR | **Dynamic M query parameters** (single-value, multi-select with *Select all*, Date and DateTime) bound to slicers over a DirectQuery model | tableEx, slicer |
| `LargeExportReport` | `LargeExportTest` | Enhanced PBIR | **2M rows × 31 columns**, every data type + many format strings; native vs API export comparison | tableEx, matrix (`pivotTable`), card, slicer |

> The dataset a report binds to is set in its `definition.pbir`
> (`datasetReference.byConnection.pbiModelDatabaseName`). That value is a
> **deploy-time token**, `{{SemanticModelId:<model display name>}}`, not an id.
> `deploy-model.ps1` looks the name up in the target workspace and substitutes the
> id in the upload, so a dropped-and-recreated model (new id) or a fresh workspace
> needs no file edits. Just deploy the model before its report. Unknown or
> duplicate names fail the deploy up front. Otherwise the service returns
> `PowerBIEntityNotFound` on `updateDefinition`. Use `-WhatIf` to see the
> resolved ids without deploying. The `DqParamTest` model folder deploys as
> **`DqParamReport`** (its `.platform` displayName), which is why that report's
> token names `DqParamReport`.

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

### DqParamReport — the dynamic M parameter report

One page over the DirectQuery model `DqParamReport` (folder `models/DqParamTest`).
Its `Sales` table is a native SQL query against the `ExportTestWH` Warehouse whose
`WHERE` clause is built from four M parameters, each bound to a disconnected Import
picker table:

| Parameter | Default | Bound column | Binding |
|---|---|---|---|
| `RegionParam` | `"East"` | `RegionPicker[RegionChoice]` (East, West, North, South) | single value |
| `CategoryParam` | `"Accessories"` | `CategoryPicker[CategoryChoice]` (Accessories, Bikes, Clothing) | multi-select (`supportsMultipleValues`, `selectAllValue` `"__SelectAll__"` = no category filter) |
| `FromDateParam` | `#date(2024, 1, 1)` | `DatePicker[FromDate]` (the 1st of Jan–May 2024) | single value, `Type="Date"`: `SaleDate >= FromDate` |
| `ToDateTimeParam` | `#datetime(2024, 12, 31, 23, 59, 59)` | `DateTimePicker[ToDateTime]` (2024-01-11 06:00, 2024-04-04 00:00:00 and 00:00:01, 2024-12-31 23:59:59) | single value, `Type="DateTime"`: `SaleDate < ToDateTime` |

- **`table1`** — Category, Product, SaleDate, Total Quantity, Total Amount from
  `Sales`. With the saved slicer states it shows East's Accessories (Helmet, Tire).
- **`slicer1`** — single-select dropdown on `RegionPicker[RegionChoice]`.
- **`slicer2`** — multi-select list on `CategoryPicker[CategoryChoice]` with *Select
  all*. Its saved state (inverted mode, nothing excluded) sends no parameter value, so
  the default applies. The default isn't the select-all value on purpose: an
  interactive *Select all* must send `"__SelectAll__"` to show all 5 East rows.
- **`slicer3`** / **`slicer4`** — single-select dropdowns on `DatePicker[FromDate]`
  and `DateTimePicker[ToDateTime]`. The date-time values straddle East's Road-250 sale
  (2024-04-04 00:00): `00:00:00` excludes it, `00:00:01` includes it.

Each region has 5 sales, so an export that ignores the parameters shows East's rows
whatever the slicers say. West + Bikes, Clothing gives 4 rows (Mountain-500,
Road-150, Gloves, Vest). The M wraps the date parameter in `DateTime.Date(…)`:
Power BI (and the module) set it as `DATE(…)`, which M receives as a datetime. The
golden DAX case `dq-table1-mparameter-slicers` covers the query shape.

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
- **Page 4 "Hierarchies"** — the model's user hierarchies, *Sales Geography*
  (Area › Line = Region › Category) and *Order Calendar* (Year › Month), have level
  names that differ from their columns, so a level can only be resolved through the
  hierarchy. `slicer4a` is a Sales Geography hierarchy slicer with *Select all* and no
  saved selection (tests set slicer states and level filters through the SDK) over
  `table4` (Year, Region, Category, Order Count; 240 rows unfiltered). `table4b` has
  hierarchy **levels as fields** (Year, Month, Area, Count of Line) with a saved sort
  on the Month level, descending (288 rows).
- **Page 5 "Aggregations"** — `table5`: Customer Name, then `Nullable Amount` (which
  has BLANKs) with **every implicit aggregation** (codes 0–8: Sum, Average,
  Count (Distinct), Min, Max, Count, Median, Standard deviation, Variance), Count of the
  Boolean `Is Returned`, and Order Count. There are about 40 rows per customer (999
  customers), so sample and population statistics, and BLANK handling, give
  different results.

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
it reads the (cached) report definition — already loaded for field validation
unless `Export:FieldValidation` is `Off` — finds the measure in
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
