// The two ways an export reaches the user's disk. Neither holds the file in memory
// (unlike fetch + blob):
//  - file:     a Save As dialog (File System Access API, Chromium) and the body of
//              POST {apiBase}/csv piped straight into the chosen file.
//  - download: POST {apiBase}/prepare validates the export and returns a one-time ticket;
//              a hidden iframe navigates to GET {apiBase}/download/{ticket}, so the
//              browser's download manager applies Content-Disposition and streams to disk.
//              Progress and errors come from GET {apiBase}/download/{ticket}/status.
import { PbiExportError, aborted, errorFromText, errorText, isAbort, throwIfAborted } from "./errors";
import { ApiClient, readJson } from "./http";
import type { ExportProgress, PrepareResponse, VisualExportRequest } from "./types";

export type Report = (p: Omit<ExportProgress, "elapsedMs">) => void;

interface FileHandle {
    name: string;
    getFile(): Promise<{ size: number }>;
    createWritable(): Promise<WritableStream<Uint8Array>>;
    remove?(): Promise<void>;
}

export type PickResult = { handle: FileHandle } | { cancelled: true } | { unavailable: true; reason?: unknown };

export function canPickFile(): boolean {
    return typeof (globalThis as any).showSaveFilePicker === "function";
}

/** Shows the Save As dialog. Must run while the click still counts as a user gesture. */
export async function pickSaveFile(suggestedName: string): Promise<PickResult> {
    if (!canPickFile()) return { unavailable: true };
    try {
        const handle: FileHandle = await (globalThis as any).showSaveFilePicker({
            suggestedName,
            types: [{ description: "CSV file", accept: { "text/csv": [".csv"] } }],
        });
        return { handle };
    } catch (e) {
        if ((e as { name?: string })?.name === "AbortError") return { cancelled: true };
        // e.g. SecurityError without a user gesture or in a cross-origin iframe.
        return { unavailable: true, reason: e };
    }
}

/** Deletes the picked file if nothing was saved into it (Chromium creates it empty). Never deletes content. */
export async function discardFile(file: FileHandle | null | undefined): Promise<void> {
    if (!file) return;
    try {
        if ((await file.getFile()).size === 0 && typeof file.remove === "function") await file.remove();
    } catch { /* best effort */ }
}

export function formatSize(bytes: number): string {
    const [value, unit] = bytes < 1048576 ? [bytes / 1024, " KB"] : [bytes / 1048576, " MB"];
    return value.toLocaleString(undefined, { minimumFractionDigits: 1, maximumFractionDigits: 1 }) + unit;
}

function seconds(started: number): string {
    return ((performance.now() - started) / 1000).toFixed(1) + "s";
}

export async function saveToFile(api: ApiClient, request: VisualExportRequest, file: FileHandle,
    report: Report, signal?: AbortSignal): Promise<{ bytes: number }> {
    const started = performance.now();
    const fileName = file.name;
    report({ phase: "checking", method: "file", fileName, message: "Exporting\u2026 waiting for the first rows" });
    let resp: Response;
    try {
        resp = await api.postJson("csv", request, signal);
        if (!resp.ok) throw errorFromText(await resp.text(), resp.status);
        if (!resp.body) throw new PbiExportError("downloadFailed", "The response has no body.");
    } catch (e) {
        await discardFile(file);
        throw e;
    }

    let bytes = 0;
    const counter = new TransformStream<Uint8Array, Uint8Array>({
        transform(chunk, controller) { bytes += chunk.byteLength; controller.enqueue(chunk); },
    });
    const tick = () => report({
        phase: "streaming", method: "file", fileName, bytes,
        message: `Exporting\u2026 ${formatSize(bytes)} saved to ${fileName} (${seconds(started)})`,
    });
    const timer = setInterval(tick, 1000);
    try {
        const writable = await file.createWritable();
        await resp.body.pipeThrough(counter).pipeTo(writable, { signal });
    } catch (e) {
        if (!resp.body.locked) resp.body.cancel().catch(() => { });
        await discardFile(file);
        if (isAbort(e) || signal?.aborted) throw aborted(signal);
        throw new PbiExportError("downloadFailed",
            `The download stopped after ${formatSize(bytes)} (${errorText(e)}); nothing was saved.`, { cause: e });
    } finally {
        clearInterval(timer);
    }
    report({
        phase: "done", method: "file", fileName, bytes,
        message: `Done \u2013 saved ${fileName} (${formatSize(bytes)} in ${seconds(started)})`,
    });
    return { bytes };
}

