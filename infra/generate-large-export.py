"""Generates the LargeExportTest wide table (TMDL) and the complete LargeExportReport (PBIR).

The column spec below is the single source of truth for both artifacts:
  infra/models/LargeExportTest/definition/tables/Orders.tmdl
  infra/reports/LargeExportReport/**   (every file: .platform, definition.pbir, pages, visuals)

Rows are generated deterministically in a DAX calculated table (GENERATESERIES + hash-like
MOD arithmetic on the row number), so every refresh produces identical data and exports can
be diffed row-by-row on [Row Id]. Re-run after editing the spec:

    python infra/generate-large-export.py
"""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent
ROWS = 2_000_000
# Deployed LargeExportTest semantic model the report binds to (definition.pbir byConnection).
DATASET_ID = "00000000-0000-0000-0000-000000000018"
SCHEMA = "https://developer.microsoft.com/json-schemas/fabric/item/report/definition/"
CURRENCY_FMT = r"\$#,0.00;(\$#,0.00);\$#,0.00"
ORDER_DATE = "DATE(2020, 1, 1) + MOD([@h1], 2192)"
ORDER_TIME = "TIME(MOD([@h3], 24), MOD([@h4], 60), MOD([@h5], 60))"

# (name, tmdl dataType, formatString | None, summarizeBy, isHidden, DAX expression)
COLUMNS = [
    ("Row Id", "int64", "0", "none", False, "CONVERT([Value], INTEGER)"),
    ("Order Number", "string", None, "none", False, '"ORD-" & FORMAT([Value], "0000000")'),
    ("Region", "string", None, "none", False,
     'SWITCH(MOD([Value], 4), 0, "North", 1, "South", 2, "East", "West")'),
    ("Category", "string", None, "none", False,
     'SWITCH(MOD([@h1], 10), 0, "Hardware", 1, "Software", 2, "Services", 3, "Accessories", '
     '4, "Furniture", 5, "Office Supplies", 6, "Electronics", 7, "Apparel", 8, "Grocery", "Toys")'),
    # CSV-hostile values: comma, embedded quotes, accents, unicode, newline, tab, padding, leading '='.
    ("Product Name", "string", None, "none", False,
     'SWITCH(MOD([@h2], 12), 0, "Bolt, Hex 10mm", 1, "Pipe 3/4"" Copper", 2, "Café Crème Mug", '
     '3, "Ünïcødé Widget ✓", 4, "日本製 Knife", 5, "Multi-line" & UNICHAR(10) & "Label", '
     '6, "Quote ""Special"" Edition", 7, "Semi;Colon Kit", 8, "Tab" & UNICHAR(9) & "Separated", '
     '9, "  Padded Name  ", 10, "=1+1 Formula Text", "Standard Widget")'),
    ("Customer Name", "string", None, "none", False, '"Customer " & FORMAT(MOD([@h3], 50000), "00000")'),
    ("Postal Code", "string", None, "none", False, 'FORMAT(MOD([@h4], 100000), "00000")'),
    ("Quantity", "int64", "#,0", "sum", False, "CONVERT(1 + MOD([@h1], 500), INTEGER)"),
    ("Unit Price", "decimal", CURRENCY_FMT, "none", False,
     "CONVERT(0.99 + MOD([@h2], 100000) / 100, CURRENCY)"),
    ("Discount Pct", "double", "0.00%", "none", False, "CONVERT(MOD([@h3], 3001) / 10000, DOUBLE)"),
    ("Sales Amount", "decimal", CURRENCY_FMT, "sum", False,
     "CONVERT((1 + MOD([@h1], 500)) * (0.99 + MOD([@h2], 100000) / 100) * (1 - MOD([@h3], 3001) / 10000)"
     " * IF(MOD([@h5], 20) = 0, -1, 1), CURRENCY)"),
    ("Unit Cost", "double", "#,0.0000", "none", False,
     "CONVERT((0.99 + MOD([@h2], 100000) / 100) * (0.4 + MOD([@h4], 5000) / 10000), DOUBLE)"),
    ("Margin Ratio", "double", "0.000;-0.000;0", "none", False,
     "CONVERT((MOD([@h5], 2001) - 1000) / 1000, DOUBLE)"),
    ("Weight Kg", "double", None, "none", False, "CONVERT([@h4] / 1000003, DOUBLE)"),
    ("Scientific Value", "double", "0.00E+00", "none", False,
     "CONVERT(POWER(10, MOD([@h1], 19) - 6) * (1 + MOD([@h2], 1000) / 1000), DOUBLE)"),
    # Exceeds 2^53: detects precision loss anywhere values pass through a double / JSON number.
    ("Big Integer", "int64", None, "none", False, "CONVERT(9007199254740000 + [Value], INTEGER)"),
    ("Priority Code", "int64", "000000", "none", False, "CONVERT(MOD([@h3], 1000), INTEGER)"),
    ("Rating", "double", "0.0", "none", False, "CONVERT(1 + MOD([@h4], 41) / 10, DOUBLE)"),
    ("Tax Rate", "decimal", "0.0%", "none", False, "CONVERT(MOD([@h5], 13) / 100, CURRENCY)"),
    ("Temperature", "double", '#,0.0" °C"', "none", False, "CONVERT((MOD([@h2], 901) - 400) / 10, DOUBLE)"),
    ("Contract Value", "decimal", r"\$#,0", "none", False, "CONVERT([@h1] * 1.2345, CURRENCY)"),
    ("Order Date", "dateTime", "yyyy-mm-dd", "none", False, f"CONVERT({ORDER_DATE}, DATETIME)"),
    ("Ship Date", "dateTime", "Long Date", "none", False,
     f"CONVERT({ORDER_DATE} + MOD([@h2], 15), DATETIME)"),
    ("Order Timestamp", "dateTime", "yyyy-mm-dd hh:nn:ss", "none", False,
     f"CONVERT({ORDER_DATE} + {ORDER_TIME}, DATETIME)"),
    ("Order Time", "dateTime", "h:nn:ss AM/PM", "none", False, f"CONVERT({ORDER_TIME}, DATETIME)"),
    ("Is Returned", "boolean", None, "none", False, "MOD([@h5], 20) = 0"),
    ("Nullable Amount", "double", "#,0.00", "none", False,
     "IF(MOD([Value], 7) = 0, BLANK(), CONVERT(MOD([@h4], 1000000) / 100, DOUBLE))"),
    ("Notes", "string", None, "none", False,
     'SWITCH(MOD([@h3], 5), 0, BLANK(), 1, "", 2, "Rush order", '
     '3, "Gift wrap, with ""fragile"" sticker", "N/A")'),
    ("Order Year", "int64", "0", "none", False, f"CONVERT(YEAR({ORDER_DATE}), INTEGER)"),
    ("Order Month", "string", None, "none", False, f'FORMAT({ORDER_DATE}, "yyyy-mm")'),
    # Hidden in the model but placed in the visual: the visual shows it, so native export
    # and the API both include it.
    ("Internal Hash", "string", None, "none", True, 'FORMAT([@h1], "0000000000")'),
]

