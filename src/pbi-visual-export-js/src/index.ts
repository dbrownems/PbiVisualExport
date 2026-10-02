// PbiVisualExport browser client: exports an embedded Power BI visual's data to CSV
// through the PbiVisualExport ASP.NET Core module, without the report's export limits.
//
//   const exporter = PbiVisualExport.createExporter({ report, apiBase: "/pbi-export" });
//   button.onclick = () => exporter.exportVisual(visual, { onProgress: p => status.textContent = p.message });
//   exporter.addVisualMenuCommand();   // "Export all data (CSV)" in every visual's "More options" menu
import { PbiExportError, errorText, isAbort, throwIfAborted } from "./errors";
import { SKIP_VISUAL_TYPES, browserTimeZone, createBookmarkSnapshot, gatherFields, gatherFilters, gatherSort, suggestedFileName, visualTitle } from "./gather";
import { ApiClient, readJson } from "./http";
import { canPickFile, discardFile, downloadViaTicket, pickSaveFile, saveToFile } from "./transfer";
import type {
    AddVisualMenuCommandOptions, BuildRequestOptions, ExportMethod, ExportOptions, ExportProgress, ExportResult,
    ExportableVisual, ExporterOptions, PbiPage, PbiReport, PbiVisual, VisualExportRequest, VisualMenuCommandOptions,
    XmlaSignInStatus,
} from "./types";

export * from "./types";
export { PbiExportError } from "./errors";
export type { PbiExportErrorCode } from "./errors";
export { CONTRACT_VERSION } from "./http";

declare const __VERSION__: string;
/** Version of this client. */
export const VERSION: string = typeof __VERSION__ === "string" ? __VERSION__ : "0.0.0-dev";

const DEFAULT_COMMAND = "pbiVisualExport";

export function createExporter(options: ExporterOptions): Exporter {
    return new Exporter(options);
}

export class Exporter {
    readonly report: PbiReport;
    private readonly api: ApiClient;
    private readonly pollIntervalMs: number;
    private readonly ids: { workspaceId?: string; reportId?: string; datasetId?: string };
    private readonly disposers: (() => void)[] = [];

    constructor(options: ExporterOptions) {
        if (!options?.report) throw new TypeError("createExporter: `report` is required.");
        this.report = options.report;
        const fetchImpl = options.fetch ?? globalThis.fetch.bind(globalThis);
        this.api = new ApiClient(options.apiBase ?? "/pbi-export", {
            headers: options.headers,
            credentials: options.credentials ?? "same-origin",
            fetch: fetchImpl,
            onWarning: options.onWarning ?? (m => console.warn(m)),
        });
        this.pollIntervalMs = options.pollIntervalMs ?? 1000;
        this.ids = { workspaceId: options.workspaceId, reportId: options.reportId, datasetId: options.datasetId };
    }

    /** The URL of an endpoint of the module (e.g. `url("client.js")`). */
    url(path: string): string {
        return this.api.url(path);
    }

    /** Exportable visuals on the report's visible pages. */
    async listVisuals(): Promise<ExportableVisual[]> {
        const result: ExportableVisual[] = [];
        for (const page of await this.report.getPages()) {
            if (page.visibility === 1) continue; // SectionVisibility.HiddenInViewMode
            let visuals: PbiVisual[];
            try { visuals = await page.getVisuals(); } catch { continue; }
            for (const visual of visuals) {
                if (SKIP_VISUAL_TYPES.has(visual.type)) continue;
                const title = visual.title && visual.title.trim() ? visual.title : visual.type;
                result.push({ key: `${page.name}::${visual.name}`, page, visual, title, label: `${title}  \u2013  [${page.displayName}]` });
            }
        }
        return result;
    }

    /** A visual by page and visual name (e.g. from a `commandTriggered` event). */
    async getVisual(pageName: string, visualName: string): Promise<{ page: PbiPage; visual: PbiVisual }> {
        const page = typeof this.report.getPageByName === "function"
            ? await this.report.getPageByName(pageName)
            : (await this.report.getPages()).find(p => p.name === pageName);
        if (!page) throw new Error(`Page "${pageName}" isn't in the report.`);
        const visual = typeof page.getVisualByName === "function"
            ? await page.getVisualByName(visualName)
            : (await page.getVisuals()).find(v => v.name === visualName);
        if (!visual) throw new Error(`Visual "${visualName}" isn't on page "${page.displayName}".`);
        return { page, visual };
    }

