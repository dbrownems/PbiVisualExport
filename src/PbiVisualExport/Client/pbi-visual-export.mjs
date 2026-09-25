/*! pbi-visual-export 0.1.0 | MIT | https://github.com/dbrownems/PbiVisualExport */

// src/errors.ts
var PbiExportError = class extends Error {
  constructor(code, message, details) {
    super(message, details && "cause" in details ? { cause: details.cause } : void 0);
    this.name = "PbiExportError";
    this.code = code;
    this.status = details?.status;
    this.problems = details?.problems;
    this.signIn = details?.signIn;
  }
};
function errorText(e) {
  if (!e) return "unknown error";
  if (typeof e === "string") return e;
  const o = e;
  return o.message || o.detailedMessage || JSON.stringify(e);
}
function isAbort(e) {
  return e instanceof PbiExportError && e.code === "aborted" || typeof e === "object" && e !== null && e.name === "AbortError";
}
function aborted(signal) {
  const reason = signal?.reason;
  return new PbiExportError("aborted", "The export was cancelled.", reason === void 0 ? void 0 : { cause: reason });
}
function throwIfAborted(signal) {
  if (signal?.aborted) throw aborted(signal);
}
function errorFromText(text, status) {
  let body = null;
  try {
    body = JSON.parse(text);
  } catch {
  }
  if (body && typeof body !== "object") body = null;
  const message = body?.error || text?.trim() || (status ? `HTTP ${status}` : "The server rejected the request.");
  if (body?.signInRequired)
    return new PbiExportError(
      "signInRequired",
      message,
      { status, signIn: { error: body.error, verificationUri: body.verificationUri, userCode: body.userCode } }
    );
  return new PbiExportError("http", message, { status, problems: Array.isArray(body?.problems) ? body.problems : void 0 });
}