MEASURES = [
    ("Order Count", "COUNTROWS(Orders)", "#,0"),
    ("Total Sales", "SUM(Orders[Sales Amount])", CURRENCY_FMT),
    ("Total Quantity", "SUM(Orders[Quantity])", "#,0"),
    ("Avg Discount", "AVERAGE(Orders[Discount Pct])", "0.00%"),
    ("Return Rate",
     "DIVIDE(CALCULATE(COUNTROWS(Orders), Orders[Is Returned] = TRUE()), COUNTROWS(Orders))", "0.0%"),
]


def tmdl_name(name: str) -> str:
    return f"'{name}'" if any(c in name for c in " .=:'") else name


def lineage(i: int) -> str:
    return f"55555555-0000-0000-0000-{i:012d}"


def build_tmdl() -> str:
    t = ["table Orders", f"\tlineageTag: {lineage(1)}", ""]
    for i, (name, expr, fmt) in enumerate(MEASURES):
        t += [f"\tmeasure {tmdl_name(name)} = {expr}",
              f"\t\tformatString: {fmt}",
              f"\t\tlineageTag: {lineage(100 + i)}", ""]
    for i, (name, dtype, fmt, summ, hidden, _) in enumerate(COLUMNS):
        t.append(f"\tcolumn {tmdl_name(name)}")
        t.append(f"\t\tdataType: {dtype}")
        if hidden:
            t.append("\t\tisHidden")
        if fmt:
            t.append(f"\t\tformatString: {fmt}")
        t += [f"\t\tlineageTag: {lineage(200 + i)}",
              f"\t\tsummarizeBy: {summ}",
              f"\t\tsourceColumn: [{name}]",
              "",
              "\t\tannotation SummarizationSetBy = Automatic",
              ""]
    body = ",\n".join(f'\t\t"{name}", {expr}' for name, _, _, _, _, expr in COLUMNS)
    dax = (
        f"VAR _base = GENERATESERIES(1, {ROWS}, 1)\n"
        "VAR _h =\n"
        "\tADDCOLUMNS(\n"
        "\t\t_base,\n"
        '\t\t"@h1", MOD([Value] * 48271, 2147483647),\n'
        '\t\t"@h2", MOD([Value] * 69621, 2147483647),\n'
        '\t\t"@h3", MOD([Value] * 16807, 2147483647),\n'
        '\t\t"@h4", MOD([Value] * 39373, 2147483647),\n'
        '\t\t"@h5", MOD([Value] * 40692, 2147483399)\n'
        "\t)\n"
        "RETURN\n"
        "\tSELECTCOLUMNS(\n"
        "\t\t_h,\n"
        f"{body}\n"
        "\t)"
    )
    t += ["\tpartition Orders = calculated", "\t\tmode: import", "\t\tsource ="]
    t += ["\t\t\t\t" + line for line in dax.split("\n")]
    t += ["", "\tannotation PBI_ResultType = Table", ""]
    return "\n".join(t)


