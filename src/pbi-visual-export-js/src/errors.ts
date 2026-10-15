import type { XmlaSignInInfo } from "./types";

export type PbiExportErrorCode =
    /** Dev XMLA device-code sign-in pending: see `signIn`, then `waitForSignIn()` and retry. */
    | "signInRequired"
    /** No columns or measures were found on the visual. */
    | "noFields"
    /** A slicer's selection (or the page's visual list) couldn't be read; exporting without it would over-export. */
    | "slicerUnreadable"
    /** Report, page or visual filters couldn't be read; exporting without them would over-export. */
    | "filtersUnreadable"
    /** `getDataFields` is missing: load powerbi-report-authoring after powerbi-client. */
    | "authoringApiMissing"
    /** The report's workspace or report ID couldn't be determined. */
    | "missingIds"
    /** `target: "file"` but the Save As dialog is unavailable. */
    | "pickerUnavailable"
    /** The server rejected the request (see `status`; `problems` only on request-validation 400s). */
    | "http"
    /** The server couldn't be reached. */
    | "network"
    /** The transfer failed or stopped part way. A Save As file may keep partial content (it's removed only if still empty); a browser download saves nothing. */
    | "downloadFailed"
    /** Cancelled through the `signal`. */
    | "aborted";

export class PbiExportError extends Error {
    readonly code: PbiExportErrorCode;
    /** HTTP status, for `http` errors. */
    readonly status?: number;
    /** The server's list of request problems, sent only with request-validation 400s. */
    readonly problems?: string[];
    /** For `signInRequired`. */
    readonly signIn?: XmlaSignInInfo;

    constructor(code: PbiExportErrorCode, message: string,
        details?: { status?: number; problems?: string[]; signIn?: XmlaSignInInfo; cause?: unknown }) {
        super(message, details && "cause" in details ? { cause: details.cause } : undefined);
        this.name = "PbiExportError";
        this.code = code;
        this.status = details?.status;
        this.problems = details?.problems;
        this.signIn = details?.signIn;
    }
}

/** A readable message from an SDK error (strings, `{ message }`, `{ detailedMessage }` objects). */
export function errorText(e: unknown): string {
    if (!e) return "unknown error";
    if (typeof e === "string") return e;
    const o = e as { message?: string; detailedMessage?: string };
    return o.message || o.detailedMessage || JSON.stringify(e);
}

export function isAbort(e: unknown): boolean {
    return (e instanceof PbiExportError && e.code === "aborted")
        || (typeof e === "object" && e !== null && (e as { name?: string }).name === "AbortError");
}

export function aborted(signal?: AbortSignal): PbiExportError {
    const reason = signal?.reason;
    return new PbiExportError("aborted", "The export was cancelled.", reason === undefined ? undefined : { cause: reason });
}

export function throwIfAborted(signal?: AbortSignal): void {
    if (signal?.aborted) throw aborted(signal);
}

/** A server error body `{ error, problems?, signInRequired? }` (or plain text) as an error. */
export function errorFromText(text: string, status: number): PbiExportError {
    let body: { error?: string; problems?: string[]; signInRequired?: boolean; verificationUri?: string; userCode?: string } | null = null;
    try { body = JSON.parse(text); } catch { /* plain text */ }
    if (body && typeof body !== "object") body = null;
    const message = body?.error || text?.trim() || (status ? `HTTP ${status}` : "The server rejected the request.");
    if (body?.signInRequired)
        return new PbiExportError("signInRequired", message,
            { status, signIn: { error: body.error, verificationUri: body.verificationUri, userCode: body.userCode } });
    return new PbiExportError("http", message, { status, problems: Array.isArray(body?.problems) ? body!.problems : undefined });
}