    /**
     * The export request for a visual: its fields, the filters and slicer selections that
     * apply to it, and (with `sort`) its sort. Nothing is sent.
     */
    async buildRequest(visual: PbiVisual, options: BuildRequestOptions = {}): Promise<VisualExportRequest> {
        const page = this.pageOf(visual, options.page);
        const started = performance.now();
        const report = (p: Omit<ExportProgress, "elapsedMs">) => options.onProgress?.({ ...p, elapsedMs: performance.now() - started });
        const { workspaceId, reportId, datasetId } = this.resolveIds();
        report({ phase: "gathering", message: "Gathering visual configuration\u2026" });
        const fields = await gatherFields(visual);
        throwIfAborted(options.signal);
        const snapshot = createBookmarkSnapshot(this.report);
        const filters = await gatherFilters(this.report, page, visual,
            () => report({ phase: "opening-page", message: `Opening page \u201c${page.displayName}\u201d to read its slicers\u2026` }),
            options.signal, snapshot);
        throwIfAborted(options.signal);
        const applySort = options.sort === true;
        const sortInfo = applySort ? await gatherSort(this.report, page, visual, snapshot) : { sort: [], source: "not read (sort off)" };
        const timeZone = browserTimeZone();
        return {
            workspaceId, reportId,
            ...(datasetId ? { datasetId } : {}),
            pageName: page.name,
            visualName: visual.name,
            visualTitle: visualTitle(visual, page),
            fields,
            filters,
            applyFormatStrings: options.formatted !== false,
            applySort,
            sort: sortInfo.sort,
            ...(timeZone ? { timeZone } : {}),
            sortSource: sortInfo.source,
        };
    }

    /**
     * Exports a visual to CSV. Resolves when the file is complete, with `status: "cancelled"`
     * if the user dismissed the Save As dialog; rejects with a {@link PbiExportError}.
     * Call it directly from a click handler: the Save As dialog needs the user gesture.
     */
    async exportVisual(visual: PbiVisual, options: ExportOptions = {}): Promise<ExportResult> {
        const started = performance.now();
        const elapsedMs = () => performance.now() - started;
        const page = this.pageOf(visual, options.page);
        const target = options.target ?? "auto";

        // First, while the click still counts as a user gesture.
        let file = null;
        if (target !== "download") {
            const pick = await pickSaveFile(options.fileName || suggestedFileName(visualTitle(visual, page)));
            if ("cancelled" in pick) return { status: "cancelled", elapsedMs: elapsedMs() };
            if ("handle" in pick) file = pick.handle;
            else if (target === "file")
                throw new PbiExportError("pickerUnavailable", "The Save As dialog isn't available here" +
                    (pick.reason ? ` (${errorText(pick.reason)})` : canPickFile() ? "" : " (it needs a Chromium browser)") + ".",
                    { cause: pick.reason });
        }
        const method: ExportMethod = file ? "file" : "download";

        let request: VisualExportRequest;
        try {
            request = await this.buildRequest(visual, { ...options, page });
            if (request.fields.length === 0)
                throw new PbiExportError("noFields", "Couldn't detect any fields on the visual.");
        } catch (e) {
            await discardFile(file);
            throw e;
        }
        options.onRequest?.(request, method);

        const report = (p: Omit<ExportProgress, "elapsedMs">) => options.onProgress?.({ ...p, elapsedMs: elapsedMs() });
        if (file) {
            const { bytes } = await saveToFile(this.api, request, file, report, options.signal);
            return { status: "completed", method, fileName: file.name, bytes, elapsedMs: elapsedMs(), request };
        }
        const { rows, prepared } = await downloadViaTicket(this.api, request, report, this.pollIntervalMs, options.signal);
        return { status: "completed", method, fileName: prepared.fileName, rows, elapsedMs: elapsedMs(), request, prepared };
    }

    /** Dev XMLA device-code sign-in status, or null if the server doesn't use device code. */
    async getSignInStatus(signal?: AbortSignal): Promise<XmlaSignInStatus | null> {
        const resp = await this.api.get("xmla-auth", signal);
        if (resp.status === 404) return null;
        return readJson<XmlaSignInStatus>(resp);
    }