def col_field(name: str) -> dict:
    return {"Column": {"Expression": {"SourceRef": {"Entity": "Orders"}}, "Property": name}}


def col_proj(name: str, active: bool = False) -> dict:
    # `active` marks the current drill level (slicers, matrix rows). Never set it on a
    # tableEx projection: the table then renders ONLY that column.
    p = {"field": col_field(name), "queryRef": f"Orders.{name}", "nativeQueryRef": name}
    if active:
        p["active"] = True
    return p


def measure_proj(name: str) -> dict:
    return {"field": {"Measure": {"Expression": {"SourceRef": {"Entity": "Orders"}}, "Property": name}},
            "queryRef": f"Orders.{name}", "nativeQueryRef": name}


# Implicit aggregation codes (QueryAggregateFunction): 0=Sum, 1=Avg, 3=Min, 4=Max.
AGG_NAMES = {0: ("Sum", "Sum"), 1: ("Avg", "Average"), 3: ("Min", "Min"), 4: ("Max", "Max")}


def agg_proj(name: str, func: int) -> dict:
    ref, label = AGG_NAMES[func]
    return {"field": {"Aggregation": {"Expression": col_field(name), "Function": func}},
            "queryRef": f"{ref}(Orders.{name})", "nativeQueryRef": f"{label} of {name}"}


def title_obj(text: str) -> dict:
    return {"title": [{"properties": {
        "show": {"expr": {"Literal": {"Value": "true"}}},
        "text": {"expr": {"Literal": {"Value": f"'{text}'"}}},
    }}]}


def visual(name: str, vtype: str, pos: tuple, query_state: dict, title: str | None = None,
           extra: dict | None = None) -> dict:
    x, y, z, w, h = pos
    v = {"visualType": vtype, "query": {"queryState": query_state}}
    if title:
        v["visualContainerObjects"] = title_obj(title)
    if extra:
        v.update(extra)
    v["drillFilterOtherVisuals"] = True
    return {
        "$schema": SCHEMA + "visualContainer/1.0.0/schema.json",
        "name": name,
        "position": {"x": x, "y": y, "z": z, "width": w, "height": h, "tabOrder": z},
        "visual": v,
    }


def lit(value: str) -> dict:
    return {"expr": {"Literal": {"Value": value}}}


def slicer_filter(columns: list[str], values: list[list[str]], negate: bool) -> dict:
    """Saved slicer selection (objects.general.filter). Each entry of `values` is one tuple of PBIR
    literals ("'Toys'", "2024L") matching `columns`. Multi-column tuples = hierarchy slicer."""
    cond = {"In": {
        "Expressions": [{"Column": {"Expression": {"SourceRef": {"Source": "o"}}, "Property": c}}
                        for c in columns],
        "Values": [[{"Literal": {"Value": v}} for v in tup] for tup in values],
    }}
    if negate:
        cond = {"Not": {"Expression": cond}}
    return {"filter": {"Version": 2, "From": [{"Name": "o", "Entity": "Orders", "Type": 0}],
                       "Where": [{"Condition": cond}]}}


