// Reads a visual's configuration through the Power BI Client JS API: its fields
// (powerbi-report-authoring), the filters and slicer selections that apply to it, and its
// sort (from a captured bookmark: the SDK can set a visual's sort but not read it).
import { PbiExportError, errorText, throwIfAborted } from "./errors";
import type { ExportField, ExportSort, PbiFilter, PbiPage, PbiReport, PbiVisual } from "./types";

/** Visual types with nothing to export. */
export const SKIP_VISUAL_TYPES: ReadonlySet<string> = new Set([
    "textbox", "image", "shape", "actionButton", "basicShape", "card", "bookmarkNavigator", "filter",
]);

// Roles to probe when a visual doesn't advertise capabilities.
const FALLBACK_ROLES = ["Category", "Series", "Y", "Values", "Rows", "Columns",
    "Axis", "Legend", "Details", "Group", "Tooltips"];

// semanticQuery QueryAggregateFunction (fabric/item/report/definition/semanticQuery schema), by
// code, named as getDataFields names them: "Count" is Count (Distinct), "CountNonNull" is Count.
const QUERY_AGGREGATE_FUNCTIONS = ["Sum", "Avg", "Count", "Min", "Max", "CountNonNull",
    "Median", "StandardDeviation", "Variance"];

type Target = {
    table?: string; column?: string | { name?: string; table?: string }; measure?: string | { name?: string; table?: string };
    hierarchy?: string; hierarchyLevel?: string;
    aggregationFunction?: string; aggregation?: string; role?: string; hidden?: boolean; displayName?: string;
};

/** One `getDataFields` target as an export field, or null if it names nothing. */
export function mapTarget(t: unknown, role?: string): ExportField | null {
    if (!t || typeof t !== "object") return null;
    const x = t as Target;
    const col = x.column, mea = x.measure;
    const table = x.table || (typeof col === "object" && col?.table) || (typeof mea === "object" && mea?.table) || "";
    const field: ExportField = {
        table,
        column: typeof col === "string" ? col : (col && col.name) || null,
        measure: typeof mea === "string" ? mea : (mea && mea.name) || null,
        aggregation: x.aggregationFunction || x.aggregation || null,
        role: role || x.role || null,
        hidden: x.hidden === true,
    };
    // A hierarchy level ({table, hierarchy, hierarchyLevel}): the server resolves its column.
    if (x.hierarchy && x.hierarchyLevel) return { ...field, column: null, measure: null, hierarchy: x.hierarchy, hierarchyLevel: x.hierarchyLevel };
    if (!field.column && !field.measure && x.displayName) field.column = x.displayName;
    if (!field.table && !field.column && !field.measure) return null;
    return field;
}

export async function gatherFields(visual: PbiVisual): Promise<ExportField[]> {
    if (typeof visual.getDataFields !== "function")
        throw new PbiExportError("authoringApiMissing",
            "Reading a visual's fields needs powerbi-report-authoring: load it after powerbi-client.");
    let roles: string[] = [];
    try {
        const caps = typeof visual.getCapabilities === "function" ? await visual.getCapabilities() : null;
        if (caps && Array.isArray(caps.dataRoles)) roles = caps.dataRoles.map(r => r.name).filter(Boolean);
    } catch { /* use the fallback roles */ }
    if (roles.length === 0) roles = FALLBACK_ROLES;

    const fields: ExportField[] = [];
    for (const role of roles) {
        let targets: unknown[];
        try { targets = await visual.getDataFields(role); } catch { continue; }
        if (!Array.isArray(targets)) continue;
        for (const t of targets) {
            const f = mapTarget(t, role);
            if (f) fields.push(f);
        }
    }
    return fields;
}

/**
 * Report, page and visual filters plus the current selection of every slicer on the page.
 * Filters and slicers the SDK can't serialize are read from a captured bookmark; one that
 * can't be read either way aborts the export: dropping it would silently over-export.
 */
