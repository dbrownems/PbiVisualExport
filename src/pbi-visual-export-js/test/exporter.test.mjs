import { test, describe, afterEach, mock } from "node:test";
import assert from "node:assert/strict";
import { createExporter, PbiExportError, CONTRACT_VERSION, VERSION } from "../dist/pbi-visual-export.mjs";
import {
    WS, RPT, DS, mockVisual, mockPage, mockReport, gzipBookmark, bookmarkWith, mockFetch, json,
    installPicker, uninstallPicker, installDocument, uninstallDocument,
} from "./mocks.mjs";

const quiet = { onWarning: () => { } };

function setup({ visual = mockVisual(), pageVisuals, reportOpts = {}, routes = {}, exporterOpts = {} } = {}) {
    const page = mockPage({ visuals: pageVisuals ?? [visual] });
    const report = mockReport({ pages: [page], ...reportOpts });
    const fetch = mockFetch(routes);
    const exporter = createExporter({ report, fetch, ...quiet, ...exporterOpts });
    return { page, report, fetch, exporter, visual };
}

async function rejectsWith(promise, code) {
    await assert.rejects(promise, e => {
        assert.ok(e instanceof PbiExportError, `expected PbiExportError, got ${e}`);
        assert.equal(e.code, code, e.message);
        return true;
    });
}

afterEach(() => {
    uninstallPicker();
    uninstallDocument();
    mock.timers.reset();
});

describe("exports", () => {
    test("version and contract", () => {
        assert.match(VERSION, /^\d+\.\d+\.\d+/);
        assert.equal(CONTRACT_VERSION, "1");
    });
});

describe("buildRequest", () => {
    test("maps fields (including hidden), IDs from the embed URL and all filter levels", async () => {
        const rf = { $schema: "basic", target: { table: "T", column: "R" } };
        const pf = { $schema: "basic", target: { table: "T", column: "P" } };
        const vf = { $schema: "basic", target: { table: "T", column: "V" } };
        const sf = { $schema: "basic", target: { table: "T", column: "S" }, values: ["x"] };
        const visual = mockVisual({ filters: [vf] });
        const slicer = mockVisual({ name: "s1", type: "slicer", slicerState: { filters: [sf] } });
        const page = mockPage({ visuals: [visual, slicer], filters: [pf] });
        const report = mockReport({ pages: [page], filters: [rf] });
        const exporter = createExporter({ report, datasetId: DS, fetch: mockFetch({}), ...quiet });

        const req = await exporter.buildRequest(visual);
        assert.equal(req.workspaceId, WS);
        assert.equal(req.reportId, RPT);
        assert.equal(req.datasetId, DS);
        assert.equal(req.pageName, "p1");
        assert.equal(req.visualName, "v1");
        assert.equal(req.visualTitle, "Sales");
        assert.deepEqual(req.fields, [
            { table: "T", column: "A", measure: null, aggregation: null, role: "Values", hidden: false },
            { table: "T", column: null, measure: "M", aggregation: null, role: "Values", hidden: false },
            { table: "T", column: "H", measure: null, aggregation: null, role: "Values", hidden: true },
        ]);
        assert.deepEqual(req.filters, [rf, pf, vf, sf]);
        assert.equal(req.applyFormatStrings, true);
        assert.equal(req.applySort, false);
        assert.deepEqual(req.sort, []);
        assert.equal(req.timeZone, Intl.DateTimeFormat().resolvedOptions().timeZone);
        assert.match(req.timeZone, /\S/);
    });

    test("explicit IDs win over the embed URL; unformatted", async () => {
        const { exporter, visual } = setup({ exporterOpts: { workspaceId: "w", reportId: "r" } });
        const req = await exporter.buildRequest(visual, { formatted: false });
        assert.equal(req.workspaceId, "w");
        assert.equal(req.reportId, "r");
        assert.equal("datasetId" in req, false);
        assert.equal(req.applyFormatStrings, false);
    });

    test("object-shaped targets and aggregations", async () => {
        const visual = mockVisual({ fields: { Values: [
            { column: { name: "Qty", table: "Sales" }, aggregationFunction: "Sum" },
            { measure: { name: "Margin", table: "Sales" } },
            { nothing: true },
        ] } });
        const { exporter } = setup({ visual });
        const req = await exporter.buildRequest(visual);
        assert.deepEqual(req.fields.map(f => [f.table, f.column, f.measure, f.aggregation]),
            [["Sales", "Qty", null, "Sum"], ["Sales", null, "Margin", null]]);
    });

    test("hierarchy-level targets", async () => {
        const visual = mockVisual({ roles: ["Rows"], fields: { Rows: [
            { $schema: "http://powerbi.com/product/schema#hierarchyLevel", table: "Sales", hierarchy: "Calendar", hierarchyLevel: "Year" },
            { table: "Sales", hierarchy: "Order Date", hierarchyLevel: "Month", aggregationFunction: "Count", displayName: "x" },
            { table: "Sales", hierarchy: "Calendar" },
        ] } });
        const { exporter } = setup({ visual });
        const req = await exporter.buildRequest(visual);
        assert.deepEqual(req.fields, [
            { table: "Sales", column: null, measure: null, hierarchy: "Calendar", hierarchyLevel: "Year", aggregation: null, role: "Rows", hidden: false },
            { table: "Sales", column: null, measure: null, hierarchy: "Order Date", hierarchyLevel: "Month", aggregation: "Count", role: "Rows", hidden: false },
            // A hierarchy without a level names nothing the server can read: sent as-is (the server rejects it).
            { table: "Sales", column: null, measure: null, aggregation: null, role: "Rows", hidden: false },
        ]);
    });

    test("missing IDs", async () => {
        const { exporter, visual } = setup({ reportOpts: { config: {} } });
        await rejectsWith(exporter.buildRequest(visual), "missingIds");
    });

    test("needs powerbi-report-authoring", async () => {
        const visual = mockVisual();
        delete visual.getDataFields;
        const { exporter } = setup({ visual });
        await rejectsWith(exporter.buildRequest(visual), "authoringApiMissing");
    });

    test("a visual without a page", async () => {
        const { exporter } = setup();
        await assert.rejects(exporter.buildRequest(mockVisual()), TypeError);
    });
});