    /** Resolves once the XMLA device-code sign-in (after a `signInRequired` error) completes. */
    async waitForSignIn(options: { signal?: AbortSignal; intervalMs?: number } = {}): Promise<void> {
        for (; ;) {
            throwIfAborted(options.signal);
            try {
                const s = await this.getSignInStatus(options.signal);
                if (!s || s.signedIn) return;
            } catch (e) {
                if (isAbort(e)) throw e;
                /* keep polling */
            }
            await new Promise(r => setTimeout(r, options.intervalMs ?? 3000));
        }
    }

    /**
     * The extension command for a visual's "More options" menu, for hosts that build their
     * own `settings.extensions`. Handle it with {@link handleCommand}.
     */
    visualMenuCommand(options: VisualMenuCommandOptions = {}): object {
        return {
            name: options.name ?? DEFAULT_COMMAND,
            title: options.title ?? "Export all data (CSV)",
            ...(options.icon ? { icon: options.icon } : {}),
            extend: {
                visualOptionsMenu: {
                    title: options.title ?? "Export all data (CSV)",
                    menuLocation: options.menuLocation === "bottom" ? 0 : 1, // models.MenuLocation
                },
            },
        };
    }

    /**
     * Handles a `commandTriggered` event: exports the visual if it's this module's command.
     * Returns null for other commands.
     */
    async handleCommand(event: { detail?: any }, options: ExportOptions & { name?: string } = {}): Promise<ExportResult | null> {
        const d = event?.detail;
        if (!d || d.command !== (options.name ?? DEFAULT_COMMAND)) return null;
        const { page, visual } = await this.getVisual(d.page?.name, d.visual?.name);
        // A menu click happens inside the report's iframe, so there's no gesture for a Save As dialog.
        return this.exportVisual(visual, { target: "download", ...options, page });
    }

    /**
     * Adds "Export all data (CSV)" to every visual's "More options" menu. This replaces the
     * report's `settings.extensions` (use {@link visualMenuCommand} to merge your own).
     * Returns a function that removes the handler.
     */
    async addVisualMenuCommand(options: AddVisualMenuCommandOptions = {}): Promise<() => void> {
        if (typeof this.report.updateSettings !== "function")
            throw new TypeError("addVisualMenuCommand needs report.updateSettings (powerbi-client).");
        await this.report.updateSettings({ extensions: { commands: [this.visualMenuCommand(options)] } });
        const name = options.name ?? DEFAULT_COMMAND;
        const handler = (event: { detail?: any }) => {
            if (event?.detail?.command !== name) return;
            let visual: PbiVisual | undefined;
            (async () => {
                const found = await this.getVisual(event.detail.page?.name, event.detail.visual?.name);
                visual = found.visual;
                const exportOptions = typeof options.export === "function" ? options.export(found.visual) : options.export;
                const result = await this.exportVisual(found.visual, { target: "download", ...exportOptions, page: found.page });
                options.onResult?.(result, found.visual);
            })().catch(e => (options.onError ?? ((err: unknown) => console.error("PbiVisualExport:", err)))(e, visual));
        };
        this.report.on("commandTriggered", handler);
        const dispose = () => this.report.off("commandTriggered", handler);
        this.disposers.push(dispose);
        return dispose;
    }

    /** Removes the handlers added by this exporter. */
    dispose(): void {
        for (const d of this.disposers.splice(0)) d();
    }

    private pageOf(visual: PbiVisual, page?: PbiPage): PbiPage {
        const p = page ?? (visual?.page as PbiPage | undefined);
        if (!p || typeof p.getVisuals !== "function")
            throw new TypeError("The visual's page is unknown: pass `page` (or a visual from page.getVisuals()).");
        return p;
    }

    private resolveIds(): { workspaceId: string; reportId: string; datasetId?: string } {
        const config = this.report.config || {};
        const reportId = this.ids.reportId || safe(() => this.report.getId?.()) || config.id;
        let workspaceId = this.ids.workspaceId;
        if (!workspaceId && config.embedUrl) {
            try { workspaceId = new URL(config.embedUrl).searchParams.get("groupId") || undefined; } catch { /* not a URL */ }
        }
        if (!workspaceId || !reportId)
            throw new PbiExportError("missingIds",
                `The report's ${!workspaceId ? "workspace" : "report"} ID is unknown: pass ${!workspaceId ? "workspaceId" : "reportId"} to createExporter.`);
        return { workspaceId, reportId, datasetId: this.ids.datasetId || config.datasetId };
    }
}

function safe<T>(f: () => T): T | undefined {
    try { return f(); } catch { return undefined; }
}