export async function gatherFilters(report: PbiReport, page: PbiPage, visual: PbiVisual,
    onOpeningPage: () => void, signal?: AbortSignal): Promise<PbiFilter[]> {
    const all: PbiFilter[] = [];
    const pushAll = (arr: unknown) => { if (Array.isArray(arr)) for (const f of arr) all.push(f); };

    pushAll(await levelFilters("report", () => report.getFilters(), report, page, visual));
    pushAll(await levelFilters("page", () => page.getFilters(), report, page, visual));
    pushAll(await levelFilters("visual", () => visual.getFilters(), report, page, visual));

    let visuals: PbiVisual[] = [];
    try { visuals = await page.getVisuals(); } catch { /* no slicers readable */ }
    let activated = false;
    for (const v of visuals) {
        if (v.type !== "slicer") continue;
        let state: { filters: PbiFilter[] } | null = null;
        let lastErr: unknown = null;
        // Slicers on a page that isn't rendered fail with visualConfigIsNotInitialized:
        // activate the page and retry.
        for (let attempt = 0; attempt < 30 && !state; attempt++) {
            throwIfAborted(signal);
            try {
                state = await v.getSlicerState();
            } catch (e) {
                lastErr = e;
                if (errorText(e) !== "visualConfigIsNotInitialized") break;
                if (!activated) {
                    activated = true;
                    if ((await report.getActivePage()).name !== page.name) {
                        onOpeningPage();
                        await page.setActive();
                    }
                }
                await new Promise(r => setTimeout(r, 500));
            }
        }
        if (!state) {
            // The SDK's slicer serializer throws on some slicers (one with no saved selection
            // throws "reading 'general'"; one in the "Select all" state throws
            // slicerSerializationFailed). The bookmark holds the same state as a
            // semantic-query filter: no filter there = no selection.
            const title = v.title && v.title.trim() ? v.title : v.name;
            const fallback = await slicerStateFromBookmark(report, page, v);
            if (fallback === "no-filter") continue;
            if (Array.isArray(fallback)) { pushAll(fallback); continue; }
            throw new PbiExportError("slicerUnreadable",
                `Couldn't read slicer \u201c${title}\u201d (${errorText(lastErr)}` +
                (fallback ? "; the bookmark shows a selection the API can't translate yet" : "") +
                "). Export aborted rather than ignoring its selection.", { cause: lastErr });
        }
        pushAll(state.filters);
    }
    return all;
}

type FilterLevel = "report" | "page" | "visual";

/**
 * The filters of one level. The SDK's serializer rejects (with `undefined`) the whole level
 * when one filter card is in the "Select all" state; the bookmark holds the same filters as
 * semantic queries (`explorationState.filters`, `.sections[page].filters`,
 * `.sections[page].visualContainers[visual].filters`).
 */
async function levelFilters(level: FilterLevel, read: () => Promise<PbiFilter[]>,
    report: PbiReport, page: PbiPage, visual: PbiVisual): Promise<PbiFilter[]> {
    try {
        const filters = await read();
        return Array.isArray(filters) ? filters : [];
    } catch (e) {
        let holder: any;
        let fallback: PbiFilter[] | null = null;
        try {
            const state = await captureExplorationState(report);
            const section = state?.sections?.[page.name];
            holder = level === "report" ? state : level === "page" ? section : section?.visualContainers?.[visual.name];
            if (holder) fallback = bookmarkFilters(holder.filters);
        } catch { /* bookmark unreadable */ }
        if (fallback) return fallback;
        throw new PbiExportError("filtersUnreadable",
            `Couldn't read the ${level} filters (${errorText(e)}` +
            (holder ? "; the bookmark shows a filter the API can't translate yet" : "") +
            "). Export aborted rather than ignoring them.", { cause: e });
    }
}

// A bookmark filters container (`{ byExpr: [...] }`) as SDK filters; null if one can't be
// translated. A filter card with no selection has no `filter`.
function bookmarkFilters(container: any): PbiFilter[] | null {
    const out: PbiFilter[] = [];
    const entries = [...(Array.isArray(container?.byExpr) ? container.byExpr : []),
        ...(container?.byName && typeof container.byName === "object" ? Object.values(container.byName) : [])];
    for (const entry of entries as any[]) {
        if (!entry?.filter) continue;
        const filters = queryFilters(entry.filter);
        if (!filters) return null;
        out.push(...filters);
    }
    return out;
}

// "no-filter" if the bookmark shows the slicer has no selection, the equivalent filters if
// it can be translated, "filter" if it has another selection, null if it can't be determined.
async function slicerStateFromBookmark(report: PbiReport, page: PbiPage, slicer: PbiVisual): Promise<PbiFilter[] | "filter" | "no-filter" | null> {
    try {
        const vc = (await captureVisualContainers(report, page))[slicer.name];
        if (!vc || !vc.singleVisual) return null;
        const general = vc.singleVisual.objects?.merge?.general || [];
        const filters = general.map((g: any) => g?.properties?.filter).filter((f: any) => f);
        if (filters.length === 0) return "no-filter";
        const translated = filters.map((f: any) => queryFilters(f.filter));
        return translated.every((f: PbiFilter[] | null) => f) ? translated.flat() as PbiFilter[] : "filter";
    } catch {
        return null;
    }
}