describe("slicers", () => {
    const serializationFailed = async () => { throw { message: "slicerSerializationFailed" }; };

    test("unreadable slicer with no selection in the bookmark is skipped", async () => {
        const visual = mockVisual();
        const slicer = mockVisual({ name: "s1", type: "slicer", title: "Region", getSlicerState: serializationFailed });
        const bookmarkState = await gzipBookmark(bookmarkWith("p1", { s1: { singleVisual: { objects: { merge: { general: [{ properties: {} }] } } } } }));
        const { exporter } = setup({ visual, pageVisuals: [visual, slicer], reportOpts: { bookmarkState } });
        const req = await exporter.buildRequest(visual);
        assert.deepEqual(req.filters, []);
    });

    test("unreadable slicer with a selection aborts the export", async () => {
        const visual = mockVisual();
        const slicer = mockVisual({ name: "s1", type: "slicer", title: "Region", getSlicerState: serializationFailed });
        const general = [{ properties: { filter: { filter: { Version: 2 } } } }];
        const bookmarkState = await gzipBookmark(bookmarkWith("p1", { s1: { singleVisual: { objects: { merge: { general } } } } }));
        const { exporter } = setup({ visual, pageVisuals: [visual, slicer], reportOpts: { bookmarkState } });
        await assert.rejects(exporter.buildRequest(visual), e => {
            assert.equal(e.code, "slicerUnreadable");
            assert.match(e.message, /Region/);
            assert.match(e.message, /slicerSerializationFailed/);
            return true;
        });
    });

    test("unreadable slicer without a bookmark aborts the export", async () => {
        const visual = mockVisual();
        const slicer = mockVisual({ name: "s1", type: "slicer", getSlicerState: serializationFailed });
        const { exporter } = setup({ visual, pageVisuals: [visual, slicer] });
        await rejectsWith(exporter.buildRequest(visual), "slicerUnreadable");
    });

    test("page visuals that can't be listed abort the export", async () => {
        const { exporter, visual, page } = setup();
        page.getVisuals = async () => { throw { message: "boom" }; };
        await assert.rejects(exporter.buildRequest(visual), e => {
            assert.equal(e.code, "slicerUnreadable");
            assert.match(e.message, /boom/);
            return true;
        });
    });

    // The bookmark filter of a slicer after clicking "Select all", as captured from Power BI.
    const anyValue = (table, column, source) => ({
        Version: 2,
        From: [{ Name: "c", Entity: table, Type: 0 }],
        Where: [{ Condition: { Comparison: { ComparisonKind: 0, Left: { Column: { Expression: { SourceRef: source ? { Source: "c" } : { Entity: table } }, Property: column } }, Right: { AnyValue: {} } } } }],
    });

    test("unreadable slicer in the \"Select all\" state is sent as a selectAll filter", async () => {
        for (const bySource of [true, false]) {
            const visual = mockVisual();
            const slicer = mockVisual({ name: "s1", type: "slicer", getSlicerState: serializationFailed });
            const general = [{ properties: { filter: { filter: anyValue("CategoryPicker", "CategoryChoice", bySource) } } }];
            const bookmarkState = await gzipBookmark(bookmarkWith("p1", { s1: { singleVisual: { objects: { merge: { general } } } } }));
            const { exporter } = setup({ visual, pageVisuals: [visual, slicer], reportOpts: { bookmarkState } });
            const req = await exporter.buildRequest(visual);
            assert.deepEqual(req.filters, [{
                $schema: "http://powerbi.com/product/schema#basic",
                target: { table: "CategoryPicker", column: "CategoryChoice" },
                filterType: 1, operator: "All", values: [], requireSingleSelection: false, selectAll: true,
            }]);
        }
    });

    test("an AnyValue comparison mixed with another condition still aborts the export", async () => {
        const visual = mockVisual();
        const slicer = mockVisual({ name: "s1", type: "slicer", getSlicerState: serializationFailed });
        const filter = anyValue("CategoryPicker", "CategoryChoice", true);
        filter.Where.push({ Condition: { In: { Expressions: [], Values: [] } } });
        const general = [{ properties: { filter: { filter } } }];
        const bookmarkState = await gzipBookmark(bookmarkWith("p1", { s1: { singleVisual: { objects: { merge: { general } } } } }));
        const { exporter } = setup({ visual, pageVisuals: [visual, slicer], reportOpts: { bookmarkState } });
        await rejectsWith(exporter.buildRequest(visual), "slicerUnreadable");
    });

    test("slicers on an unrendered page: opens the page and retries", async () => {
        let calls = 0;
        const sf = { $schema: "basic", target: { table: "T", column: "S" } };
        const slicer = mockVisual({ name: "s1", type: "slicer", getSlicerState: async () => {
            if (calls++ === 0) throw { message: "visualConfigIsNotInitialized" };
            return { filters: [sf] };
        } });
        const visual = mockVisual();
        const other = mockPage({ name: "p0" });
        const page = mockPage({ name: "p2", displayName: "Detail", visuals: [visual, slicer] });
        const report = mockReport({ pages: [other, page] });
        const exporter = createExporter({ report, fetch: mockFetch({}), ...quiet });
        const phases = [];
        const req = await exporter.buildRequest(visual, { onProgress: p => phases.push(p.phase) });
        assert.deepEqual(req.filters, [sf]);
        assert.equal(page.activated, 1);
        assert.ok(phases.includes("opening-page"));
    });

    const countCaptures = report => {
        const capture = report.bookmarksManager.capture;
        const counter = { n: 0 };
        report.bookmarksManager.capture = async (...a) => { counter.n++; return capture(...a); };
        return counter;
    };
    // Functions, not constants: inCond is declared further down the module.
    const regionEast = () => ({ Version: 2, From: [{ Name: "r", Entity: "Region", Type: 0 }], Where: [inCond("r", "RegionName", ["'East'"])] });
    const slicerContainer = () => ({ singleVisual: { objects: { merge: { general: [{ properties: { filter: { filter: regionEast() } } }] } } } });

    test("one bookmark capture serves every fallback and the sort", async () => {
        const visual = mockVisual();
        const slicer = mockVisual({ name: "s1", type: "slicer", getSlicerState: serializationFailed });
        const orderBy = [{ Direction: 2, Expression: { Column: { Expression: { SourceRef: { Entity: "Sales" } }, Property: "Date" } } }];
        const bookmarkState = await gzipBookmark({ explorationState: {
            filters: { byExpr: [card("Sales", inCond("c", "Product", ["'Helmet'"]))] },
            sections: { p1: { filters: { byExpr: [] }, visualContainers: { s1: slicerContainer(), v1: { singleVisual: { orderBy } } } } },
        } });
        const { exporter, report } = setup({ visual, pageVisuals: [visual, slicer], reportOpts: { bookmarkState } });
        report.getFilters = rejectsUndefined;
        const captures = countCaptures(report);
        const req = await exporter.buildRequest(visual, { sort: true });
        assert.equal(captures.n, 1);
        assert.deepEqual(req.filters, [basic("Sales", "Product", "In", ["Helmet"]), basic("Region", "RegionName", "In", ["East"])]);
        assert.deepEqual(req.sort, [{ table: "Sales", column: "Date", direction: "Descending" }]);
    });

    test("opening the page discards the bookmark captured before it", async () => {
        let calls = 0;
        const slicer = mockVisual({ name: "s1", type: "slicer", getSlicerState: async () => {
            throw { message: calls++ === 0 ? "visualConfigIsNotInitialized" : "slicerSerializationFailed" };
        } });
        const visual = mockVisual();
        const page = mockPage({ name: "p2", visuals: [visual, slicer] });
        const bookmarkState = await gzipBookmark({ explorationState: {
            filters: { byExpr: [] },
            sections: { p2: { visualContainers: { s1: slicerContainer() } } },
        } });
        const report = mockReport({ pages: [mockPage({ name: "p0" }), page], bookmarkState });
        report.getFilters = rejectsUndefined;
        const captures = countCaptures(report);
        const exporter = createExporter({ report, fetch: mockFetch({}), ...quiet });
        const req = await exporter.buildRequest(visual);
        assert.equal(page.activated, 1);
        assert.equal(captures.n, 2);
        assert.deepEqual(req.filters, [basic("Region", "RegionName", "In", ["East"])]);
    });

    test("unreadable slicer with an In selection in the bookmark is translated", async () => {
        const visual = mockVisual();
        const slicer = mockVisual({ name: "s1", type: "slicer", getSlicerState: serializationFailed });
        const filter = { Version: 2, From: [{ Name: "r", Entity: "Region", Type: 0 }], Where: [inCond("r", "RegionName", ["'East'"])] };
        const general = [{ properties: { filter: { filter } } }];
        const bookmarkState = await gzipBookmark(bookmarkWith("p1", { s1: { singleVisual: { objects: { merge: { general } } } } }));
        const { exporter } = setup({ visual, pageVisuals: [visual, slicer], reportOpts: { bookmarkState } });
        const req = await exporter.buildRequest(visual);
        assert.deepEqual(req.filters, [basic("Region", "RegionName", "In", ["East"])]);
    });

    // A hierarchy slicer's selection as the bookmark stores it: "Select all", then clear
    // North › Electronics (LargeExportReport slicer3d), and a selection of two branches.
    const tupleCond = (not, exprs, rows) => {
        const In = { Expressions: exprs, Values: rows.map(r => r.map(v => ({ Literal: { Value: v } }))) };
        return { Condition: not ? { Not: { Expression: { In } } } : { In } };
    };
    const levelRef = (source, hierarchy, level) => ({ HierarchyLevel: { Expression: { Hierarchy: {
        Expression: { SourceRef: { Source: source } }, Hierarchy: hierarchy } }, Level: level } });

    test("unreadable hierarchy slicer: multi-column In / Not(In) become tuple filters", async () => {
        const visual = mockVisual();
        const slicer = mockVisual({ name: "s1", type: "slicer", getSlicerState: serializationFailed });
        const from = [{ Name: "o", Entity: "Orders", Type: 0 }];
        const general = [
            { properties: { filter: { filter: { Version: 2, From: from,
                Where: [tupleCond(true, [colRef("o", "Region"), colRef("o", "Category")], [["'North'", "'Electronics'"]])] } } } },
            { properties: { filter: { filter: { Version: 2, From: from,
                Where: [tupleCond(false, [levelRef("o", "Sales Geography", "Area"), levelRef("o", "Sales Geography", "Line")],
                    [["'East'", "'Toys'"], ["'West'", "null"]])] } } } },
        ];
        const bookmarkState = await gzipBookmark(bookmarkWith("p1", { s1: { singleVisual: { objects: { merge: { general } } } } }));
        const { exporter } = setup({ visual, pageVisuals: [visual, slicer], reportOpts: { bookmarkState } });
        const req = await exporter.buildRequest(visual);
        assert.deepEqual(req.filters, [
            { $schema: "http://powerbi.com/product/schema#tuple", filterType: 6, operator: "NotIn",
                target: [{ table: "Orders", column: "Region" }, { table: "Orders", column: "Category" }],
                values: [[{ value: "North" }, { value: "Electronics" }]] },
            { $schema: "http://powerbi.com/product/schema#tuple", filterType: 6, operator: "In",
                target: [{ table: "Orders", hierarchy: "Sales Geography", hierarchyLevel: "Area" },
                    { table: "Orders", hierarchy: "Sales Geography", hierarchyLevel: "Line" }],
                values: [[{ value: "East" }, { value: "Toys" }], [{ value: "West" }, { value: null }]] },
        ]);
    });

    test("a tuple row that doesn't match its columns aborts the export", async () => {
        const visual = mockVisual();
        const slicer = mockVisual({ name: "s1", type: "slicer", getSlicerState: serializationFailed });
        const filter = { Version: 2, From: [{ Name: "o", Entity: "Orders", Type: 0 }],
            Where: [tupleCond(false, [colRef("o", "Region"), colRef("o", "Category")], [["'North'"]])] };
        const bookmarkState = await gzipBookmark(bookmarkWith("p1", { s1: { singleVisual: { objects: { merge: { general: [{ properties: { filter: { filter } } }] } } } } }));
        const { exporter } = setup({ visual, pageVisuals: [visual, slicer], reportOpts: { bookmarkState } });
        await rejectsWith(exporter.buildRequest(visual), "slicerUnreadable");
    });
});