def slicer(name: str, column: str | list[str], pos: tuple, dropdown: bool, title: str | None = None,
           select_all: bool = False, inverted: bool = False,
           values: list[str] | list[list[str]] | None = None) -> dict:
    """A slicer on Orders[column] (or a hierarchy slicer when `column` is a list).

    select_all shows the "Select all" item. inverted=True is the state Power BI saves after the
    user checks "Select all": everything is selected except `values` (NOT IN), so inverted with
    no values means "Select all" is checked. Otherwise `values` is a plain IN selection.
    For a hierarchy slicer, each value is a tuple (list) with one literal per level.
    """
    columns = [column] if isinstance(column, str) else column
    tuples = [v if isinstance(v, list) else [v] for v in (values or [])]
    objects: dict = {}
    if dropdown:
        objects["data"] = [{"properties": {"mode": lit("'Dropdown'")}}]
    if select_all:
        objects["selection"] = [{"properties": {"selectAllCheckboxEnabled": lit("true")}}]
    general: dict = {}
    if inverted:
        general["isInvertedSelectionMode"] = lit("true")
    if tuples:
        general["filter"] = slicer_filter(columns, tuples, negate=inverted)
    if general:
        objects["general"] = [{"properties": general}]
    projections = [col_proj(c, True) for c in columns]
    return visual(name, "slicer", pos, {"Values": {"projections": projections}},
                  title=title or " > ".join(columns), extra={"objects": objects} if objects else None)


def build_detail_visual() -> dict:
    projections = [col_proj(name) for name, *_rest in COLUMNS]
    return {
        "$schema": SCHEMA + "visualContainer/1.0.0/schema.json",
        "name": "detail1",
        "position": {"x": 20, "y": 110, "z": 3, "width": 1240, "height": 590, "tabOrder": 3},
        "visual": {
            "visualType": "tableEx",
            "query": {
                "queryState": {"Values": {"projections": projections}},
                "sortDefinition": {
                    "sort": [{"field": col_field("Row Id"), "direction": "Ascending"}],
                    "isDefaultSort": True,
                },
            },
            "visualContainerObjects": title_obj("Order Detail (2M rows, all columns)"),
            "drillFilterOtherVisuals": True,
        },
    }


