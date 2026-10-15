// Minimal stand-ins for powerbi-client objects, fetch, the File System Access API and the DOM.

export const WS = "11111111-1111-1111-1111-111111111111";
export const RPT = "22222222-2222-2222-2222-222222222222";
export const DS = "33333333-3333-3333-3333-333333333333";

export function mockVisual({ name = "v1", type = "tableEx", title = "Sales", roles = ["Values"],
    fields = { Values: [{ table: "T", column: "A" }, { table: "T", measure: "M" }, { table: "T", column: "H", hidden: true }] },
    filters = [], slicerState, getSlicerState } = {}) {
    const v = {
        name, type, title,
        getCapabilities: async () => ({ dataRoles: roles.map(r => ({ name: r })) }),
        getDataFields: async role => fields[role] ?? [],
        getFilters: async () => filters,
        getSlicerState: getSlicerState ?? (async () => slicerState ?? { filters: [] }),
    };
    return v;
}

export function mockPage({ name = "p1", displayName = "Page 1", visuals = [], filters = [], visibility = 0 } = {}) {
    const page = {
        name, displayName, visibility, activated: 0,
        getVisuals: async () => visuals,
        getFilters: async () => filters,
        setActive: async () => { page.activated++; },
    };
    for (const v of visuals) v.page = page;
    return page;
}

export function mockReport({ pages = [], filters = [], bookmarkState = "", embedUrl, id, config } = {}) {
    const handlers = new Map();
    const report = {
        config: config ?? { embedUrl: embedUrl ?? `https://app.powerbi.com/reportEmbed?reportId=${RPT}&groupId=${WS}`, id: id ?? RPT },
        settingsUpdates: [],
        getId: () => report.config.id,
        getPages: async () => pages,
        getActivePage: async () => pages[0],
        getFilters: async () => filters,
        bookmarksManager: { capture: async () => ({ state: bookmarkState }) },
        updateSettings: async s => { report.settingsUpdates.push(s); },
        on: (event, h) => handlers.set(event, h),
        off: event => handlers.delete(event),
        fire: (event, detail) => handlers.get(event)?.({ detail }),
        handlers,
    };
    return report;
}

/** A bookmark `state` like the SDK's: base64 of gzip'd JSON. */
export async function gzipBookmark(obj) {
    const stream = new Blob([JSON.stringify(obj)]).stream().pipeThrough(new CompressionStream("gzip"));
    const bytes = new Uint8Array(await new Response(stream).arrayBuffer());
    return btoa(String.fromCharCode(...bytes));
}

export function bookmarkWith(pageName, visualContainers) {
    return { explorationState: { sections: { [pageName]: { visualContainers } } } };
}

/** A fetch that records calls and answers from `routes` (path suffix -> handler). */
export function mockFetch(routes) {
    const calls = [];
    const fetch = async (url, init = {}) => {
        const path = new URL(url, "http://localhost").pathname;
        calls.push({ url: String(url), path, init, body: init.body ? JSON.parse(init.body) : undefined });
        for (const [suffix, handler] of Object.entries(routes)) {
            if (path.endsWith(suffix)) return handler({ url, path, init, calls });
        }
        return new Response("not found", { status: 404 });
    };
    fetch.calls = calls;
    return fetch;
}

export const json = (obj, status = 200, headers = {}) =>
    new Response(JSON.stringify(obj), { status, headers: { "Content-Type": "application/json", ...headers } });

/** Installs a fake showSaveFilePicker that "saves" into memory. Returns the saved state. */
export function installPicker({ name = "out.csv", error } = {}) {
    const saved = { name, chunks: [], removed: false, suggestedName: undefined };
    globalThis.showSaveFilePicker = async opts => {
        saved.suggestedName = opts?.suggestedName;
        if (error) throw error;
        return {
            name,
            getFile: async () => ({ size: saved.chunks.reduce((n, c) => n + c.byteLength, 0) }),
            remove: async () => { saved.removed = true; },
            createWritable: async () => new WritableStream({ write(c) { saved.chunks.push(c); } }),
        };
    };
    saved.text = () => new TextDecoder().decode(Buffer.concat(saved.chunks.map(c => Buffer.from(c))));
    return saved;
}

export function uninstallPicker() {
    delete globalThis.showSaveFilePicker;
}

/** Just enough DOM for a browser download: records the `<a download>` links clicked. */
export function installDocument() {
    const links = [];
    globalThis.document = {
        createElement: tag => {
            const a = { tag, href: "", download: "", hidden: false, clicked: false, removed: false,
                click() { this.clicked = true; },
                remove() { this.removed = true; } };
            links.push(a);
            return a;
        },
        body: { appendChild: () => { } },
    };
    return links;
}

export function uninstallDocument() {
    delete globalThis.document;
}