describe("hierarchy-level filter cards from the bookmark", () => {
    test("user hierarchy and auto date/time levels become level targets", async () => {
        const visual = mockVisual();
        const page = mockPage({ visuals: [visual] });
        page.getFilters = rejectsUndefined;
        const level = (hierarchyExpr, hierarchy, lvl) => ({ HierarchyLevel: { Expression: { Hierarchy: {
            Expression: hierarchyExpr, Hierarchy: hierarchy } }, Level: lvl } });
        const levelIn = (ref, literals) => ({ Condition: { In: { Expressions: [ref], Values: literals.map(v => [{ Literal: { Value: v } }]) } } });
        const byExpr = [
            card("Orders", levelIn(level({ SourceRef: { Source: "c" } }, "Order Calendar", "Year"), ["2024L"])),
            // Auto date/time: the hierarchy is named after the date column.
            card("Orders", { Condition: { Not: { Expression: levelIn(level({ PropertyVariationSource: {
                Expression: { SourceRef: { Source: "c" } }, Name: "Variation", Property: "Order Date" } },
                "Date Hierarchy", "Month"), ["'March'"]).Condition } } }),
        ];
        const bookmarkState = await gzipBookmark({ explorationState: { sections: { p1: { filters: { byExpr }, visualContainers: {} } } } });
        const exporter = createExporter({ report: mockReport({ pages: [page], bookmarkState }), fetch: mockFetch({}), ...quiet });
        const req = await exporter.buildRequest(visual);
        const levelBasic = (hierarchy, hierarchyLevel, operator, values) => ({ ...basic("Orders", "x", operator, values),
            target: { table: "Orders", hierarchy, hierarchyLevel } });
        assert.deepEqual(req.filters, [
            levelBasic("Order Calendar", "Year", "In", [2024]),
            levelBasic("Order Date", "Month", "NotIn", ["March"]),
        ]);
    });
});