// src/gather.ts
var SKIP_VISUAL_TYPES = /* @__PURE__ */ new Set([
  "textbox",
  "image",
  "shape",
  "actionButton",
  "basicShape",
  "card",
  "bookmarkNavigator",
  "filter"
]);
var FALLBACK_ROLES = [
  "Category",
  "Series",
  "Y",
  "Values",
  "Rows",
  "Columns",
  "Axis",
  "Legend",
  "Details",
  "Group",
  "Tooltips"
];
var QUERY_AGGREGATE_FUNCTIONS = [
  "Sum",
  "Avg",
  "DistinctCount",
  "Min",
  "Max",
  "Count",
  "Median",
  "StandardDeviation",
  "Variance"
];
function mapTarget(t, role) {
  if (!t || typeof t !== "object") return null;
  const x = t;
  const col = x.column, mea = x.measure;
  const table = x.table || typeof col === "object" && col?.table || typeof mea === "object" && mea?.table || "";
  const field = {
    table,
    column: typeof col === "string" ? col : col && col.name || null,
    measure: typeof mea === "string" ? mea : mea && mea.name || null,
    aggregation: x.aggregationFunction || x.aggregation || null,
    role: role || x.role || null,
    hidden: x.hidden === true
  };
  if (!field.column && !field.measure && x.displayName) field.column = x.displayName;
  if (!field.table && !field.column && !field.measure) return null;
  return field;
}
async function gatherFields(visual) {
  if (typeof visual.getDataFields !== "function")
    throw new PbiExportError(
      "authoringApiMissing",
      "Reading a visual's fields needs powerbi-report-authoring: load it after powerbi-client."
    );
  let roles = [];
  try {
    const caps = typeof visual.getCapabilities === "function" ? await visual.getCapabilities() : null;
    if (caps && Array.isArray(caps.dataRoles)) roles = caps.dataRoles.map((r) => r.name).filter(Boolean);
  } catch {
  }
  if (roles.length === 0) roles = FALLBACK_ROLES;
  const fields = [];
  for (const role of roles) {
    let targets;
    try {
      targets = await visual.getDataFields(role);
    } catch {
      continue;
    }
    if (!Array.isArray(targets)) continue;
    for (const t of targets) {
      const f = mapTarget(t, role);
      if (f) fields.push(f);
    }
  }
  return fields;
}
async function gatherFilters(report, page, visual, onOpeningPage, signal) {
  const all = [];
  const pushAll = (arr) => {
    if (Array.isArray(arr)) for (const f of arr) all.push(f);
  };
  try {
    pushAll(await report.getFilters());
  } catch {
  }
  try {
    pushAll(await page.getFilters());
  } catch {
  }
  try {
    pushAll(await visual.getFilters());
  } catch {
  }
  let visuals = [];
  try {
    visuals = await page.getVisuals();
  } catch {
  }
  let activated = false;
  for (const v of visuals) {
    if (v.type !== "slicer") continue;
    let state = null;
    let lastErr = null;
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
        await new Promise((r) => setTimeout(r, 500));
      }
    }
    if (!state) {
      const title = v.title && v.title.trim() ? v.title : v.name;
      const fallback = await slicerStateFromBookmark(report, page, v);
      if (fallback === "no-filter") continue;
      throw new PbiExportError(
        "slicerUnreadable",
        `Couldn't read slicer “${title}” (${errorText(lastErr)}` + (fallback ? "; the bookmark shows a selection the API can't translate yet" : "") + "). Export aborted rather than ignoring its selection.",
        { cause: lastErr }
      );
    }
    pushAll(state.filters);
  }
  return all;
}
async function slicerStateFromBookmark(report, page, slicer) {
  try {
    const vc = (await captureVisualContainers(report, page))[slicer.name];
    if (!vc || !vc.singleVisual) return null;
    const general = vc.singleVisual.objects?.merge?.general || [];
    return general.some((g) => g && g.properties && g.properties.filter) ? "filter" : "no-filter";
  } catch {
    return null;
  }
}
async function captureVisualContainers(report, page) {
  const bookmark = await report.bookmarksManager.capture({ allPages: true });
  const state = await decodeBookmarkState(bookmark.state || "");
  return state?.explorationState?.sections?.[page.name]?.visualContainers || {};
}
async function decodeBookmarkState(state) {
  const bytes = Uint8Array.from(atob(state), (c) => c.charCodeAt(0));
  if (bytes[0] !== 31 || bytes[1] !== 139) return JSON.parse(new TextDecoder().decode(bytes));
  const stream = new Blob([bytes]).stream().pipeThrough(new DecompressionStream("gzip"));
  return JSON.parse(await new Response(stream).text());
}
function sourceEntity(x) {
  return x?.Expression?.SourceRef?.Entity;
}
function mapOrderByExpression(x) {
  if (!x) return null;
  if (x.Column) return { table: sourceEntity(x.Column), column: x.Column.Property };
  if (x.Measure) return { table: sourceEntity(x.Measure), measure: x.Measure.Property };
  const c = x.Aggregation?.Expression?.Column;
  if (c) return { table: sourceEntity(c), column: c.Property, aggregation: QUERY_AGGREGATE_FUNCTIONS[x.Aggregation.Function] || "Sum" };
  return null;
}
async function gatherSort(report, page, visual) {
  let orderBy;
  try {
    orderBy = (await captureVisualContainers(report, page))[visual.name]?.singleVisual?.orderBy;
  } catch (e) {
    return { sort: [], source: "unavailable: " + errorText(e) };
  }
  if (!Array.isArray(orderBy) || orderBy.length === 0) return { sort: [], source: "none (implicit order)" };
  const sort = [];
  let unsupported = 0;
  for (const o of orderBy) {
    const key = mapOrderByExpression(o?.Expression);
    if (!key || !key.table) {
      unsupported++;
      continue;
    }
    sort.push({ ...key, direction: o.Direction === 2 ? "Descending" : "Ascending" });
  }
  return { sort, source: "bookmark" + (unsupported ? ` (${unsupported} unsupported key(s) skipped)` : "") };
}
function visualTitle(visual, page) {
  return visual.title && visual.title.trim() ? visual.title.trim() : page.displayName;
}
function suggestedFileName(title, now = /* @__PURE__ */ new Date()) {
  const p = (n) => String(n).padStart(2, "0");
  const stamp = `${now.getFullYear()}${p(now.getMonth() + 1)}${p(now.getDate())}-${p(now.getHours())}${p(now.getMinutes())}${p(now.getSeconds())}`;
  const safe2 = title.replace(/[\\/:*?"<>|,\u0000-\u001f]/g, "_").trim().slice(0, 80);
  return `${safe2 || "export"}-${stamp}.csv`;
}

// src/http.ts
var CONTRACT_VERSION = "1";
var CONTRACT_HEADER = "X-PbiVisualExport-Contract";
var ApiClient = class {
  constructor(apiBase, opts) {
    this.opts = opts;
    this.warnedContract = false;
    this.base = apiBase.replace(/\/+$/, "");
  }
  url(path) {
    return `${this.base}/${path.replace(/^\/+/, "")}`;
  }
  postJson(path, body, signal) {
    return this.send(path, {
      method: "POST",
      body: JSON.stringify(body),
      signal,
      headers: { "Content-Type": "application/json", [CONTRACT_HEADER]: CONTRACT_VERSION }
    });
  }
  get(path, signal) {
    return this.send(path, { method: "GET", cache: "no-store", signal });
  }
  async send(path, init) {
    const headers = new Headers(await resolveHeaders(this.opts.headers));
    for (const [k, v] of Object.entries(init.headers || {})) headers.set(k, v);
    let resp;
    try {
      resp = await this.opts.fetch(this.url(path), { ...init, headers, credentials: this.opts.credentials });
    } catch (e) {
      if (isAbort(e)) throw aborted(init.signal ?? void 0);
      throw new PbiExportError("network", `Couldn't reach the export service (${e?.message || e}).`, { cause: e });
    }
    const contract = resp.headers.get(CONTRACT_HEADER);
    if (contract && contract !== CONTRACT_VERSION && !this.warnedContract) {
      this.warnedContract = true;
      this.opts.onWarning(`PbiVisualExport: the server speaks contract ${contract}, this client ${CONTRACT_VERSION}. Load the client from the server ({apiBase}/client.js) to keep them in step.`);
    }
    return resp;
  }
};
async function resolveHeaders(source) {
  return typeof source === "function" ? await source() : source;
}
async function readJson(resp) {
  const text = await resp.text();
  if (!resp.ok) throw errorFromText(text, resp.status);
  try {
    return JSON.parse(text);
  } catch {
    throw new PbiExportError("http", `The export service returned an unexpected response (HTTP ${resp.status}).`, { status: resp.status });
  }
}

// src/transfer.ts
function canPickFile() {
  return typeof globalThis.showSaveFilePicker === "function";
}
async function pickSaveFile(suggestedName) {
  if (!canPickFile()) return { unavailable: true };
  try {
    const handle = await globalThis.showSaveFilePicker({
      suggestedName,
      types: [{ description: "CSV file", accept: { "text/csv": [".csv"] } }]
    });
    return { handle };
  } catch (e) {
    if (e?.name === "AbortError") return { cancelled: true };
    return { unavailable: true, reason: e };
  }
}
async function discardFile(file) {
  if (!file) return;
  try {
    if ((await file.getFile()).size === 0 && typeof file.remove === "function") await file.remove();
  } catch {
  }
}
function formatSize(bytes) {
  const [value, unit] = bytes < 1048576 ? [bytes / 1024, " KB"] : [bytes / 1048576, " MB"];
  return value.toLocaleString(void 0, { minimumFractionDigits: 1, maximumFractionDigits: 1 }) + unit;
}
function seconds(started) {
  return ((performance.now() - started) / 1e3).toFixed(1) + "s";
}
async function saveToFile(api, request, file, report, signal) {
  const started = performance.now();
  const fileName = file.name;
  report({ phase: "checking", method: "file", fileName, message: "Exporting… waiting for the first rows" });
  let resp;
  try {
    resp = await api.postJson("csv", request, signal);
    if (!resp.ok) throw errorFromText(await resp.text(), resp.status);
    if (!resp.body) throw new PbiExportError("downloadFailed", "The response has no body.");
  } catch (e) {
    await discardFile(file);
    throw e;
  }
  let bytes = 0;
  const counter = new TransformStream({
    transform(chunk, controller) {
      bytes += chunk.byteLength;
      controller.enqueue(chunk);
    }
  });
  const tick = () => report({
    phase: "streaming",
    method: "file",
    fileName,
    bytes,
    message: `Exporting… ${formatSize(bytes)} saved to ${fileName} (${seconds(started)})`
  });
  const timer = setInterval(tick, 1e3);
  try {
    const writable = await file.createWritable();
    await resp.body.pipeThrough(counter).pipeTo(writable, { signal });
  } catch (e) {
    if (!resp.body.locked) resp.body.cancel().catch(() => {
    });
    await discardFile(file);
    if (isAbort(e) || signal?.aborted) throw aborted(signal);
    throw new PbiExportError(
      "downloadFailed",
      `The download stopped after ${formatSize(bytes)} (${errorText(e)}); nothing was saved.`,
      { cause: e }
    );
  } finally {
    clearInterval(timer);
  }
  report({
    phase: "done",
    method: "file",
    fileName,
    bytes,
    message: `Done – saved ${fileName} (${formatSize(bytes)} in ${seconds(started)})`
  });
  return { bytes };
}
async function downloadViaTicket(api, request, report, pollIntervalMs, signal) {
  if (typeof document === "undefined")
    throw new PbiExportError("downloadFailed", 'A browser download needs a document (use target "file" elsewhere).');
  report({ phase: "checking", method: "download", message: "Checking the export…" });
  const prepared = await readJson(await api.postJson("prepare", request, signal));
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
    if (signal?.aborted) frame.remove();
    else setTimeout(() => frame.remove(), 6e4);
  }
}
function watchDownload(api, prepared, frame, started, report, pollIntervalMs, signal) {
  const fileName = prepared.fileName;
  const statusPath = `download/${encodeURIComponent(prepared.ticket)}/status`;
  return new Promise((resolve, reject) => {
    let settled = false;
    let failures = 0;
    let polling = false;
    const finish = (err, rows = 0) => {
      if (settled) return;
      settled = true;
      clearInterval(timer);
      frame.removeEventListener("load", onLoad);
      signal?.removeEventListener("abort", onAbort);
      if (err) reject(err);
      else resolve(rows);
    };
    const poll = async () => {
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
            report({ phase: "waiting", method: "download", fileName, message: `Exporting… waiting for the download to start (${seconds(started)})` });
            break;
          case "Streaming":
            report({ phase: "streaming", method: "download", fileName, rows, message: `Exporting… ${rows.toLocaleString()} rows sent to the browser as ${fileName} (${seconds(started)})` });
            break;
          case "Completed":
            report({ phase: "done", method: "download", fileName, rows, message: `Done – ${rows.toLocaleString()} rows downloaded as ${fileName} in ${seconds(started)} (see the browser's downloads).` });
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
    function onAbort() {
      finish(aborted(signal));
    }
    signal?.addEventListener("abort", onAbort);
    async function onLoad() {
      let text = "";
      try {
        if (frame.contentWindow?.location.href === "about:blank") return;
        text = frame.contentDocument?.body?.innerText || "";
      } catch {
      }
      if (!text) {
        try {
          text = (await poll())?.error || "";
        } catch (e) {
          return finish(e);
        }
      }
      finish(text ? errorFromText(text, 0) : new PbiExportError("downloadFailed", "The download failed."));
    }
    frame.addEventListener("load", onLoad);
  });
}

// src/index.ts
var VERSION = true ? "0.1.0" : "0.0.0-dev";
var DEFAULT_COMMAND = "pbiVisualExport";
function createExporter(options) {
  return new Exporter(options);
}
var Exporter = class {
  constructor(options) {
    this.disposers = [];
    if (!options?.report) throw new TypeError("createExporter: `report` is required.");
    this.report = options.report;
    const fetchImpl = options.fetch ?? globalThis.fetch.bind(globalThis);
    this.api = new ApiClient(options.apiBase ?? "/pbi-export", {
      headers: options.headers,
      credentials: options.credentials ?? "same-origin",
      fetch: fetchImpl,
      onWarning: options.onWarning ?? ((m) => console.warn(m))
    });
    this.pollIntervalMs = options.pollIntervalMs ?? 1e3;
    this.ids = { workspaceId: options.workspaceId, reportId: options.reportId, datasetId: options.datasetId };
  }
  /** The URL of an endpoint of the module (e.g. `url("client.js")`). */
  url(path) {
    return this.api.url(path);
  }
  /** Exportable visuals on the report's visible pages. */
  async listVisuals() {
    const result = [];
    for (const page of await this.report.getPages()) {
      if (page.visibility === 1) continue;
      let visuals;
      try {
        visuals = await page.getVisuals();
      } catch {
        continue;
      }
      for (const visual of visuals) {
        if (SKIP_VISUAL_TYPES.has(visual.type)) continue;
        const title = visual.title && visual.title.trim() ? visual.title : visual.type;
        result.push({ key: `${page.name}::${visual.name}`, page, visual, title, label: `${title}  –  [${page.displayName}]` });
      }
    }
    return result;
  }
  /** A visual by page and visual name (e.g. from a `commandTriggered` event). */
  async getVisual(pageName, visualName) {
    const page = typeof this.report.getPageByName === "function" ? await this.report.getPageByName(pageName) : (await this.report.getPages()).find((p) => p.name === pageName);
    if (!page) throw new Error(`Page "${pageName}" isn't in the report.`);
    const visual = typeof page.getVisualByName === "function" ? await page.getVisualByName(visualName) : (await page.getVisuals()).find((v) => v.name === visualName);
    if (!visual) throw new Error(`Visual "${visualName}" isn't on page "${page.displayName}".`);
    return { page, visual };
  }
  /**
   * The export request for a visual: its fields, the filters and slicer selections that
   * apply to it, and (with `sort`) its sort. Nothing is sent.
   */
  async buildRequest(visual, options = {}) {
    const page = this.pageOf(visual, options.page);
    const started = performance.now();
    const report = (p) => options.onProgress?.({ ...p, elapsedMs: performance.now() - started });
    const { workspaceId, reportId, datasetId } = this.resolveIds();
    report({ phase: "gathering", message: "Gathering visual configuration…" });
    const fields = await gatherFields(visual);
    throwIfAborted(options.signal);
    const filters = await gatherFilters(
      this.report,
      page,
      visual,
      () => report({ phase: "opening-page", message: `Opening page “${page.displayName}” to read its slicers…` }),
      options.signal
    );
    throwIfAborted(options.signal);
    const applySort = options.sort === true;
    const sortInfo = applySort ? await gatherSort(this.report, page, visual) : { sort: [], source: "not read (sort off)" };
    return {
      workspaceId,
      reportId,
      ...datasetId ? { datasetId } : {},
      pageName: page.name,
      visualName: visual.name,
      visualTitle: visualTitle(visual, page),
      fields,
      filters,
      applyFormatStrings: options.formatted !== false,
      applySort,
      sort: sortInfo.sort,
      sortSource: sortInfo.source
    };
  }
  /**
   * Exports a visual to CSV. Resolves when the file is complete, with `status: "cancelled"`
   * if the user dismissed the Save As dialog; rejects with a {@link PbiExportError}.
   * Call it directly from a click handler: the Save As dialog needs the user gesture.
   */
  async exportVisual(visual, options = {}) {
    const started = performance.now();
    const elapsedMs = () => performance.now() - started;
    const page = this.pageOf(visual, options.page);
    const target = options.target ?? "auto";
    let file = null;
    if (target !== "download") {
      const pick = await pickSaveFile(options.fileName || suggestedFileName(visualTitle(visual, page)));
      if ("cancelled" in pick) return { status: "cancelled", elapsedMs: elapsedMs() };
      if ("handle" in pick) file = pick.handle;
      else if (target === "file")
        throw new PbiExportError(
          "pickerUnavailable",
          "The Save As dialog isn't available here" + (pick.reason ? ` (${errorText(pick.reason)})` : canPickFile() ? "" : " (it needs a Chromium browser)") + ".",
          { cause: pick.reason }
        );
    }
    const method = file ? "file" : "download";
    let request;
    try {
      request = await this.buildRequest(visual, { ...options, page });
      if (request.fields.length === 0)
        throw new PbiExportError("noFields", "Couldn't detect any fields on the visual.");
    } catch (e) {
      await discardFile(file);
      throw e;
    }
    options.onRequest?.(request, method);
    const report = (p) => options.onProgress?.({ ...p, elapsedMs: elapsedMs() });
    if (file) {
      const { bytes } = await saveToFile(this.api, request, file, report, options.signal);
      return { status: "completed", method, fileName: file.name, bytes, elapsedMs: elapsedMs(), request };
    }
    const { rows, prepared } = await downloadViaTicket(this.api, request, report, this.pollIntervalMs, options.signal);
    return { status: "completed", method, fileName: prepared.fileName, rows, elapsedMs: elapsedMs(), request, prepared };
  }
  /** Dev XMLA device-code sign-in status, or null if the server doesn't use device code. */
  async getSignInStatus(signal) {
    const resp = await this.api.get("xmla-auth", signal);
    if (resp.status === 404) return null;
    return readJson(resp);
  }
  /** Resolves once the XMLA device-code sign-in (after a `signInRequired` error) completes. */
  async waitForSignIn(options = {}) {
    for (; ; ) {
      throwIfAborted(options.signal);
      try {
        const s = await this.getSignInStatus(options.signal);
        if (!s || s.signedIn) return;
      } catch (e) {
        if (isAbort(e)) throw e;
      }
      await new Promise((r) => setTimeout(r, options.intervalMs ?? 3e3));
    }
  }
  /**
   * The extension command for a visual's "More options" menu, for hosts that build their
   * own `settings.extensions`. Handle it with {@link handleCommand}.
   */
  visualMenuCommand(options = {}) {
    return {
      name: options.name ?? DEFAULT_COMMAND,
      title: options.title ?? "Export all data (CSV)",
      ...options.icon ? { icon: options.icon } : {},
      extend: {
        visualOptionsMenu: {
          title: options.title ?? "Export all data (CSV)",
          menuLocation: options.menuLocation === "bottom" ? 0 : 1
          // models.MenuLocation
        }
      }
    };
  }
  /**
   * Handles a `commandTriggered` event: exports the visual if it's this module's command.
   * Returns null for other commands.
   */
  async handleCommand(event, options = {}) {
    const d = event?.detail;
    if (!d || d.command !== (options.name ?? DEFAULT_COMMAND)) return null;
    const { page, visual } = await this.getVisual(d.page?.name, d.visual?.name);
    return this.exportVisual(visual, { target: "download", ...options, page });
  }
  /**
   * Adds "Export all data (CSV)" to every visual's "More options" menu. This replaces the
   * report's `settings.extensions` (use {@link visualMenuCommand} to merge your own).
   * Returns a function that removes the handler.
   */
  async addVisualMenuCommand(options = {}) {
    if (typeof this.report.updateSettings !== "function")
      throw new TypeError("addVisualMenuCommand needs report.updateSettings (powerbi-client).");
    await this.report.updateSettings({ extensions: { commands: [this.visualMenuCommand(options)] } });
    const name = options.name ?? DEFAULT_COMMAND;
    const handler = (event) => {
      if (event?.detail?.command !== name) return;
      let visual;
      (async () => {
        const found = await this.getVisual(event.detail.page?.name, event.detail.visual?.name);
        visual = found.visual;
        const exportOptions = typeof options.export === "function" ? options.export(found.visual) : options.export;
        const result = await this.exportVisual(found.visual, { target: "download", ...exportOptions, page: found.page });
        options.onResult?.(result, found.visual);
      })().catch((e) => (options.onError ?? ((err) => console.error("PbiVisualExport:", err)))(e, visual));
    };
    this.report.on("commandTriggered", handler);
    const dispose = () => this.report.off("commandTriggered", handler);
    this.disposers.push(dispose);
    return dispose;
  }
  /** Removes the handlers added by this exporter. */
  dispose() {
    for (const d of this.disposers.splice(0)) d();
  }
  pageOf(visual, page) {
    const p = page ?? visual?.page;
    if (!p || typeof p.getVisuals !== "function")
      throw new TypeError("The visual's page is unknown: pass `page` (or a visual from page.getVisuals()).");
    return p;
  }
  resolveIds() {
    const config = this.report.config || {};
    const reportId = this.ids.reportId || safe(() => this.report.getId?.()) || config.id;
    let workspaceId = this.ids.workspaceId;
    if (!workspaceId && config.embedUrl) {
      try {
        workspaceId = new URL(config.embedUrl).searchParams.get("groupId") || void 0;
      } catch {
      }
    }
    if (!workspaceId || !reportId)
      throw new PbiExportError(
        "missingIds",
        `The report's ${!workspaceId ? "workspace" : "report"} ID is unknown: pass ${!workspaceId ? "workspaceId" : "reportId"} to createExporter.`
      );
    return { workspaceId, reportId, datasetId: this.ids.datasetId || config.datasetId };
  }
};
function safe(f) {
  try {
    return f();
  } catch {
    return void 0;
  }
}
export {
  CONTRACT_VERSION,
  Exporter,
  PbiExportError,
  VERSION,
  createExporter
};
