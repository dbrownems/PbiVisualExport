import { PbiExportError, aborted, errorFromText, isAbort } from "./errors";
import type { HeadersSource } from "./types";

/** Version of the endpoint contract this client speaks (server header `X-PbiVisualExport-Contract`). */
export const CONTRACT_VERSION = "2";
const CONTRACT_HEADER = "X-PbiVisualExport-Contract";

/** Calls the module's endpoints under `apiBase`. */
export class ApiClient {
    private readonly base: string;
    private warnedContract = false;

    constructor(apiBase: string,
        private readonly opts: {
            headers?: HeadersSource; credentials: RequestCredentials; fetch: typeof fetch; onWarning: (message: string) => void;
        }) {
        this.base = apiBase.replace(/\/+$/, "");
    }

    url(path: string): string {
        return `${this.base}/${path.replace(/^\/+/, "")}`;
    }

    postJson(path: string, body: unknown, signal?: AbortSignal): Promise<Response> {
        return this.send(path, {
            method: "POST", body: JSON.stringify(body), signal,
            headers: { "Content-Type": "application/json", [CONTRACT_HEADER]: CONTRACT_VERSION },
        });
    }

    get(path: string, signal?: AbortSignal): Promise<Response> {
        return this.send(path, { method: "GET", cache: "no-store", signal });
    }

    private async send(path: string, init: RequestInit & { headers?: Record<string, string> }): Promise<Response> {
        const headers = new Headers(await resolveHeaders(this.opts.headers));
        for (const [k, v] of Object.entries(init.headers || {})) headers.set(k, v);
        let resp: Response;
        try {
            resp = await this.opts.fetch(this.url(path), { ...init, headers, credentials: this.opts.credentials });
        } catch (e) {
            if (isAbort(e)) throw aborted(init.signal ?? undefined);
            throw new PbiExportError("network", `Couldn't reach the export service (${(e as Error)?.message || e}).`, { cause: e });
        }
        const contract = resp.headers.get(CONTRACT_HEADER);
        if (contract && contract !== CONTRACT_VERSION && !this.warnedContract) {
            this.warnedContract = true;
            this.opts.onWarning(`PbiVisualExport: the server speaks contract ${contract}, this client ${CONTRACT_VERSION}. ` +
                "Load the client from the server ({apiBase}/client.js) to keep them in step.");
        }
        return resp;
    }
}

async function resolveHeaders(source?: HeadersSource): Promise<HeadersInit | undefined> {
    return typeof source === "function" ? await source() : source;
}

/** Throws the server's error if `resp` isn't 2xx; otherwise returns the parsed JSON body. */
export async function readJson<T>(resp: Response): Promise<T> {
    const text = await resp.text();
    if (!resp.ok) throw errorFromText(text, resp.status);
    try {
        return JSON.parse(text) as T;
    } catch {
        throw new PbiExportError("http", `The export service returned an unexpected response (HTTP ${resp.status}).`, { status: resp.status });
    }
}
