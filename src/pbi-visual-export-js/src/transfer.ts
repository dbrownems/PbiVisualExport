// The two ways an export reaches the user's disk:
//  - file:     a Save As dialog (File System Access API, Chromium desktop) and the body of
//              POST {apiBase}/csv piped straight into the chosen file. Nothing is held in
//              memory.
//  - download: POST {apiBase}/prepare checks the export, then the body of POST {apiBase}/csv
//              is collected into a Blob and saved through an <a download> link. The browser
//              holds the whole file until the export completes, which limits how large an
//              export can be (see the README for staging large exports in Blob Storage).
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

/**
 * A browser download, for when there's no Save As dialog: checks the export with `prepare`,
 * collects the CSV into a Blob and saves it through an `<a download>` link. The browser holds
 * the whole file until the export completes; if the export fails part way, nothing is saved.
 */
export async function downloadBuffered(api: ApiClient, request: VisualExportRequest, fileName: string,
    report: Report, signal?: AbortSignal): Promise<{ bytes: number; prepared: PrepareResponse }> {
    if (typeof document === "undefined")
        throw new PbiExportError("downloadFailed", "A browser download needs a document (use target \"file\" elsewhere).");
    report({ phase: "checking", method: "download", fileName, message: "Checking the export\u2026" });
    const prepared = await readJson<PrepareResponse>(await api.postJson("prepare", request, signal));
    throwIfAborted(signal);

    const started = performance.now();
    report({ phase: "waiting", method: "download", fileName, message: "Exporting\u2026 waiting for the first rows" });
    const resp = await api.postJson("csv", request, signal);
    if (!resp.ok) throw errorFromText(await resp.text(), resp.status);
    if (!resp.body) throw new PbiExportError("downloadFailed", "The response has no body.");

    let bytes = 0;
    const counter = new TransformStream<Uint8Array, Uint8Array>({
        transform(chunk, controller) { bytes += chunk.byteLength; controller.enqueue(chunk); },
    });
    const tick = () => report({
        phase: "streaming", method: "download", fileName, bytes,
        message: `Exporting\u2026 ${formatSize(bytes)} received (${seconds(started)}); the file is saved when the export completes`,
    });
    const timer = setInterval(tick, 1000);
    let blob: Blob;
    try {
        const body = resp.body.pipeThrough(counter, { signal });
        blob = await new Response(body, { headers: { "Content-Type": "text/csv;charset=utf-8" } }).blob();
    } catch (e) {
        if (isAbort(e) || signal?.aborted) throw aborted(signal);
        throw new PbiExportError("downloadFailed",
            `The download stopped after ${formatSize(bytes)} (${errorText(e)}); nothing was saved.`, { cause: e });
    } finally {
        clearInterval(timer);
    }
    saveBlob(blob, fileName);
    report({
        phase: "done", method: "download", fileName, bytes,
        message: `Done \u2013 ${fileName} (${formatSize(bytes)} in ${seconds(started)}) is in the browser's downloads.`,
    });
    return { bytes, prepared };
}

// Hands a complete Blob to the browser's download manager.
function saveBlob(blob: Blob, fileName: string): void {
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = fileName;
    a.hidden = true;
    document.body.appendChild(a);
    a.click();
    a.remove();
    // Some browsers read the Blob after click() returns: keep the URL alive for a while.
    setTimeout(() => URL.revokeObjectURL(url), 60000);
}