export async function downloadViaTicket(api: ApiClient, request: VisualExportRequest, report: Report,
    pollIntervalMs: number, signal?: AbortSignal): Promise<{ rows: number; prepared: PrepareResponse }> {
    if (typeof document === "undefined")
        throw new PbiExportError("downloadFailed", "A browser download needs a document (use target \"file\" elsewhere).");
    report({ phase: "checking", method: "download", message: "Checking the export\u2026" });
    const prepared = await readJson<PrepareResponse>(await api.postJson("prepare", request, signal));
    throwIfAborted(signal);

    const ticketUrl = api.url(`download/${encodeURIComponent(prepared.ticket)}`);
    const frame = document.createElement("iframe");
    frame.hidden = true;
    frame.setAttribute("aria-hidden", "true");
    document.body.appendChild(frame);
    const started = performance.now();
    const done = watchDownload(api, prepared, frame, started, report, pollIntervalMs, signal);
    frame.src = ticketUrl;
    try {
        return { rows: await done, prepared };
    } finally {
        // Removing the frame while the browser still writes could cancel the download.
        if (signal?.aborted) frame.remove();
        else setTimeout(() => frame.remove(), 60000);
    }
}

// Polls the ticket's status until the server has streamed the whole file (or failed).
// The frame never loads for a download; if it does, the response was an error page.
function watchDownload(api: ApiClient, prepared: PrepareResponse, frame: HTMLIFrameElement, started: number,
    report: Report, pollIntervalMs: number, signal?: AbortSignal): Promise<number> {
    const fileName = prepared.fileName;
    const statusPath = `download/${encodeURIComponent(prepared.ticket)}/status`;
    return new Promise<number>((resolve, reject) => {
        let settled = false;
        let failures = 0;
        let polling = false;
        const finish = (err: unknown, rows = 0) => {
            if (settled) return;
            settled = true;
            clearInterval(timer);
            frame.removeEventListener("load", onLoad);
            signal?.removeEventListener("abort", onAbort);
            if (err) reject(err); else resolve(rows);
        };
        const poll = async (): Promise<{ state?: string; rows?: number; error?: string | null } | null> => {
            const resp = await api.get(statusPath, signal);
            const text = await resp.text();
            if (!resp.ok) throw errorFromText(text, resp.status);
            return JSON.parse(text);
        };
        const timer = setInterval(async () => {
            if (settled || polling) return;
            polling = true;
            try {
                const s = await poll();
                failures = 0;
                const rows = Number(s?.rows || 0);
                switch (s?.state) {
                    case "Issued":
                        report({ phase: "waiting", method: "download", fileName, message: `Exporting\u2026 waiting for the download to start (${seconds(started)})` });
                        break;
                    case "Streaming":
                        report({ phase: "streaming", method: "download", fileName, rows, message: `Exporting\u2026 ${rows.toLocaleString()} rows sent to the browser as ${fileName} (${seconds(started)})` });
                        break;
                    case "Completed":
                        report({ phase: "done", method: "download", fileName, rows, message: `Done \u2013 ${rows.toLocaleString()} rows downloaded as ${fileName} in ${seconds(started)} (see the browser's downloads).` });
                        finish(null, rows);
                        break;
                    default:
                        finish(new PbiExportError("downloadFailed", s?.error || `The download ${String(s?.state).toLowerCase()}.`));
                }
            } catch (e) {
                if (e instanceof PbiExportError && (e.code === "http" || e.code === "aborted")) finish(e);
                else if (++failures >= 30) finish(e);
            } finally {
                polling = false;
            }
        }, pollIntervalMs);
        function onAbort() { finish(aborted(signal)); }
        signal?.addEventListener("abort", onAbort);
        async function onLoad() {
            let text = "";
            try {
                if (frame.contentWindow?.location.href === "about:blank") return;
                text = frame.contentDocument?.body?.innerText || "";
            } catch { /* cross-origin error page: ask the status endpoint */ }
            if (!text) {
                try { text = (await poll())?.error || ""; } catch (e) { return finish(e); }
            }
            finish(text ? errorFromText(text, 0) : new PbiExportError("downloadFailed", "The download failed."));
        }
        frame.addEventListener("load", onLoad);
    });
}