/**
 * A semantic-query filter (a bookmark's copy of a filter card or slicer selection) as SDK
 * filters, one per condition; null if any condition isn't one of:
 * - `column = AnyValue`: "Select all" on a column bound to a dynamic M parameter. Sent as
 *   an "All" filter with `selectAll: true`, so the server can pass the parameter its
 *   "Select all" value; on any other column it selects everything, like an empty filter.
 * - `column IN (literals)` / `NOT(column IN (literals))`: "In" / "NotIn".
 * - `(c1, c2, …) IN ((literals), …)` and its `NOT`: a tuple filter, "In" / "NotIn" (how a
 *   bookmark stores a hierarchy slicer's selection).
 * Columns may be hierarchy levels (`{table, hierarchy, hierarchyLevel}`).
 */
function queryFilters(query: any): PbiFilter[] | null {
    const from: Record<string, string> = {};
    for (const f of query?.From || []) if (f && f.Name && f.Entity) from[f.Name] = f.Entity;
    const where = query?.Where;
    if (!Array.isArray(where) || where.length === 0) return null;
    const out: PbiFilter[] = [];
    for (const w of where) {
        const filter = conditionFilter(w?.Condition, from);
        if (!filter) return null;
        out.push(filter);
    }
    return out;
}

type FilterTarget = { table: string; column: string } | { table: string; hierarchy: string; hierarchyLevel: string };

/**
 * A semantic-query column or hierarchy level as a filter target. An auto date/time level
 * (`PropertyVariationSource`) is sent as `{table, hierarchy: <date column>, hierarchyLevel}`;
 * the server resolves it through the column's date variation.
 */
function queryTarget(x: any, from: Record<string, string>): FilterTarget | null {
    const entity = (e: any) => e?.SourceRef?.Entity || from[e?.SourceRef?.Source];
    const col = x?.Column;
    if (col) {
        const table = entity(col.Expression);
        return table && col.Property ? { table, column: col.Property } : null;
    }
    const level = x?.HierarchyLevel;
    const h = level?.Expression?.Hierarchy;
    if (!level?.Level || !h) return null;
    const variation = h.Expression?.PropertyVariationSource;
    const table = entity(variation ? variation.Expression : h.Expression);
    const hierarchy = variation ? variation.Property : h.Hierarchy;
    return table && hierarchy ? { table, hierarchy, hierarchyLevel: level.Level } : null;
}

function conditionFilter(cond: any, from: Record<string, string>): PbiFilter | null {
    const basic = (t: FilterTarget, operator: string, values: unknown[], extra?: object): PbiFilter => ({
        $schema: "http://powerbi.com/product/schema#basic",
        target: t, filterType: 1, operator, values, requireSingleSelection: false, ...extra,
    });

    const cmp = cond?.Comparison;
    if (cmp) {
        const t = queryTarget(cmp.Left, from);
        return t && cmp.ComparisonKind === 0 && cmp.Right && "AnyValue" in cmp.Right
            ? basic(t, "All", [], { selectAll: true }) : null;
    }
    const notIn = cond?.Not?.Expression?.In;
    const inList = notIn || cond?.In;
    const exprs = inList?.Expressions, rows = inList?.Values;
    if (!Array.isArray(exprs) || exprs.length === 0 || !Array.isArray(rows)) return null;
    const targets = exprs.map((e: any) => queryTarget(e, from));
    if (targets.some((t: FilterTarget | null) => !t)) return null;
    const tuples: unknown[][] = [];
    for (const row of rows) {
        if (!Array.isArray(row) || row.length !== exprs.length) return null;
        const values = row.map(literalValue);
        if (values.some(v => v === undefined)) return null;
        tuples.push(values);
    }
    if (exprs.length === 1) return basic(targets[0]!, notIn ? "NotIn" : "In", tuples.map(t => t[0]));
    return {
        $schema: "http://powerbi.com/product/schema#tuple",
        target: targets, filterType: 6, operator: notIn ? "NotIn" : "In",
        values: tuples.map(t => t.map(value => ({ value }))),
    };
}

/**
 * A semantic-query literal as a filter value, or undefined if unsupported: `'text'` (quotes
 * doubled), `3L`, `1.5D`/`1.5M`, `true`, `null`, `datetime'2024-01-11T00:00:00'`. Date/time
 * literals are report-local wall time with no offset, which the server reads as such.
 */
function literalValue(x: any): string | number | boolean | null | undefined {
    const s = x?.Literal?.Value;
    if (typeof s !== "string") return undefined;
    if (s === "null") return null;
    if (s === "true" || s === "false") return s === "true";
    let m = /^'([\s\S]*)'$/.exec(s);
    if (m) return m[1].replace(/''/g, "'");
    m = /^(-?\d+)L$/.exec(s);
    if (m) { const n = Number(m[1]); return Number.isSafeInteger(n) ? n : m[1]; }
    m = /^(-?(?:\d+\.?\d*|\.\d+)(?:E[+-]?\d+)?)[DM]$/i.exec(s);
    if (m) return Number(m[1]);
    m = /^(?:datetime|date)'([^']*)'$/.exec(s);
    return m ? m[1] : undefined;
}

