// Power BI Visual -> CSV export front end.
// Embeds the report, lets the user pick a visual, gathers that visual's fields
// (columns/measures) plus all active filters/slicers, and posts them to the
// back end which streams an arbitrarily large CSV.

(function () {
    // Where the host mapped the export module (app.MapPbiVisualExport("/pbi-export")).
    const API = "/pbi-export";
    const models = window["powerbi-client"].models;
    const powerbi = window.powerbi;

    const selectEl = document.getElementById("visual-select");
    const reportSelectEl = document.getElementById("report-select");
    const exportBtn = document.getElementById("export-btn");
    const previewBtn = document.getElementById("preview-btn");
    const nativeBtn = document.getElementById("native-btn");
    const formatChk = document.getElementById("format-chk");
    const sortChk = document.getElementById("sort-chk");
    const statusEl = document.getElementById("status");
    const errorEl = document.querySelector(".error-container");
    const debugPanel = document.getElementById("debug-panel");
    const debugJson = document.getElementById("debug-json");
    const debugMeta = document.getElementById("debug-meta");
    const debugCopy = document.getElementById("debug-copy");
    const debugHide = document.getElementById("debug-hide");

    let report = null;
    let embedInfo = null;
    let visualsByKey = {};   // key -> { visual, page }

    function setStatus(msg) { statusEl.textContent = msg || ""; }

    function showError(text) {
        errorEl.style.display = "block";
        errorEl.innerHTML = "<strong>Error:</strong><br/>" + String(text).split("\n").join("<br/>");
    }

    function escapeHtml(s) {
        return String(s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
    }

    // Dev mode: the XMLA endpoint needs its own device-code sign-in. Show the code and
    // poll until the server reports it is complete.
    let signInPoll = null;
    function showSignIn(info) {
        errorEl.style.display = "block";
        const link = info.verificationUri
            ? `<a href="${escapeHtml(info.verificationUri)}" target="_blank" rel="noopener">${escapeHtml(info.verificationUri)}</a>`
            : "the device login page";
        errorEl.innerHTML = info.userCode
            ? `<strong>XMLA sign-in required.</strong><br/>Open ${link} and enter code <code>${escapeHtml(info.userCode)}</code>. Export will be available once sign-in completes.`
            : `<strong>XMLA sign-in required.</strong><br/>${escapeHtml(info.error || "See the server log for the device code.")}`;
        if (signInPoll) clearInterval(signInPoll);
        signInPoll = setInterval(async () => {
            try {
                const s = await (await fetch(API + "/xmla-auth")).json();
                if (s.signedIn) {
                    clearInterval(signInPoll);
                    signInPoll = null;
                    errorEl.style.display = "none";
                    setStatus("XMLA sign-in complete \u2013 click Export data again.");
                }
            } catch { /* keep polling */ }
        }, 3000);
    }

    async function init() {
        await loadReports();
    }

    async function loadReports() {
        setStatus("Loading reports\u2026");
        let resp;
        try {
            resp = await fetch("/embedinfo/reports");
        } catch (e) {
            showError("Could not reach the server: " + e.message);
            return;
        }
        if (!resp.ok) {
            showError(await resp.text());
            return;
        }
        const reports = await resp.json();

        reportSelectEl.innerHTML = "";
        if (!reports.length) {
            reportSelectEl.innerHTML = "<option>(no reports)</option>";
            setStatus("");
            showError("No Power BI reports found in this workspace.");
            return;
        }
        let defaultValue = reports[0].id;
        for (const r of reports) {
            const opt = document.createElement("option");
            opt.value = r.id;
            opt.textContent = r.name;
            reportSelectEl.appendChild(opt);
            if (r.isDefault) defaultValue = r.id;
        }
        reportSelectEl.value = defaultValue;
        reportSelectEl.disabled = false;

        await loadReport(reportSelectEl.value);
    }

    async function loadReport(reportId) {
        // Reset selectors + panels for the new report.
        selectEl.disabled = true;
        exportBtn.disabled = true;
        previewBtn.disabled = true;
        nativeBtn.disabled = true;
        selectEl.innerHTML = "<option>Loading report\u2026</option>";
        debugPanel.style.display = "none";
        errorEl.style.display = "none";
        visualsByKey = {};
        setStatus("Loading report\u2026");

        let resp;
        try {
            resp = await fetch("/embedinfo/getembedinfo?reportId=" + encodeURIComponent(reportId));
        } catch (e) {
            showError("Could not reach the server: " + e.message);
            return;
        }
        if (!resp.ok) {
            showError(await resp.text());
            return;
        }
        embedInfo = await resp.json();

        const config = {
            type: "report",
            tokenType: models.TokenType.Embed,
            accessToken: embedInfo.accessToken,
            embedUrl: embedInfo.embedUrl,
            settings: { panes: { filters: { visible: true }, pageNavigation: { visible: true } } }
        };

        const container = document.getElementById("report-container");
        powerbi.reset(container);
        report = powerbi.embed(container, config);

        report.off("loaded");
        report.on("loaded", () => { populateVisuals().catch(err => console.error(err)); });

        report.off("error");
        report.on("error", (e) => console.error(e.detail));
    }

    async function populateVisuals() {
        setStatus("Reading visuals\u2026");
        visualsByKey = {};
        const options = [];
        try {
            const pages = await report.getPages();
            for (const page of pages) {
                if (page.visibility === models.SectionVisibility.HiddenInViewMode) continue;
                let visuals = [];
                try { visuals = await page.getVisuals(); } catch (e) { continue; }
                for (const v of visuals) {
                    if (SKIP_VISUAL_TYPES.has(v.type)) continue;
                    const key = page.name + "::" + v.name;
                    visualsByKey[key] = { visual: v, page: page };
                    const title = (v.title && v.title.trim()) ? v.title : v.type;
                    options.push({ key, label: `${title}  \u2013  [${page.displayName}]` });
                }
            }
        } catch (e) {
            showError("Failed to enumerate visuals: " + e.message);
            return;
        }

        selectEl.innerHTML = "";
        if (options.length === 0) {
            selectEl.innerHTML = "<option>(no exportable visuals)</option>";
            setStatus("");
            return;
        }
        for (const o of options) {
            const opt = document.createElement("option");
            opt.value = o.key;
            opt.textContent = o.label;
            selectEl.appendChild(opt);
        }
        selectEl.disabled = false;
        exportBtn.disabled = false;
        previewBtn.disabled = false;
        nativeBtn.disabled = false;
        setStatus(`${options.length} visual(s)`);
    }

    const SKIP_VISUAL_TYPES = new Set([
        "textbox", "image", "shape", "actionButton", "basicShape",
        "card", "bookmarkNavigator", "filter"
    ]);

    // Roles to probe when a visual doesn't advertise capabilities.
    const FALLBACK_ROLES = ["Category", "Series", "Y", "Values", "Rows", "Columns",
        "Axis", "Legend", "Details", "Group", "Tooltips"];

    function mapTarget(t, role) {
        if (!t || typeof t !== "object") return null;
        const table = t.table || (t.column && t.column.table) || (t.measure && t.measure.table);
        const field = {
            table: table || "",
            column: typeof t.column === "string" ? t.column : (t.column && t.column.name) || null,
            measure: typeof t.measure === "string" ? t.measure : (t.measure && t.measure.name) || null,
            aggregation: t.aggregationFunction || t.aggregation || null,
            role: role || t.role || null,
            // "Hide fields on the visual": computed server-side but not exported.
            hidden: t.hidden === true
        };
        if (!field.column && !field.measure && t.displayName) field.column = t.displayName;
        if (!field.table && !field.column && !field.measure) return null;
        return field;
    }

    async function gatherFields(visual) {
        const fields = [];
        let roleNames = [];
        try {
            const caps = await visual.getCapabilities();
            if (caps && Array.isArray(caps.dataRoles))
                roleNames = caps.dataRoles.map(r => r.name).filter(Boolean);
        } catch (e) { /* fall through to fallback roles */ }
        if (roleNames.length === 0) roleNames = FALLBACK_ROLES;

        for (const role of roleNames) {
            let dfs = null;
            try { dfs = await visual.getDataFields(role); } catch (e) { continue; }
            if (!Array.isArray(dfs)) continue;
            for (const t of dfs) {
                const f = mapTarget(t, role);
                if (f) fields.push(f);
            }
        }
        return fields;
    }

    async function gatherFilters(visual, page) {
        const all = [];
        const pushAll = (arr) => { if (Array.isArray(arr)) for (const f of arr) all.push(f); };

        try { pushAll(await report.getFilters()); } catch (e) { }
        try { pushAll(await page.getFilters()); } catch (e) { }
        try { pushAll(await visual.getFilters()); } catch (e) { }

        // Slicers on the page contribute their current selection as filters. A slicer
        // whose state can't be read must abort the export: dropping it would silently
        // over-export. Slicers on a page that isn't rendered fail with
        // visualConfigIsNotInitialized, so activate the page and retry.
        let visuals = [];
        try { visuals = await page.getVisuals(); } catch (e) { }
        let activated = false;
        for (const v of visuals) {
            if (v.type !== "slicer") continue;
            let state = null, lastErr = null;
            for (let attempt = 0; attempt < 30 && !state; attempt++) {
                try {
                    state = await v.getSlicerState();
                } catch (e) {
                    lastErr = e;
                    if (errorText(e) !== "visualConfigIsNotInitialized") break;
                    if (!activated) {
                        activated = true;
                        if ((await report.getActivePage()).name !== page.name) {
                            setStatus(`Opening page \u201c${page.displayName}\u201d to read its slicers\u2026`);
                            await page.setActive();
                        }
                    }
                    await new Promise(r => setTimeout(r, 500));
                }
            }
            if (!state) {
                // The SDK's slicer serializer can throw on some slicers (e.g. one with no
                // saved selection throws "reading 'general'"; select-all slicers have been
                // reported to throw slicerSerializationFailed). The bookmark holds the same
                // state as a semantic-query filter: no filter there = no selection.
                const title = (v.title && v.title.trim()) ? v.title : v.name;
                const fallback = await slicerStateFromBookmark(page, v);
                if (fallback === "no-filter") continue;
                throw new Error(`Couldn't read slicer \u201c${title}\u201d (${errorText(lastErr)}` +
                    (fallback ? `; the bookmark shows a selection the API can't translate yet` : "") +
                    "). Export aborted rather than ignoring its selection.");
            }
            pushAll(state.filters);
        }

        return all;
    }

    // "no-filter" if the captured bookmark shows the slicer has no selection, "filter" if it
    // has one, null if it can't be determined.
    async function slicerStateFromBookmark(page, slicer) {
        try {
            const vc = (await captureVisualContainers(page))[slicer.name];
            if (!vc || !vc.singleVisual) return null;
            const general = ((vc.singleVisual.objects || {}).merge || {}).general || [];
            return general.some(g => g && g.properties && g.properties.filter) ? "filter" : "no-filter";
        } catch (e) {
            return null;
        }
    }

    async function captureVisualContainers(page) {
        // allPages: the default capture only includes the active page.
        const bookmark = await report.bookmarksManager.capture({ allPages: true });
        const state = await decodeBookmarkState(bookmark.state);
        return (((state.explorationState || {}).sections || {})[page.name] || {}).visualContainers || {};
    }

    function errorText(e) {
        if (!e) return "unknown error";
        if (typeof e === "string") return e;
        return e.message || e.detailedMessage || JSON.stringify(e);
    }

    // ---- Sort ----
    // The SDK can set a visual's sort (visual.sortBy) but not read it. A captured bookmark
    // holds it: explorationState.sections[page].visualContainers[visual].singleVisual.orderBy
    // reflects the live sort (header clicks, sortBy) or, if unchanged, the report's saved
    // default sort. No orderBy = the visual has no explicit sort.

    // semanticQuery QueryAggregateFunction (fabric/item/report/definition/semanticQuery schema).
    const QUERY_AGGREGATE_FUNCTIONS = ["Sum", "Avg", "DistinctCount", "Min", "Max", "Count",
        "Median", "StandardDeviation", "Variance"];

    async function decodeBookmarkState(state) {
        const bytes = Uint8Array.from(atob(state), c => c.charCodeAt(0));
        // Captured states are gzip'd JSON (base64 "H4sI..."); fall back to plain JSON.
        if (bytes[0] !== 0x1f || bytes[1] !== 0x8b) return JSON.parse(new TextDecoder().decode(bytes));
        const stream = new Blob([bytes]).stream().pipeThrough(new DecompressionStream("gzip"));
        return JSON.parse(await new Response(stream).text());
    }

    function sourceEntity(x) {
        return x && x.Expression && x.Expression.SourceRef && x.Expression.SourceRef.Entity;
    }

    function mapOrderByExpression(x) {
        if (!x) return null;
        if (x.Column) return { table: sourceEntity(x.Column), column: x.Column.Property };
        if (x.Measure) return { table: sourceEntity(x.Measure), measure: x.Measure.Property };
        if (x.Aggregation && x.Aggregation.Expression && x.Aggregation.Expression.Column) {
            const c = x.Aggregation.Expression.Column;
            return {
                table: sourceEntity(c), column: c.Property,
                aggregation: QUERY_AGGREGATE_FUNCTIONS[x.Aggregation.Function] || "Sum"
            };
        }
        return null; // e.g. hierarchy levels: not supported
    }

    async function gatherSort(visual, page) {
        let orderBy;
        try {
            const vc = await captureVisualContainers(page);
            orderBy = vc[visual.name] && vc[visual.name].singleVisual && vc[visual.name].singleVisual.orderBy;
        } catch (e) {
            return { sort: [], source: "unavailable: " + ((e && (e.message || e.detailedMessage)) || String(e)) };
        }
        if (!Array.isArray(orderBy) || orderBy.length === 0) return { sort: [], source: "none (implicit order)" };

        const sort = [];
        const unsupported = [];
        for (const o of orderBy) {
            const key = mapOrderByExpression(o && o.Expression);
            if (!key || !key.table) { unsupported.push(JSON.stringify(o && o.Expression)); continue; }
            key.direction = o.Direction === 2 ? "Descending" : "Ascending";
            sort.push(key);
        }
        return { sort, source: "bookmark" + (unsupported.length ? ` (${unsupported.length} unsupported key(s) skipped)` : "") };
    }

    function describeSort(payload) {
        if (!payload.applySort) return "sort off";
        if (!payload.sort.length) return "implicit order";
        return "sort: " + payload.sort.map(s =>
            (s.measure || (s.aggregation ? `${s.aggregation}(${s.column})` : s.column)) +
            (s.direction === "Descending" ? " \u2193" : " \u2191")).join(", ");
    }

    function setBusy(busy) {
        exportBtn.disabled = busy;
        previewBtn.disabled = busy;
        nativeBtn.disabled = busy;
        formatChk.disabled = busy;
        sortChk.disabled = busy;
        selectEl.disabled = busy;
        reportSelectEl.disabled = busy;
    }

    async function buildPayload(entry) {
        const { visual, page } = entry;
        const fields = await gatherFields(visual);
        const filters = await gatherFilters(visual, page);
        const sortInfo = sortChk.checked ? await gatherSort(visual, page) : { sort: [], source: "not read (sort off)" };
        return {
            // IDs are required; the server resolves names (and checks any sent) from them.
            workspaceId: embedInfo.workspaceId,
            workspaceName: embedInfo.workspaceName,
            datasetId: embedInfo.datasetId,
            datasetName: embedInfo.datasetName,
            reportId: embedInfo.reportId,
            reportName: embedInfo.reportName,
            pageName: page.name,
            visualName: visual.name,
            visualTitle: (visual.title && visual.title.trim()) ? visual.title : page.displayName,
            fields: fields,
            filters: filters,
            applyFormatStrings: formatChk.checked,
            // Server-side ORDER BY; off avoids sorting multi-million-row results on the capacity.
            applySort: sortChk.checked,
            sort: sortInfo.sort,
            sortSource: sortInfo.source   // diagnostic only (ignored by the server)
        };
    }

    function renderDebug(payload, note) {
        debugJson.textContent = JSON.stringify(payload, null, 2);
        debugMeta.textContent =
            `${payload.workspaceName} / ${payload.datasetName} \u00b7 ${payload.fields.length} field(s) \u00b7 ${payload.filters.length} filter(s) \u00b7 ${payload.applyFormatStrings ? "formatted" : "raw values"} \u00b7 ${describeSort(payload)} \u00b7 ${note} \u00b7 ${new Date().toLocaleTimeString()}`;
        debugPanel.style.display = "block";
    }

    async function previewConfig() {
        const entry = visualsByKey[selectEl.value];
        if (!entry) return;
        setStatus("Reading visual configuration\u2026");
        try {
            const payload = await buildPayload(entry);
            renderDebug(payload, payload.fields.length ? "preview (not sent)" : "preview \u2013 no fields detected");
            setStatus(`${payload.fields.length} field(s), ${payload.filters.length} filter(s)`);
        } catch (e) {
            setStatus("");
            showError("Failed to read visual configuration: " + e.message);
        }
    }

    // Download strategy (neither holds the file in memory, unlike fetch + blob):
    //  1. File System Access API (Chromium): a real "Save as" dialog, then the response
    //     body of POST {API}/csv is piped straight into the chosen file - with progress
    //     and errors in the page.
    //  2. Otherwise POST {API}/prepare validates the export and returns a one-time ticket;
    //     a hidden iframe then navigates to GET {API}/download/{ticket}, so the browser's
    //     download manager applies Content-Disposition and streams to disk. Progress and
    //     errors come from polling GET {API}/download/{ticket}/status.
    async function doExport() {
        const entry = visualsByKey[selectEl.value];
        if (!entry) return;

        // Must come first, while the click still counts as a user gesture.
        const pick = await pickSaveFile(entry);
        if (pick.cancelled) { setStatus("Export cancelled."); return; }
        const file = pick.handle || null;

        setBusy(true);
        errorEl.style.display = "none";
        setStatus("Gathering visual configuration\u2026");

        let payload;
        try {
            payload = await buildPayload(entry);
        } catch (e) {
            await discardFile(file);
            setStatus("");
            showError("Failed to read visual configuration: " + e.message);
            setBusy(false);
            return;
        }

        if (payload.fields.length === 0) {
            await discardFile(file);
            renderDebug(payload, "no fields detected \u2013 not sent");
            setStatus("");
            showError("Could not detect any fields on the selected visual.");
            setBusy(false);
            return;
        }

        try {
            if (file) await exportToFile(payload, file);
            else await exportViaTicket(payload);
        } catch (e) {
            setStatus("");
            if (e && e.signIn) showSignIn(e.signIn);
            else showError("Export failed: " + (e && e.message || e));
        } finally {
            setBusy(false);
        }
    }

    class ApiError extends Error {
        constructor(message, signIn) { super(message); this.signIn = signIn || null; }
    }

    function apiErrorFromText(text, status) {
        let body = null;
        try { body = JSON.parse(text); } catch { /* plain text */ }
        return new ApiError((body && body.error) || (text && text.trim()) || (status ? "HTTP " + status : "the server rejected the request."),
            body && body.signInRequired ? body : null);
    }

    function postJson(url, payload) {
        return fetch(url, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(payload) });
    }

    function formatSize(bytes) {
        const [value, unit] = bytes < 1048576 ? [bytes / 1024, " KB"] : [bytes / 1048576, " MB"];
        return value.toLocaleString(undefined, { minimumFractionDigits: 1, maximumFractionDigits: 1 }) + unit;
    }

    function suggestedFileName(entry) {
        const title = (entry.visual.title && entry.visual.title.trim()) ? entry.visual.title : entry.page.displayName;
        const d = new Date(), p = n => String(n).padStart(2, "0");
        const stamp = `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`;
        return `${title.replace(/[\\/:*?"<>|,\u0000-\u001f]/g, "_").trim().slice(0, 80) || "export"}-${stamp}.csv`;
    }

    // { handle } | { cancelled: true } | { fallback: true }
    async function pickSaveFile(entry) {
        if (typeof window.showSaveFilePicker !== "function") return { fallback: true };
        try {
            const handle = await window.showSaveFilePicker({
                suggestedName: suggestedFileName(entry),
                types: [{ description: "CSV file", accept: { "text/csv": [".csv"] } }]
            });
            return { handle };
        } catch (e) {
            if (e && e.name === "AbortError") return { cancelled: true };
            // e.g. SecurityError in a cross-origin iframe or without a user gesture.
            console.warn("Save picker unavailable; using a browser download instead.", e);
            return { fallback: true };
        }
    }

    // Deletes the picked file if nothing was saved into it (Chromium creates it empty when
    // the user confirms the dialog). Never deletes a file that has content.
    async function discardFile(file) {
        if (!file) return;
        try {
            if ((await file.getFile()).size === 0 && typeof file.remove === "function") await file.remove();
        } catch { /* best effort */ }
    }

    async function exportToFile(payload, file) {
        renderDebug(payload, `sent to POST ${API}/csv \u2192 ` + file.name);
        const started = performance.now();
        const elapsed = () => ((performance.now() - started) / 1000).toFixed(1) + "s";
        setStatus("Exporting\u2026 waiting for the first rows");
        let resp;
        try {
            resp = await postJson(API + "/csv", payload);
            if (!resp.ok) throw apiErrorFromText(await resp.text(), resp.status);
        } catch (e) {
            await discardFile(file);
            throw e;
        }

        let bytes = 0;
        const counter = new TransformStream({
            transform(chunk, controller) { bytes += chunk.byteLength; controller.enqueue(chunk); }
        });
        const tick = setInterval(() => setStatus(`Exporting\u2026 ${formatSize(bytes)} saved to ${file.name} (${elapsed()})`), 1000);
        try {
            const writable = await file.createWritable();
            await resp.body.pipeThrough(counter).pipeTo(writable);
        } catch (e) {
            if (!resp.body.locked) resp.body.cancel().catch(() => { });
            await discardFile(file);
            throw new Error(`the download stopped after ${formatSize(bytes)} (${e && e.message || e}); nothing was saved.`);
        } finally {
            clearInterval(tick);
        }
        setStatus(`Done \u2013 saved ${file.name} (${formatSize(bytes)} in ${elapsed()})`);
    }

    async function exportViaTicket(payload) {
        renderDebug(payload, `sent to POST ${API}/prepare, then GET ${API}/download/{ticket} (browser download)`);
        setStatus("Checking the export\u2026");
        const pre = await postJson(API + "/prepare", payload);
        const text = await pre.text();
        if (!pre.ok) throw apiErrorFromText(text, pre.status);
        const info = JSON.parse(text);

        const frame = document.createElement("iframe");
        frame.hidden = true;
        document.body.appendChild(frame);
        const started = performance.now();
        const ticketUrl = `${API}/download/${encodeURIComponent(info.ticket)}`;
        const done = watchDownload(ticketUrl, info.fileName, frame, started);
        frame.src = ticketUrl;
        try {
            await done;
        } finally {
            // Removing the frame while the browser still writes could cancel the download.
            setTimeout(() => frame.remove(), 60000);
        }
    }

    // Polls the ticket's status until the server has streamed the whole file (or failed).
    // The frame never loads for a download; if it does, the response was an error page.
    function watchDownload(ticketUrl, fileName, frame, started) {
        const secs = () => ((performance.now() - started) / 1000).toFixed(1) + "s";
        return new Promise((resolve, reject) => {
            let settled = false;
            const finish = (err) => {
                if (settled) return;
                settled = true;
                clearInterval(timer);
                frame.removeEventListener("load", onLoad);
                err ? reject(err) : resolve();
            };
            const timer = setInterval(async () => {
                try {
                    const resp = await fetch(ticketUrl + "/status", { cache: "no-store" });
                    const s = await resp.json();
                    if (!resp.ok) return finish(new Error(s.error || "HTTP " + resp.status));
                    const rows = Number(s.rows || 0).toLocaleString();
                    switch (s.state) {
                        case "Issued": setStatus(`Exporting\u2026 waiting for the download to start (${secs()})`); break;
                        case "Streaming": setStatus(`Exporting\u2026 ${rows} rows sent to the browser as ${fileName} (${secs()})`); break;
                        case "Completed":
                            setStatus(`Done \u2013 ${rows} rows downloaded as ${fileName} in ${secs()} (see the browser's downloads).`);
                            return finish();
                        default: return finish(new Error(s.error || `the download ${String(s.state).toLowerCase()}.`));
                    }
                } catch { /* keep polling */ }
            }, 1000);
            function onLoad() {
                let text = "";
                try {
                    if (frame.contentWindow.location.href === "about:blank") return;
                    text = frame.contentDocument.body ? frame.contentDocument.body.innerText : "";
                } catch { /* unreadable frame */ }
                finish(apiErrorFromText(text, 0));
            }
            frame.addEventListener("load", onLoad);
        });
    }

    // Power BI's built-in export (visual.exportData) for side-by-side comparison with
    // the API export. The service caps this at 30,000 summarized rows, but on wide
    // visuals a 30k request can run for many minutes; 2,000 rows is plenty to compare
    // data types and format strings (infra/compare-exports.py).
    const NATIVE_EXPORT_ROWS = 2000;
    async function doNativeExport() {
        const entry = visualsByKey[selectEl.value];
        if (!entry) return;
        setBusy(true);
        setStatus("Native export (visual.exportData)\u2026");
        const started = performance.now();
        try {
            const result = await entry.visual.exportData(models.ExportDataType.Summarized, NATIVE_EXPORT_ROWS);
            const secs = ((performance.now() - started) / 1000).toFixed(1);
            const title = (entry.visual.title && entry.visual.title.trim()) ? entry.visual.title : entry.page.displayName;
            const fileName = title.replace(/[^a-z0-9\-_ ]/gi, "_") + "-native.csv";
            triggerDownload(new Blob([result.data], { type: "text/csv;charset=utf-8" }), fileName);
            setStatus(`Native export done in ${secs}s \u2013 downloaded ${fileName} (first ${NATIVE_EXPORT_ROWS.toLocaleString()} rows)`);
        } catch (e) {
            setStatus("");
            showError("Native export failed: " + (e && (e.message || e.detailedMessage || JSON.stringify(e))));
        } finally {
            setBusy(false);
        }
    }

    function triggerDownload(blob, fileName) {
        const url = URL.createObjectURL(blob);
        const a = document.createElement("a");
        a.href = url;
        a.download = fileName;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        setTimeout(() => URL.revokeObjectURL(url), 5000);
    }

    exportBtn.addEventListener("click", doExport);
    nativeBtn.addEventListener("click", doNativeExport);
    formatChk.addEventListener("change", () => {
        if (debugPanel.style.display === "block") previewConfig();
    });
    sortChk.addEventListener("change", () => {
        if (debugPanel.style.display === "block") previewConfig();
    });
    previewBtn.addEventListener("click", previewConfig);
    debugHide.addEventListener("click", () => { debugPanel.style.display = "none"; });
    debugCopy.addEventListener("click", async () => {
        try {
            await navigator.clipboard.writeText(debugJson.textContent);
            debugCopy.textContent = "Copied!";
            setTimeout(() => { debugCopy.textContent = "Copy JSON"; }, 1500);
        } catch (e) { /* clipboard blocked; ignore */ }
    });
    // Keep the debug panel in sync when the user switches visuals while it is open.
    selectEl.addEventListener("change", () => {
        if (debugPanel.style.display === "block") previewConfig();
    });
    // Switching the report re-embeds and repopulates the visual list.
    reportSelectEl.addEventListener("change", () => {
        loadReport(reportSelectEl.value).catch(err => showError("Failed to load report: " + err.message));
    });
    init();
})();
