// Public types of the PbiVisualExport browser client.

// ---- powerbi-client objects (structural) ----
// Minimal shapes of the powerbi-client objects the exporter calls. Structural rather than
// imported, so any powerbi-client 2.x `Report` / `Page` / `VisualDescriptor` fits, the
// bundle has no runtime dependency on the SDK, and tests can pass plain objects.

/** A Power BI filter (`models.IFilter`), sent to the server as is. */
export type PbiFilter = object;

/** A `VisualDescriptor`. `getCapabilities` / `getDataFields` come from powerbi-report-authoring. */
export interface PbiVisual {
    name: string;
    title?: string;
    type: string;
    /** The visual's page (powerbi-client sets it on descriptors from `page.getVisuals()`). */
    page?: unknown;
    getFilters(): Promise<PbiFilter[]>;
    getSlicerState(): Promise<{ filters: PbiFilter[] }>;
    getCapabilities?(): Promise<{ dataRoles?: { name: string }[] }>;
    getDataFields?(dataRole: string): Promise<unknown[]>;
}

/** A report `Page`. */
export interface PbiPage {
    name: string;
    displayName: string;
    /** `models.SectionVisibility`: 0 = always visible, 1 = hidden in view mode. */
    visibility?: number;
    getVisuals(): Promise<PbiVisual[]>;
    getVisualByName?(visualName: string): Promise<PbiVisual>;
    getFilters(): Promise<PbiFilter[]>;
    setActive(): Promise<unknown>;
}

/** An embedded `Report`. */
export interface PbiReport {
    config?: { id?: string; embedUrl?: string; datasetId?: string };
    getId?(): string;
    getPages(): Promise<PbiPage[]>;
    getPageByName?(pageName: string): Promise<PbiPage>;
    getActivePage(): Promise<PbiPage>;
    getFilters(): Promise<PbiFilter[]>;
    bookmarksManager: { capture(request?: { allPages?: boolean }): Promise<{ state?: string }> };
    updateSettings?(settings: object): Promise<unknown>;
    on(eventName: string, handler: (event: any) => unknown): void;
    off(eventName: string, handler?: (event: any) => unknown): void;
}

// ---- Wire contract (version 1) ----

/** A column, hierarchy level or measure placed in the visual. */
export interface ExportField {
    table: string;
    column: string | null;
    measure: string | null;
    /**
     * With `hierarchyLevel`: a level of a hierarchy on the visual (a user hierarchy, or a date
     * column's auto date/time hierarchy, named after the column). The server exports the
     * level's column, headed by the level name.
     */
    hierarchy?: string;
    hierarchyLevel?: string;
    /** Implicit aggregation of a column or hierarchy level (Sum, Avg, Count, ...). */
    aggregation: string | null;
    /** The visual data role (Category, Values, Rows, ...). */
    role: string | null;
    /** "Hide fields on the visual": computed (it affects the rows) but not written. */
    hidden: boolean;
}

/** One sort key of the visual. */
export interface ExportSort {
    table: string;
    column?: string;
    measure?: string;
    aggregation?: string;
    /** With `hierarchyLevel`: a hierarchy level (see {@link ExportField.hierarchy}). */
    hierarchy?: string;
    hierarchyLevel?: string;
    direction: "Ascending" | "Descending";
}

/** Body of `POST {apiBase}/csv` and `POST {apiBase}/prepare`. */
export interface VisualExportRequest {
    workspaceId: string;
    /** Optional: the server resolves it from the report. */
    datasetId?: string;
    reportId: string;
    pageName: string;
    visualName: string;
    visualTitle: string;
    fields: ExportField[];
    /** Report, page and visual filters plus the page's slicer selections. */
    filters: PbiFilter[];
    applyFormatStrings: boolean;
    applySort: boolean;
    sort: ExportSort[];
    /**
     * The browser's IANA time zone. The SDK reports date filter values as UTC instants of
     * the browser's local wall time; the server converts them back with this zone.
     */
    timeZone?: string;
    /** Diagnostic only (ignored by the server): where the sort came from. */
    sortSource: string;
}

/** Response of `POST {apiBase}/prepare`. */
export interface PrepareResponse {
    fileName: string;
    columns: number;
    sort: string;
    reportMeasures?: unknown;
    measureFilters?: unknown;
    /** Dynamic M query parameters set from filters on their bound columns, e.g. `RegionParam = "West"`. */
    parameters?: string[];
    warnings?: string[];
    ticket: string;
    ticketExpiresInSeconds: number;
    workspaceName: string;
    datasetId: string;
    datasetName: string;
    reportName: string;
}

/** Response of `GET {apiBase}/xmla-auth` (dev device-code mode only). */
export interface XmlaSignInStatus {
    mode: string;
    signedIn: boolean;
    message?: string | null;
    verificationUri?: string | null;
    userCode?: string | null;
}