def build_report_files() -> dict[str, object]:
    """Every file of the LargeExportReport PBIR definition, keyed by path relative to the report folder."""
    files: dict[str, object] = {
        ".platform": {
            "$schema": "https://developer.microsoft.com/json-schemas/fabric/gitIntegration/platformProperties/2.0.0/schema.json",
            "metadata": {
                "type": "Report",
                "displayName": "LargeExportReport",
                "description": "2M-row wide-table report on LargeExportTest. Compares Power BI's native "
                               "export (capped) with the API's unbounded CSV export, across data types "
                               "and format strings. Generated by infra/generate-large-export.py.",
            },
            "config": {"version": "2.0", "logicalId": "00000000-0000-0000-0000-000000000000"},
        },
        "definition.pbir": {
            "version": "4.0",
            "datasetReference": {
                "byPath": None,
                "byConnection": {
                    "connectionString": None,
                    "pbiServiceModelId": None,
                    "pbiModelVirtualServerName": "sobe_wowvirtualserver",
                    "pbiModelDatabaseName": DATASET_ID,
                    "name": "EntityDataSource",
                    "connectionType": "pbiServiceXmlaStyleLive",
                },
            },
        },
        "definition/version.json": {"$schema": SCHEMA + "versionMetadata/1.0.0/schema.json", "version": "2.0.0"},
        "definition/report.json": {
            "$schema": SCHEMA + "report/1.0.0/schema.json",
            "themeCollection": {"baseTheme": {"name": "CY22SU11", "reportVersionAtImport": "5.40",
                                              "type": "SharedResources"}},
            "layoutOptimization": "None",
            "resourcePackages": [{"name": "SharedResources", "type": "SharedResources", "items": [
                {"name": "CY22SU11", "path": "BaseThemes/CY22SU11.json", "type": "BaseTheme"}]}],
            "settings": {
                "useStylableVisualContainerHeader": True,
                "defaultDrillFilterOtherVisuals": True,
                "exportDataMode": "AllowSummarizedAndUnderlying",
            },
        },
        "definition/pages/pages.json": {
            "$schema": SCHEMA + "pagesMetadata/1.0.0/schema.json",
            "pageOrder": ["Page1", "Page2", "Page3"],
            "activePageName": "Page1",
        },
    }
    for page, display in (("Page1", "Wide Detail"), ("Page2", "Summary"), ("Page3", "Select All Slicers")):
        files[f"definition/pages/{page}/page.json"] = {
            "$schema": SCHEMA + "page/1.0.0/schema.json",
            "name": page, "displayName": display, "displayOption": "FitToPage",
            "height": 720, "width": 1280,
        }

    p1 = "definition/pages/Page1/visuals"
    files[f"{p1}/slicer1/visual.json"] = slicer("slicer1", "Region", (20, 10, 0, 300, 90), dropdown=True)
    files[f"{p1}/card1/visual.json"] = visual(
        "card1", "card", (340, 10, 1, 280, 90), {"Values": {"projections": [measure_proj("Order Count")]}})
    files[f"{p1}/card2/visual.json"] = visual(
        "card2", "card", (640, 10, 2, 280, 90), {"Values": {"projections": [measure_proj("Total Sales")]}})
    files[f"{p1}/detail1/visual.json"] = build_detail_visual()

    p2 = "definition/pages/Page2/visuals"
    files[f"{p2}/slicer2/visual.json"] = slicer("slicer2", "Order Year", (20, 20, 0, 220, 300), dropdown=False)
    files[f"{p2}/summary1/visual.json"] = visual(
        "summary1", "tableEx", (260, 20, 1, 1000, 300),
        {"Values": {"projections": [
            col_proj("Region"), col_proj("Category"),
            agg_proj("Quantity", 0), agg_proj("Unit Price", 1),
            measure_proj("Total Sales"), measure_proj("Avg Discount"), measure_proj("Return Rate"),
        ]}},
        title="Summary by Region and Category (implicit + explicit measures)")
    files[f"{p2}/matrix1/visual.json"] = visual(
        "matrix1", "pivotTable", (20, 340, 2, 1240, 360),
        {"Rows": {"projections": [col_proj("Order Month", True)]},
         "Columns": {"projections": [col_proj("Region")]},
         "Values": {"projections": [measure_proj("Total Sales")]}},
        title="Total Sales by Order Month and Region")

    # "Select all" slicers: getSlicerState() can fail with slicerSerializationFailed for these.
    # Single-column "Select all" (with or without exclusions) serializes fine (empty / NotIn);
    # the hierarchy slicer with "Select all" + an excluded leaf is the known failure case.
    # Expected export of table3: 2 years x (4 regions x 9 categories - North/Electronics) = 70 rows.
    p3 = "definition/pages/Page3/visuals"
    files[f"{p3}/slicer3a/visual.json"] = slicer(
        "slicer3a", "Region", (20, 20, 0, 290, 320), dropdown=False,
        title="Region (Select all)", select_all=True, inverted=True)
    files[f"{p3}/slicer3b/visual.json"] = slicer(
        "slicer3b", "Category", (325, 20, 1, 290, 320), dropdown=False,
        title="Category (Select all, then Toys cleared)", select_all=True, inverted=True, values=["'Toys'"])
    files[f"{p3}/slicer3c/visual.json"] = slicer(
        "slicer3c", "Order Year", (630, 20, 2, 290, 320), dropdown=False,
        title="Order Year (2024 + 2025)", select_all=True, values=["2024L", "2025L"])
    files[f"{p3}/slicer3d/visual.json"] = slicer(
        "slicer3d", ["Region", "Category"], (935, 20, 3, 325, 320), dropdown=False,
        title="Region > Category (Select all, then North > Electronics cleared)",
        select_all=True, inverted=True, values=[["'North'", "'Electronics'"]])
    files[f"{p3}/table3/visual.json"] = visual(
        "table3", "tableEx", (20, 360, 4, 1240, 340),
        {"Values": {"projections": [
            col_proj("Order Year"), col_proj("Region"), col_proj("Category"),
            measure_proj("Order Count"), measure_proj("Total Sales"),
        ]}},
        title="Orders by Year, Region, Category (expect 70 rows)")
    return files


def main() -> None:
    tmdl_path = ROOT / "models/LargeExportTest/definition/tables/Orders.tmdl"
    tmdl_path.write_text(build_tmdl(), encoding="utf-8", newline="\n")
    report_root = ROOT / "reports/LargeExportReport"
    files = build_report_files()
    for rel, content in files.items():
        path = report_root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(content, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
    print(f"Wrote {tmdl_path}\nWrote {len(files)} report files under {report_root}\n"
          f"{len(COLUMNS)} columns, {len(MEASURES)} measures")


if __name__ == "__main__":
    main()