async function captureExplorationState(report: PbiReport): Promise<any> {
    // allPages: the default capture only includes the active page.
    const bookmark = await report.bookmarksManager.capture({ allPages: true });
    return (await decodeBookmarkState(bookmark.state || ""))?.explorationState;
}

async function captureVisualContainers(report: PbiReport, page: PbiPage): Promise<Record<string, any>> {
    return (await captureExplorationState(report))?.sections?.[page.name]?.visualContainers || {};
}

/** A captured bookmark's `state`: base64 of gzip'd JSON ("H4sI...") or of plain JSON. */
export async function decodeBookmarkState(state: string): Promise<any> {
    const bytes = Uint8Array.from(atob(state), c => c.charCodeAt(0));
    if (bytes[0] !== 0x1f || bytes[1] !== 0x8b) return JSON.parse(new TextDecoder().decode(bytes));
    const stream = new Blob([bytes]).stream().pipeThrough(new DecompressionStream("gzip"));
    return JSON.parse(await new Response(stream).text());
}

function sourceEntity(x: any): string | undefined {
    return x?.Expression?.SourceRef?.Entity;
}

/** A semantic-query sort expression as a sort key (without direction), or null if unsupported. */
export function mapOrderByExpression(x: any): Omit<ExportSort, "direction"> | null {
    if (!x) return null;
    if (x.Column) return { table: sourceEntity(x.Column)!, column: x.Column.Property };
    if (x.Measure) return { table: sourceEntity(x.Measure)!, measure: x.Measure.Property };
    if (x.HierarchyLevel) {
        const t = queryTarget(x, {});
        return t && "hierarchy" in t ? { table: t.table, hierarchy: t.hierarchy, hierarchyLevel: t.hierarchyLevel } : null;
    }
    const e = x.Aggregation?.Expression;
    const aggregation = QUERY_AGGREGATE_FUNCTIONS[x.Aggregation?.Function] || "Sum";
    if (e?.Column) return { table: sourceEntity(e.Column)!, column: e.Column.Property, aggregation };
    if (e?.HierarchyLevel) {
        const t = queryTarget(e, {});
        return t && "hierarchy" in t ? { table: t.table, hierarchy: t.hierarchy, hierarchyLevel: t.hierarchyLevel, aggregation } : null;
    }
    return null;
}

/**
 * The visual's sort, from `explorationState.sections[page].visualContainers[visual].singleVisual.orderBy`
 * of a captured bookmark: the live sort (header clicks, `sortBy`) or, if unchanged, the
 * report's saved default. No `orderBy` = no explicit sort.
 */
export async function gatherSort(report: PbiReport, page: PbiPage, visual: PbiVisual): Promise<{ sort: ExportSort[]; source: string }> {
    let orderBy: any;
    try {
        orderBy = (await captureVisualContainers(report, page))[visual.name]?.singleVisual?.orderBy;
    } catch (e) {
        return { sort: [], source: "unavailable: " + errorText(e) };
    }
    if (!Array.isArray(orderBy) || orderBy.length === 0) return { sort: [], source: "none (implicit order)" };

    const sort: ExportSort[] = [];
    let unsupported = 0;
    for (const o of orderBy) {
        const key = mapOrderByExpression(o?.Expression);
        if (!key || !key.table) { unsupported++; continue; }
        sort.push({ ...key, direction: o.Direction === 2 ? "Descending" : "Ascending" });
    }
    return { sort, source: "bookmark" + (unsupported ? ` (${unsupported} unsupported key(s) skipped)` : "") };
}

/** The title used for file names: the visual's title, else its page's display name. */
export function visualTitle(visual: PbiVisual, page: PbiPage): string {
    return visual.title && visual.title.trim() ? visual.title.trim() : page.displayName;
}

/**
 * The browser's IANA time zone, which the report (in an iframe of this browser) uses to
 * read the date filter values the SDK reports as UTC instants. Undefined if unavailable.
 */
export function browserTimeZone(): string | undefined {
    try {
        return Intl.DateTimeFormat().resolvedOptions().timeZone || undefined;
    } catch {
        return undefined;
    }
}

/** `{title}-{yyyyMMdd-HHmmss}.csv` (local time), with characters invalid in file names replaced. */
export function suggestedFileName(title: string, now: Date = new Date()): string {
    const p = (n: number) => String(n).padStart(2, "0");
    const stamp = `${now.getFullYear()}${p(now.getMonth() + 1)}${p(now.getDate())}-${p(now.getHours())}${p(now.getMinutes())}${p(now.getSeconds())}`;
    // eslint-disable-next-line no-control-regex
    const safe = title.replace(/[\\/:*?"<>|,\u0000-\u001f]/g, "_").trim().slice(0, 80);
    return `${safe || "export"}-${stamp}.csv`;
}