/** Details of a `signInRequired` error: open `verificationUri` and enter `userCode`. */
export interface XmlaSignInInfo {
    error?: string;
    verificationUri?: string | null;
    userCode?: string | null;
}

// ---- Client API ----

export type HeadersSource = HeadersInit | (() => HeadersInit | Promise<HeadersInit>);

export interface ExporterOptions {
    /** The embedded report (from `powerbi.embed`). */
    report: PbiReport;
    /** Where the host mapped the module (`app.MapPbiVisualExport(prefix)`). Default `/pbi-export`. */
    apiBase?: string;
    /** Default: the `groupId` of the report's embed URL. */
    workspaceId?: string;
    /** Default: `report.getId()`. */
    reportId?: string;
    /** Optional: the server resolves the dataset from the report. */
    datasetId?: string;
    /** Extra request headers (e.g. `Authorization`), or a function returning them. */
    headers?: HeadersSource;
    /** `fetch` credentials mode. Default `same-origin`; use `include` for a cross-origin API with cookies. */
    credentials?: RequestCredentials;
    /** Replacement `fetch` (tests, instrumentation). */
    fetch?: typeof fetch;
    /** Download status poll interval. Default 1000 ms. */
    pollIntervalMs?: number;
    /** Non-fatal problems (e.g. a client/server contract mismatch). Default `console.warn`. */
    onWarning?: (message: string) => void;
}

export type ExportMethod = "file" | "download";
export type ExportPhase = "gathering" | "opening-page" | "checking" | "waiting" | "streaming" | "done";

export interface ExportProgress {
    phase: ExportPhase;
    /** A ready-to-display status line. */
    message: string;
    method?: ExportMethod;
    fileName?: string;
    /** Rows streamed so far (`download` method). */
    rows?: number;
    /** Bytes saved so far (`file` method). */
    bytes?: number;
    elapsedMs: number;
}

export interface BuildRequestOptions {
    /** Apply the model's format strings (DAX FORMAT). Default true. */
    formatted?: boolean;
    /**
     * ORDER BY the visual's sort. Default false: sorting a multi-million-row result costs
     * capacity memory, and a CSV rarely needs the report's order.
     */
    sort?: boolean;
    /** The visual's page. Default: `visual.page`. */
    page?: PbiPage;
    onProgress?: (progress: ExportProgress) => void;
    signal?: AbortSignal;
}

export interface ExportOptions extends BuildRequestOptions {
    /**
     * `auto` (default): a Save As dialog streaming into the chosen file where the File
     * System Access API is available, else a browser download. `file`: the dialog only
     * (fails where unavailable). `download`: a browser download (Content-Disposition).
     * The dialog needs a user gesture: call `exportVisual` directly from a click handler.
     */
    target?: "auto" | "file" | "download";
    /** Suggested name for the Save As dialog. Default `{visual title}-{timestamp}.csv`. */
    fileName?: string;
    /** Called with the request just before it is sent (e.g. to log or display it). */
    onRequest?: (request: VisualExportRequest, method: ExportMethod) => void;
}

export interface ExportResult {
    /** `cancelled`: the user dismissed the Save As dialog; nothing was exported. */
    status: "completed" | "cancelled";
    method?: ExportMethod;
    fileName?: string;
    rows?: number;
    bytes?: number;
    elapsedMs: number;
    request?: VisualExportRequest;
    /** The pre-flight result (`download` method). */
    prepared?: PrepareResponse;
}

export interface ExportableVisual {
    /** `{page name}::{visual name}`: stable within a report. */
    key: string;
    page: PbiPage;
    visual: PbiVisual;
    /** The visual's title, or its type if it has none. */
    title: string;
    /** `{title} – [{page display name}]`. */
    label: string;
}

export interface VisualMenuCommandOptions {
    /** Command name (unique in the report). Default `pbiVisualExport`. */
    name?: string;
    /** Menu text. Default `Export all data (CSV)`. */
    title?: string;
    /** Position in the visual's "More options" menu. Default `top`. */
    menuLocation?: "top" | "bottom";
    /** Base64 image data URL for the menu icon. */
    icon?: string;
}

export interface AddVisualMenuCommandOptions extends VisualMenuCommandOptions {
    /**
     * Options for each export, or a function called on each click that returns them (e.g. to
     * read the page's current settings). `target` defaults to `download`: a menu click isn't
     * a page gesture.
     */
    export?: ExportOptions | ((visual: PbiVisual) => ExportOptions);
    onResult?: (result: ExportResult, visual: PbiVisual) => void;
    /** Default: `console.error`. */
    onError?: (error: unknown, visual: PbiVisual | undefined) => void;
}