// Semantic-query conditions as a bookmark stores them (captured from Power BI).
const colRef = (source, property) => ({ Column: { Expression: { SourceRef: { Source: source } }, Property: property } });
const inCond = (source, property, literals) => ({ Condition: { In: {
    Expressions: [colRef(source, property)], Values: literals.map(v => [{ Literal: { Value: v } }]) } } });
const notInCond = (source, property, literals) => ({ Condition: { Not: { Expression: inCond(source, property, literals).Condition } } });
const anyValueCond = (source, property) => ({ Condition: { Comparison: { ComparisonKind: 0, Left: colRef(source, property), Right: { AnyValue: {} } } } });
const card = (entity, ...where) => ({ type: "Categorical", howCreated: 1,
    filter: { Version: 2, From: [{ Name: "c", Entity: entity, Type: 0 }], Where: where } });
const basic = (table, column, operator, values, extra = {}) => ({ $schema: "http://powerbi.com/product/schema#basic",
    target: { table, column }, filterType: 1, operator, values, requireSingleSelection: false, ...extra });
// The SDK rejects getFilters() with undefined when a filter card is in the "Select all" state.
const rejectsUndefined = async () => { throw undefined; };

describe("filter cards the SDK can't read", () => {
    test("a page card in the \"Select all\" state: the page's filters come from the bookmark", async () => {
        const visual = mockVisual({ filters: [basic("T", "V", "In", ["v"])] });
        const page = mockPage({ visuals: [visual] });
        page.getFilters = rejectsUndefined;
        const byExpr = [
            card("CategoryPicker", anyValueCond("c", "CategoryChoice")),
            card("Sales", inCond("c", "Product", ["'Helmet'", "'O''Neil \"x\"'"])),
            { type: "Categorical", howCreated: 1 }, // a card with no selection
        ];
        const bookmarkState = await gzipBookmark({ explorationState: { sections: { p1: { filters: { byExpr }, visualContainers: {} } } } });
        const report = mockReport({ pages: [page], bookmarkState });
        const exporter = createExporter({ report, fetch: mockFetch({}), ...quiet });
        const req = await exporter.buildRequest(visual);
        assert.deepEqual(req.filters, [
            basic("CategoryPicker", "CategoryChoice", "All", [], { selectAll: true }),
            basic("Sales", "Product", "In", ["Helmet", "O'Neil \"x\""]),
            basic("T", "V", "In", ["v"]),
        ]);
    });

    test("report and visual levels, NotIn and every literal type", async () => {
        const visual = mockVisual();
        visual.getFilters = rejectsUndefined;
        const page = mockPage({ visuals: [visual], filters: [basic("T", "P", "In", ["p"])] });
        const report = mockReport({ pages: [page] });
        report.getFilters = rejectsUndefined;
        const reportCards = [card("Sales", notInCond("c", "Category", ["'Clothing'"]))];
        const visualCards = [
            card("Sales", inCond("c", "Quantity", ["3L", "90071992547409930L"])),
            card("Sales", inCond("c", "Amount", ["419.88D", "1.5M", "-2E+3D", "12345678901234567.89D"])),
            card("Sales", inCond("c", "Flag", ["true", "false", "null"])),
            card("Sales", inCond("c", "SaleDate", ["datetime'2024-01-11T00:00:00'"])),
            card("CategoryPicker", notInCond("c", "CategoryChoice", [])),
        ];
        report.bookmarksManager.capture = async () => ({ state: await gzipBookmark({ explorationState: {
            filters: { byExpr: reportCards },
            sections: { p1: { filters: { byExpr: [] }, visualContainers: { v1: { filters: { byExpr: visualCards } } } } },
        } }) });
        const exporter = createExporter({ report, fetch: mockFetch({}), ...quiet });
        const req = await exporter.buildRequest(visual);
        assert.deepEqual(req.filters, [
            basic("Sales", "Category", "NotIn", ["Clothing"]),
            basic("T", "P", "In", ["p"]),
            basic("Sales", "Quantity", "In", [3, "90071992547409930"]),
            // Numbers that don't round-trip through a JS double stay as text (parsed by column type on the server).
            basic("Sales", "Amount", "In", [419.88, 1.5, "-2E+3", "12345678901234567.89"]),
            basic("Sales", "Flag", "In", [true, false, null]),
            basic("Sales", "SaleDate", "In", ["2024-01-11T00:00:00"]),
            basic("CategoryPicker", "CategoryChoice", "NotIn", []),
        ]);
    });

    test("a bookmark filter that can't be translated aborts the export", async () => {
        const visual = mockVisual();
        const page = mockPage({ visuals: [visual] });
        page.getFilters = rejectsUndefined;
        const measureFilter = { type: "Advanced", howCreated: 1, filter: { Version: 2, From: [{ Name: "s", Entity: "Sales", Type: 0 }],
            Where: [{ Condition: { Comparison: { ComparisonKind: 1,
                Left: { Measure: { Expression: { SourceRef: { Source: "s" } }, Property: "Total Amount" } },
                Right: { Literal: { Value: "100L" } } } } }] } };
        const byExpr = [card("CategoryPicker", anyValueCond("c", "CategoryChoice")), measureFilter];
        const bookmarkState = await gzipBookmark({ explorationState: { sections: { p1: { filters: { byExpr } } } } });
        const exporter = createExporter({ report: mockReport({ pages: [page], bookmarkState }), fetch: mockFetch({}), ...quiet });
        await assert.rejects(exporter.buildRequest(visual), e => {
            assert.equal(e.code, "filtersUnreadable");
            assert.match(e.message, /page filters/);
            assert.match(e.message, /can't translate/);
            return true;
        });
    });

    test("no readable bookmark aborts the export", async () => {
        const visual = mockVisual();
        const { exporter, report } = setup({ visual });
        report.getFilters = async () => { throw { message: "boom" }; };
        await assert.rejects(exporter.buildRequest(visual), e => {
            assert.equal(e.code, "filtersUnreadable");
            assert.match(e.message, /report filters \(boom\)/);
            return true;
        });
    });
});

describe("sort", () => {
    test("reads the sort from a gzip'd bookmark", async () => {
        const src = e => ({ Expression: { SourceRef: { Entity: e } } });
        const level = (hierarchyExpression, hierarchy, lvl) =>
            ({ HierarchyLevel: { Expression: { Hierarchy: { Expression: hierarchyExpression, Hierarchy: hierarchy } }, Level: lvl } });
        const orderBy = [
            { Direction: 2, Expression: { Column: { ...src("Sales"), Property: "Date" } } },
            { Direction: 1, Expression: { Aggregation: { Function: 1, Expression: { Column: { ...src("Sales"), Property: "Qty" } } } } },
            { Direction: 1, Expression: { Measure: { ...src("Sales"), Property: "Margin" } } },
            { Direction: 1, Expression: { HierarchyLevel: {} } },
            { Direction: 2, Expression: level({ SourceRef: { Entity: "Sales" } }, "Calendar", "Month") },
            // Auto date/time: the hierarchy is named after the date column.
            { Direction: 1, Expression: level({ PropertyVariationSource: { ...src("Sales"), Name: "Variation", Property: "Order Date" } }, "Date Hierarchy", "Year") },
            { Direction: 1, Expression: { Aggregation: { Function: 5, Expression: level({ SourceRef: { Entity: "Sales" } }, "Calendar", "Day") } } },
        ];
        const bookmarkState = await gzipBookmark(bookmarkWith("p1", { v1: { singleVisual: { orderBy } } }));
        const { exporter, visual } = setup({ reportOpts: { bookmarkState } });
        const req = await exporter.buildRequest(visual, { sort: true });
        assert.equal(req.applySort, true);
        assert.deepEqual(req.sort, [
            { table: "Sales", column: "Date", direction: "Descending" },
            { table: "Sales", column: "Qty", aggregation: "Avg", direction: "Ascending" },
            { table: "Sales", measure: "Margin", direction: "Ascending" },
            { table: "Sales", hierarchy: "Calendar", hierarchyLevel: "Month", direction: "Descending" },
            { table: "Sales", hierarchy: "Order Date", hierarchyLevel: "Year", direction: "Ascending" },
            { table: "Sales", hierarchy: "Calendar", hierarchyLevel: "Day", aggregation: "CountNonNull", direction: "Ascending" },
        ]);
        assert.match(req.sortSource, /^bookmark \(1 unsupported/);
    });

    test("plain base64 JSON bookmark with no orderBy", async () => {
        const bookmarkState = btoa(JSON.stringify(bookmarkWith("p1", { v1: { singleVisual: {} } })));
        const { exporter, visual } = setup({ reportOpts: { bookmarkState } });
        const req = await exporter.buildRequest(visual, { sort: true });
        assert.deepEqual(req.sort, []);
        assert.match(req.sortSource, /^none/);
    });
});

describe("listVisuals", () => {
    test("skips hidden pages and decorative visuals", async () => {
        const table = mockVisual({ name: "t", title: "  " });
        const text = mockVisual({ name: "x", type: "textbox" });
        const hiddenVisual = mockVisual({ name: "h" });
        const visible = mockPage({ name: "a", displayName: "A", visuals: [table, text] });
        const hidden = mockPage({ name: "b", visibility: 1, visuals: [hiddenVisual] });
        const exporter = createExporter({ report: mockReport({ pages: [visible, hidden] }), fetch: mockFetch({}), ...quiet });
        const list = await exporter.listVisuals();
        assert.deepEqual(list.map(v => [v.key, v.title]), [["a::t", "tableEx"]]);
        assert.equal(list[0].page, visible);
        assert.equal(list[0].visual, table);
    });
});

describe("exportVisual to a file (Save As)", () => {
    const csv = "A,M\r\n1,2\r\n";

    test("streams the CSV into the picked file", async () => {
        const saved = installPicker({ name: "sales.csv" });
        const { exporter, visual, fetch } = setup({ routes: {
            "/pbi-export/csv": () => new Response(csv, { headers: { "Content-Type": "text/csv" } }),
        } });
        const phases = [];
        let seen;
        const result = await exporter.exportVisual(visual, { onProgress: p => phases.push(p.phase), onRequest: (r, m) => { seen = m; } });
        assert.equal(result.status, "completed");
        assert.equal(result.method, "file");
        assert.equal(seen, "file");
        assert.equal(result.fileName, "sales.csv");
        assert.equal(result.bytes, Buffer.byteLength(csv));
        assert.equal(saved.text(), csv);
        assert.match(saved.suggestedName, /^Sales-\d{8}-\d{6}\.csv$/);
        assert.equal(phases.at(-1), "done");
        const call = fetch.calls[0];
        assert.equal(call.init.method, "POST");
        assert.equal(call.init.headers.get("X-PbiVisualExport-Contract"), "1");
        assert.equal(call.init.headers.get("Content-Type"), "application/json");
        assert.equal(call.body.visualName, "v1");
    });

    test("host headers are sent", async () => {
        installPicker();
        const { exporter, visual, fetch } = setup({
            routes: { "/api/export/csv": () => new Response(csv) },
            exporterOpts: { apiBase: "/api/export/", headers: async () => ({ Authorization: "Bearer abc" }) },
        });
        await exporter.exportVisual(visual);
        assert.equal(fetch.calls[0].path, "/api/export/csv");
        assert.equal(fetch.calls[0].init.headers.get("Authorization"), "Bearer abc");
    });

    test("cancelled Save As sends nothing", async () => {
        installPicker({ error: Object.assign(new Error("user cancelled"), { name: "AbortError" }) });
        const { exporter, visual, fetch } = setup();
        const result = await exporter.exportVisual(visual);
        assert.equal(result.status, "cancelled");
        assert.equal(fetch.calls.length, 0);
    });

    test("target file without a picker", async () => {
        const { exporter, visual } = setup();
        await rejectsWith(exporter.exportVisual(visual, { target: "file" }), "pickerUnavailable");
    });

    test("sign-in required: error carries the device code and the empty file is removed", async () => {
        const saved = installPicker();
        const { exporter, visual } = setup({ routes: {
            "/pbi-export/csv": () => json({ error: "Sign in to XMLA", signInRequired: true, verificationUri: "https://microsoft.com/devicelogin", userCode: "ABC123" }, 503),
        } });
        await assert.rejects(exporter.exportVisual(visual), e => {
            assert.equal(e.code, "signInRequired");
            assert.equal(e.status, 503);
            assert.equal(e.signIn.userCode, "ABC123");
            return true;
        });
        assert.equal(saved.removed, true);
    });

    test("validation problems", async () => {
        installPicker();
        const { exporter, visual } = setup({ routes: {
            "/pbi-export/csv": () => json({ error: "Invalid request", problems: ["unknown column T[Z]"] }, 400),
        } });
        await assert.rejects(exporter.exportVisual(visual), e => {
            assert.equal(e.code, "http");
            assert.equal(e.status, 400);
            assert.deepEqual(e.problems, ["unknown column T[Z]"]);
            return true;
        });
    });

    test("no fields", async () => {
        const saved = installPicker();
        const visual = mockVisual({ fields: {} });
        const { exporter } = setup({ visual });
        await rejectsWith(exporter.exportVisual(visual), "noFields");
        assert.equal(saved.removed, true);
    });

    test("network failure", async () => {
        installPicker();
        const { exporter, visual } = setup({ exporterOpts: { fetch: async () => { throw new TypeError("fetch failed"); } } });
        await rejectsWith(exporter.exportVisual(visual), "network");
    });

    test("stream failure mid-download", async () => {
        const saved = installPicker();
        const body = new ReadableStream({
            start(c) { c.enqueue(new TextEncoder().encode("A,M\r\n")); },
            pull(c) { c.error(new Error("connection reset")); },
        });
        const { exporter, visual } = setup({ routes: { "/pbi-export/csv": () => new Response(body) } });
        await assert.rejects(exporter.exportVisual(visual), e => {
            assert.equal(e.code, "downloadFailed");
            assert.match(e.message, /connection reset/);
            return true;
        });
    });

    test("abort", async () => {
        installPicker();
        const ac = new AbortController();
        ac.abort();
        const { exporter, visual } = setup();
        await rejectsWith(exporter.exportVisual(visual, { signal: ac.signal }), "aborted");
    });

    test("warns once on a contract mismatch", async () => {
        installPicker();
        const warnings = [];
        const { exporter, visual } = setup({
            routes: { "/pbi-export/csv": () => new Response(csv, { headers: { "X-PbiVisualExport-Contract": "2" } }) },
            exporterOpts: { onWarning: m => warnings.push(m) },
        });
        await exporter.exportVisual(visual);
        await exporter.exportVisual(visual);
        assert.equal(warnings.length, 1);
        assert.match(warnings[0], /contract 2/);
    });
});

describe("exportVisual as a browser download (ticket)", () => {
    const prepared = { ticket: "t/1", fileName: "Sales-20260101-000000.csv", ticketExpiresInSeconds: 60, columns: [] };

    function statusRoute(states) {
        let i = 0;
        return () => json(states[Math.min(i++, states.length - 1)]);
    }

    test("prepares, starts the download in a hidden frame and follows its status", async () => {
        mock.timers.enable({ apis: ["setTimeout"] }); // the frame's 60 s clean-up timer
        const frames = installDocument();
        const { exporter, visual, fetch } = setup({
            exporterOpts: { pollIntervalMs: 5 },
            routes: {
                "/pbi-export/prepare": () => json(prepared),
                "/status": statusRoute([{ state: "Issued" }, { state: "Streaming", rows: 2 }, { state: "Completed", rows: 5 }]),
            },
        });
        const phases = [];
        const result = await exporter.exportVisual(visual, { target: "download", onProgress: p => phases.push(p.phase) });
        assert.equal(result.status, "completed");
        assert.equal(result.method, "download");
        assert.equal(result.rows, 5);
        assert.equal(result.fileName, prepared.fileName);
        assert.equal(frames.length, 1);
        assert.equal(frames[0].hidden, true);
        assert.equal(frames[0].src, "/pbi-export/download/t%2F1");
        assert.ok(fetch.calls.some(c => c.path === "/pbi-export/download/t%2F1/status"));
        assert.deepEqual([...new Set(phases)], ["gathering", "checking", "waiting", "streaming", "done"]);
    });

    test("auto falls back to a download without a picker", async () => {
        mock.timers.enable({ apis: ["setTimeout"] });
        installDocument();
        const { exporter, visual } = setup({
            exporterOpts: { pollIntervalMs: 5 },
            routes: { "/pbi-export/prepare": () => json(prepared), "/status": statusRoute([{ state: "Completed", rows: 1 }]) },
        });
        const result = await exporter.exportVisual(visual);
        assert.equal(result.method, "download");
    });

    test("a failed download", async () => {
        mock.timers.enable({ apis: ["setTimeout"] });
        installDocument();
        const { exporter, visual } = setup({
            exporterOpts: { pollIntervalMs: 5 },
            routes: { "/pbi-export/prepare": () => json(prepared), "/status": statusRoute([{ state: "Failed", error: "XMLA query failed" }]) },
        });
        await assert.rejects(exporter.exportVisual(visual, { target: "download" }), e => {
            assert.equal(e.code, "downloadFailed");
            assert.equal(e.message, "XMLA query failed");
            return true;
        });
    });

    test("too many exports", async () => {
        installDocument();
        const { exporter, visual } = setup({
            routes: { "/pbi-export/prepare": () => json({ error: "Too many exports in progress." }, 429) },
        });
        await assert.rejects(exporter.exportVisual(visual, { target: "download" }), e => e.code === "http" && e.status === 429);
    });
});

describe("visual menu command", () => {
    test("adds the command, exports on click and ignores other commands", async () => {
        mock.timers.enable({ apis: ["setTimeout"] });
        installDocument();
        const { exporter, report, visual } = setup({
            exporterOpts: { pollIntervalMs: 5 },
            routes: {
                "/pbi-export/prepare": () => json({ ticket: "k", fileName: "f.csv" }),
                "/status": () => json({ state: "Completed", rows: 3 }),
            },
        });
        const results = [];
        const errors = [];
        const done = new Promise(resolve => {
            exporter.addVisualMenuCommand({
                title: "Export everything",
                export: v => ({ formatted: v.name !== "v1" }),
                onResult: (r, v) => { results.push([r, v]); resolve(); },
                onError: e => { errors.push(e); resolve(); },
            });
        });
        await new Promise(r => setImmediate(r));
        const cmd = report.settingsUpdates[0].extensions.commands[0];
        assert.equal(cmd.name, "pbiVisualExport");
        assert.equal(cmd.extend.visualOptionsMenu.title, "Export everything");
        assert.equal(cmd.extend.visualOptionsMenu.menuLocation, 1);

        report.fire("commandTriggered", { command: "somethingElse", page: { name: "p1" }, visual: { name: "v1" } });
        report.fire("commandTriggered", { command: "pbiVisualExport", page: { name: "p1" }, visual: { name: "v1" } });
        await done;
        assert.deepEqual(errors, []);
        assert.equal(results.length, 1);
        assert.equal(results[0][0].rows, 3);
        assert.equal(results[0][0].method, "download");
        assert.equal(results[0][0].request.applyFormatStrings, false);
        assert.equal(results[0][1], visual);

        exporter.dispose();
        assert.equal(report.handlers.size, 0);
    });

    test("handleCommand returns null for other commands", async () => {
        const { exporter } = setup();
        assert.equal(await exporter.handleCommand({ detail: { command: "x" } }), null);
    });

    test("an unknown visual reports an error", async () => {
        const { exporter, report } = setup();
        const failed = new Promise(resolve => exporter.addVisualMenuCommand({ onError: resolve }));
        await new Promise(r => setImmediate(r));
        report.fire("commandTriggered", { command: "pbiVisualExport", page: { name: "p1" }, visual: { name: "nope" } });
        const e = await failed;
        assert.match(e.message, /nope/);
    });
});

describe("sign-in status", () => {
    test("null when the server doesn't use device code", async () => {
        const { exporter } = setup();
        assert.equal(await exporter.getSignInStatus(), null);
    });

    test("waits until signed in", async () => {
        let n = 0;
        const { exporter } = setup({ routes: {
            "/pbi-export/xmla-auth": () => json({ mode: "DeviceCode", signedIn: ++n >= 3, userCode: "X" }),
        } });
        await exporter.waitForSignIn({ intervalMs: 1 });
        assert.equal(n, 3);
    });
});
