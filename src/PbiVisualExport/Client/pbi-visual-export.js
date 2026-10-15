/*! pbi-visual-export 0.1.0 | MIT | https://github.com/dbrownems/PbiVisualExport */
"use strict";
var PbiVisualExport = (() => {
  var __defProp = Object.defineProperty;
  var __getOwnPropDesc = Object.getOwnPropertyDescriptor;
  var __getOwnPropNames = Object.getOwnPropertyNames;
  var __hasOwnProp = Object.prototype.hasOwnProperty;
  var __export = (target, all) => {
    for (var name in all)
      __defProp(target, name, { get: all[name], enumerable: true });
  };
  var __copyProps = (to, from, except, desc) => {
    if (from && typeof from === "object" || typeof from === "function") {
      for (let key of __getOwnPropNames(from))
        if (!__hasOwnProp.call(to, key) && key !== except)
          __defProp(to, key, { get: () => from[key], enumerable: !(desc = __getOwnPropDesc(from, key)) || desc.enumerable });
    }
    return to;
  };
  var __toCommonJS = (mod) => __copyProps(__defProp({}, "__esModule", { value: true }), mod);

  // src/index.ts
  var index_exports = {};
  __export(index_exports, {
    CONTRACT_VERSION: () => CONTRACT_VERSION,
    Exporter: () => Exporter,
    PbiExportError: () => PbiExportError,
    VERSION: () => VERSION,
    createExporter: () => createExporter
  });

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
    "Count",
    "Min",
    "Max",
    "CountNonNull",
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
    if (x.hierarchy && x.hierarchyLevel) return { ...field, column: null, measure: null, hierarchy: x.hierarchy, hierarchyLevel: x.hierarchyLevel };
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
  async function gatherFilters(report, page, visual, onOpeningPage, signal, snapshot = createBookmarkSnapshot(report)) {
    const all = [];
    const pushAll = (arr) => {
      if (Array.isArray(arr)) for (const f of arr) all.push(f);
    };
    pushAll(await levelFilters("report", () => report.getFilters(), snapshot, page, visual));
    pushAll(await levelFilters("page", () => page.getFilters(), snapshot, page, visual));
    pushAll(await levelFilters("visual", () => visual.getFilters(), snapshot, page, visual));
    let visuals;
    try {
      visuals = await page.getVisuals();
    } catch (e) {
      throw new PbiExportError(
        "slicerUnreadable",
        `Couldn't list the page's visuals to read its slicers (${errorText(e)}). Export aborted rather than ignoring them.`,
        { cause: e }
      );
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
              snapshot.invalidate();
            }
          }
          await new Promise((r) => setTimeout(r, 500));
        }
      }
      if (!state) {
        const title = v.title && v.title.trim() ? v.title : v.name;
        const fallback = await slicerStateFromBookmark(snapshot, page, v);
        if (fallback === "no-filter") continue;
        if (Array.isArray(fallback)) {
          pushAll(fallback);
          continue;
        }
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
  async function levelFilters(level, read, snapshot, page, visual) {
    try {
      const filters = await read();
      return Array.isArray(filters) ? filters : [];
    } catch (e) {
      let holder;
      let fallback = null;
      try {
        const state = await snapshot.get();
        const section = state?.sections?.[page.name];
        holder = level === "report" ? state : level === "page" ? section : section?.visualContainers?.[visual.name];
        if (holder) fallback = bookmarkFilters(holder.filters);
      } catch {
      }
      if (fallback) return fallback;
      throw new PbiExportError(
        "filtersUnreadable",
        `Couldn't read the ${level} filters (${errorText(e)}` + (holder ? "; the bookmark shows a filter the API can't translate yet" : "") + "). Export aborted rather than ignoring them.",
        { cause: e }
      );
    }
  }
  function bookmarkFilters(container) {
    const out = [];
    const entries = [
      ...Array.isArray(container?.byExpr) ? container.byExpr : [],
      ...container?.byName && typeof container.byName === "object" ? Object.values(container.byName) : []
    ];
    for (const entry of entries) {
      if (!entry?.filter) continue;
      const filters = queryFilters(entry.filter);
      if (!filters) return null;
      out.push(...filters);
    }
    return out;
  }
  async function slicerStateFromBookmark(snapshot, page, slicer) {
    try {
      const vc = (await visualContainers(snapshot, page))[slicer.name];
      if (!vc || !vc.singleVisual) return null;
      const general = vc.singleVisual.objects?.merge?.general || [];
      const filters = general.map((g) => g?.properties?.filter).filter((f) => f);
      if (filters.length === 0) return "no-filter";
      const translated = filters.map((f) => queryFilters(f.filter));
      return translated.every((f) => f) ? translated.flat() : "filter";
    } catch {
      return null;
    }
  }
  function queryFilters(query) {
    const from = {};
    for (const f of query?.From || []) if (f && f.Name && f.Entity) from[f.Name] = f.Entity;
    const where = query?.Where;
    if (!Array.isArray(where) || where.length === 0) return null;
    const out = [];
    for (const w of where) {
      const filter = conditionFilter(w?.Condition, from);
      if (!filter) return null;
      out.push(filter);
    }
    return out;
  }
  function queryTarget(x, from) {
    const entity = (e) => e?.SourceRef?.Entity || from[e?.SourceRef?.Source];
    const col = x?.Column;
    if (col) {
      const table2 = entity(col.Expression);
      return table2 && col.Property ? { table: table2, column: col.Property } : null;
    }
    const level = x?.HierarchyLevel;
    const h = level?.Expression?.Hierarchy;
    if (!level?.Level || !h) return null;
    const variation = h.Expression?.PropertyVariationSource;
    const table = entity(variation ? variation.Expression : h.Expression);
    const hierarchy = variation ? variation.Property : h.Hierarchy;
    return table && hierarchy ? { table, hierarchy, hierarchyLevel: level.Level } : null;
  }
  function conditionFilter(cond, from) {
    const basic = (t, operator, values, extra) => ({
      $schema: "http://powerbi.com/product/schema#basic",
      target: t,
      filterType: 1,
      operator,
      values,
      requireSingleSelection: false,
      ...extra
    });
    const cmp = cond?.Comparison;
    if (cmp) {
      const t = queryTarget(cmp.Left, from);
      return t && cmp.ComparisonKind === 0 && cmp.Right && "AnyValue" in cmp.Right ? basic(t, "All", [], { selectAll: true }) : null;
    }
    const notIn = cond?.Not?.Expression?.In;
    const inList = notIn || cond?.In;
    const exprs = inList?.Expressions, rows = inList?.Values;
    if (!Array.isArray(exprs) || exprs.length === 0 || !Array.isArray(rows)) return null;
    const targets = exprs.map((e) => queryTarget(e, from));
    if (targets.some((t) => !t)) return null;
    const tuples = [];
    for (const row of rows) {
      if (!Array.isArray(row) || row.length !== exprs.length) return null;
      const values = row.map(literalValue);
      if (values.some((v) => v === void 0)) return null;
      tuples.push(values);
    }
    if (exprs.length === 1) return basic(targets[0], notIn ? "NotIn" : "In", tuples.map((t) => t[0]));
    return {
      $schema: "http://powerbi.com/product/schema#tuple",
      target: targets,
      filterType: 6,
      operator: notIn ? "NotIn" : "In",
      values: tuples.map((t) => t.map((value) => ({ value })))
    };
  }
  function literalValue(x) {
    const s = x?.Literal?.Value;
    if (typeof s !== "string") return void 0;
    if (s === "null") return null;
    if (s === "true" || s === "false") return s === "true";
    let m = /^'([\s\S]*)'$/.exec(s);
    if (m) return m[1].replace(/''/g, "'");
    m = /^(-?\d+)L$/.exec(s);
    if (m) {
      const n = Number(m[1]);
      return Number.isSafeInteger(n) ? n : m[1];
    }
    m = /^(-?(?:\d+\.?\d*|\.\d+)(?:E[+-]?\d+)?)[DM]$/i.exec(s);
    if (m) {
      const n = Number(m[1]);
      return String(n) === m[1] ? n : m[1];
    }
    m = /^(?:datetime|date)'([^']*)'$/.exec(s);
    return m ? m[1] : void 0;
  }
  async function captureExplorationState(report) {
    const bookmark = await report.bookmarksManager.capture({ allPages: true });
    return (await decodeBookmarkState(bookmark.state || ""))?.explorationState;
  }
  function createBookmarkSnapshot(report) {
    let memo;
    return {
      get() {
        if (!memo) {
          const p = captureExplorationState(report);
          memo = p;
          p.catch(() => {
            if (memo === p) memo = void 0;
          });
        }
        return memo;
      },
      invalidate() {
        memo = void 0;
      }
    };
  }
  async function visualContainers(snapshot, page) {
    return (await snapshot.get())?.sections?.[page.name]?.visualContainers || {};
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
    if (x.HierarchyLevel) {
      const t = queryTarget(x, {});
      return t && "hierarchy" in t ? { table: t.table, hierarchy: t.hierarchy, hierarchyLevel: t.hierarchyLevel } : null;
    }
    const e = x.Aggregation?.Expression;
    const aggregation = QUERY_AGGREGATE_FUNCTIONS[x.Aggregation?.Function] || "Sum";
    if (e?.Column) return { table: sourceEntity(e.Column), column: e.Column.Property, aggregation };
    if (e?.HierarchyLevel) {
      const t = queryTarget(e, {});
      return t && "hierarchy" in t ? { table: t.table, hierarchy: t.hierarchy, hierarchyLevel: t.hierarchyLevel, aggregation } : null;
    }
    return null;
  }
  async function gatherSort(report, page, visual, snapshot = createBookmarkSnapshot(report)) {
    let orderBy;
    try {
      orderBy = (await visualContainers(snapshot, page))[visual.name]?.singleVisual?.orderBy;
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
  function browserTimeZone() {
    try {
      return Intl.DateTimeFormat().resolvedOptions().timeZone || void 0;
    } catch {
      return void 0;
    }
  }
  function suggestedFileName(title, now = /* @__PURE__ */ new Date()) {
    const p = (n) => String(n).padStart(2, "0");
    const stamp = `${now.getFullYear()}${p(now.getMonth() + 1)}${p(now.getDate())}-${p(now.getHours())}${p(now.getMinutes())}${p(now.getSeconds())}`;
    const safe2 = title.replace(/[\\/:*?"<>|,\u0000-\u001f]/g, "_").trim().slice(0, 80);
    return `${safe2 || "export"}-${stamp}.csv`;
  }

  // src/http.ts
  var CONTRACT_VERSION = "2";
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
  async function downloadBuffered(api, request, fileName, report, signal) {
    if (typeof document === "undefined")
      throw new PbiExportError("downloadFailed", 'A browser download needs a document (use target "file" elsewhere).');
    report({ phase: "checking", method: "download", fileName, message: "Checking the export…" });
    const prepared = await readJson(await api.postJson("prepare", request, signal));
    throwIfAborted(signal);
    const started = performance.now();
    report({ phase: "waiting", method: "download", fileName, message: "Exporting… waiting for the first rows" });
    const resp = await api.postJson("csv", request, signal);
    if (!resp.ok) throw errorFromText(await resp.text(), resp.status);
    if (!resp.body) throw new PbiExportError("downloadFailed", "The response has no body.");
    let bytes = 0;
    const counter = new TransformStream({
      transform(chunk, controller) {
        bytes += chunk.byteLength;
        controller.enqueue(chunk);
      }
    });
    const tick = () => report({
      phase: "streaming",
      method: "download",
      fileName,
      bytes,
      message: `Exporting… ${formatSize(bytes)} received (${seconds(started)}); the file is saved when the export completes`
    });
    const timer = setInterval(tick, 1e3);
    let blob;
    try {
      const body = resp.body.pipeThrough(counter, { signal });
      blob = await new Response(body, { headers: { "Content-Type": "text/csv;charset=utf-8" } }).blob();
    } catch (e) {
      if (isAbort(e) || signal?.aborted) throw aborted(signal);
      throw new PbiExportError(
        "downloadFailed",
        `The download stopped after ${formatSize(bytes)} (${errorText(e)}); nothing was saved.`,
        { cause: e }
      );
    } finally {
      clearInterval(timer);
    }
    saveBlob(blob, fileName);
    report({
      phase: "done",
      method: "download",
      fileName,
      bytes,
      message: `Done – ${fileName} (${formatSize(bytes)} in ${seconds(started)}) is in the browser's downloads.`
    });
    return { bytes, prepared };
  }
  function saveBlob(blob, fileName) {
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = fileName;
    a.hidden = true;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 6e4);
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
      const snapshot = createBookmarkSnapshot(this.report);
      const filters = await gatherFilters(
        this.report,
        page,
        visual,
        () => report({ phase: "opening-page", message: `Opening page “${page.displayName}” to read its slicers…` }),
        options.signal,
        snapshot
      );
      throwIfAborted(options.signal);
      const applySort = options.sort === true;
      const sortInfo = applySort ? await gatherSort(this.report, page, visual, snapshot) : { sort: [], source: "not read (sort off)" };
      const timeZone = browserTimeZone();
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
        ...timeZone ? { timeZone } : {},
        sortSource: sortInfo.source
      };
    }
    /**
     * Exports a visual to CSV. Resolves when the file is complete, with `status: "cancelled"`
     * if the user dismissed the Save As dialog; rejects with a {@link PbiExportError}.
     * Call it directly from a click handler: the Save As dialog needs the user gesture.
     * Without the dialog (`download`), the browser holds the whole CSV until the export completes.
     */
    async exportVisual(visual, options = {}) {
      const started = performance.now();
      const elapsedMs = () => performance.now() - started;
      const page = this.pageOf(visual, options.page);
      const target = options.target ?? "auto";
      const fileName = options.fileName || suggestedFileName(visualTitle(visual, page));
      let file = null;
      if (target !== "download") {
        const pick = await pickSaveFile(fileName);
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
        const { bytes: bytes2 } = await saveToFile(this.api, request, file, report, options.signal);
        return { status: "completed", method, fileName: file.name, bytes: bytes2, elapsedMs: elapsedMs(), request };
      }
      const { bytes, prepared } = await downloadBuffered(this.api, request, fileName, report, options.signal);
      return { status: "completed", method, fileName, bytes, elapsedMs: elapsedMs(), request, prepared };
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
  return __toCommonJS(index_exports);
})();
