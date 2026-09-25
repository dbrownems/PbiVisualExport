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

// semanticQuery QueryAggregateFunction (fabric/item/report/definition/semanticQuery schema).
const QUERY_AGGREGATE_FUNCTIONS = ["Sum", "Avg", "DistinctCount", "Min", "Max", "Count",
    "Median", "StandardDeviation", "Variance"];

type Target = {
    table?: string; column?: string | { name?: string; table?: string }; measure?: string | { name?: string; table?: string };
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
 * A slicer whose state can't be read aborts the export: dropping it would silently
 * over-export.
 */
export async function gatherFilters(report: PbiReport, page: PbiPage, visual: PbiVisual,
    onOpeningPage: () => void, signal?: AbortSignal): Promise<PbiFilter[]> {
    const all: PbiFilter[] = [];
    const pushAll = (arr: unknown) => { if (Array.isArray(arr)) for (const f of arr) all.push(f); };

    try { pushAll(await report.getFilters()); } catch { /* none */ }
    try { pushAll(await page.getFilters()); } catch { /* none */ }
    try { pushAll(await visual.getFilters()); } catch { /* none */ }

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
            // throws "reading 'general'"; select-all slicers have been reported to throw
            // slicerSerializationFailed). The bookmark holds the same state as a
            // semantic-query filter: no filter there = no selection.
            const title = v.title && v.title.trim() ? v.title : v.name;
            const fallback = await slicerStateFromBookmark(report, page, v);
            if (fallback === "no-filter") continue;
            throw new PbiExportError("slicerUnreadable",
                `Couldn't read slicer \u201c${title}\u201d (${errorText(lastErr)}` +
                (fallback ? "; the bookmark shows a selection the API can't translate yet" : "") +
                "). Export aborted rather than ignoring its selection.", { cause: lastErr });
        }
        pushAll(state.filters);
    }
    return all;
}

// "no-filter" if the bookmark shows the slicer has no selection, "filter" if it has one,
// null if it can't be determined.
async function slicerStateFromBookmark(report: PbiReport, page: PbiPage, slicer: PbiVisual): Promise<"filter" | "no-filter" | null> {
    try {
        const vc = (await captureVisualContainers(report, page))[slicer.name];
        if (!vc || !vc.singleVisual) return null;
        const general = vc.singleVisual.objects?.merge?.general || [];
        return general.some((g: any) => g && g.properties && g.properties.filter) ? "filter" : "no-filter";
    } catch {
        return null;
    }
}

async function captureVisualContainers(report: PbiReport, page: PbiPage): Promise<Record<string, any>> {
    // allPages: the default capture only includes the active page.
    const bookmark = await report.bookmarksManager.capture({ allPages: true });
    const state = await decodeBookmarkState(bookmark.state || "");
    return state?.explorationState?.sections?.[page.name]?.visualContainers || {};
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
    const c = x.Aggregation?.Expression?.Column;
    if (c) return { table: sourceEntity(c)!, column: c.Property, aggregation: QUERY_AGGREGATE_FUNCTIONS[x.Aggregation.Function] || "Sum" };
    return null; // e.g. hierarchy levels
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

/** `{title}-{yyyyMMdd-HHmmss}.csv` (local time), with characters invalid in file names replaced. */
export function suggestedFileName(title: string, now: Date = new Date()): string {
    const p = (n: number) => String(n).padStart(2, "0");
    const stamp = `${now.getFullYear()}${p(now.getMonth() + 1)}${p(now.getDate())}-${p(now.getHours())}${p(now.getMinutes())}${p(now.getSeconds())}`;
    // eslint-disable-next-line no-control-regex
    const safe = title.replace(/[\\/:*?"<>|,\u0000-\u001f]/g, "_").trim().slice(0, 80);
    return `${safe || "export"}-${stamp}.csv`;
}
