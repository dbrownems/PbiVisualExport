// Sample host front end: embeds a report, lists its visuals and exports the selected one
// through the pbi-visual-export client, which the module serves at {API}/client.js
// (global `PbiVisualExport`). Also adds "Export all data (CSV)" to every visual's
// "More options" menu, and keeps Power BI's native export for comparison.

(function () {
    // Where the host mapped the export module (app.MapPbiVisualExport("/pbi-export")).
    const API = "/pbi-export";
    const models = window["powerbi-client"].models;
    const powerbi = window.powerbi;
    const { createExporter } = window.PbiVisualExport;

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
    let exporter = null;
    let visualsByKey = {};   // key -> { visual, page }

    function setStatus(msg) { statusEl.textContent = msg || ""; }

    function escapeHtml(s) {
        return String(s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
    }

    function showError(text) {
        errorEl.style.display = "block";
        errorEl.innerHTML = "<strong>Error:</strong><br/>" + escapeHtml(text).split("\n").join("<br/>");
    }

    // Warnings from prepare (e.g. a sort key outside the visual, skipped). A filter the server
    // can't translate fails the export instead of showing up here.
    function showWarnings(result) {
        const warnings = result && result.prepared && result.prepared.warnings;
        if (!warnings || !warnings.length) return;
        errorEl.style.display = "block";
        errorEl.innerHTML = "<strong>Exported with warnings:</strong><br/>" + warnings.map(w => "\u2022 " + escapeHtml(w)).join("<br/>");
    }

    // Dev mode: the XMLA endpoint needs its own device-code sign-in. Show the code and
    // wait until the server reports it is complete.
    let signInWait = null;
    function showSignIn(info) {
        errorEl.style.display = "block";
        const link = info.verificationUri
            ? `<a href="${escapeHtml(info.verificationUri)}" target="_blank" rel="noopener">${escapeHtml(info.verificationUri)}</a>`
            : "the device login page";
        errorEl.innerHTML = info.userCode
            ? `<strong>XMLA sign-in required.</strong><br/>Open ${link} and enter code <code>${escapeHtml(info.userCode)}</code>. Export will be available once sign-in completes.`
            : `<strong>XMLA sign-in required.</strong><br/>${escapeHtml(info.error || "See the server log for the device code.")}`;
        if (signInWait) signInWait.abort();
        const wait = signInWait = new AbortController();
        exporter.waitForSignIn({ signal: wait.signal }).then(() => {
            if (signInWait !== wait) return;
            signInWait = null;
            errorEl.style.display = "none";
            setStatus("XMLA sign-in complete \u2013 click Export data again.");
        }, () => { /* superseded */ });
    }

    // Shows an export failure; PbiExportError.code says what went wrong.
    function showExportError(e, what) {
        setStatus("");
        if (e && e.code === "signInRequired") return showSignIn(e.signIn || {});
        if (e && e.code === "aborted") return setStatus("Export cancelled.");
        let text = what + " failed: " + (e && e.message || e);
        if (e && Array.isArray(e.problems) && e.problems.length) text += "\n" + e.problems.map(p => "\u2022 " + p).join("\n");
        showError(text);
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
        if (exporter) exporter.dispose();
        powerbi.reset(container);
        report = powerbi.embed(container, config);
        exporter = createExporter({
            report,
            apiBase: API,
            workspaceId: embedInfo.workspaceId,
            reportId: embedInfo.reportId,
            datasetId: embedInfo.datasetId
        });

        report.off("loaded");
        report.on("loaded", () => {
            populateVisuals().catch(err => console.error(err));
            addMenuCommand().catch(err => console.warn("Couldn't add the visual menu command:", err));
        });

        report.off("error");
        report.on("error", (e) => console.error(e.detail));
    }

    async function populateVisuals() {
        setStatus("Reading visuals\u2026");
        visualsByKey = {};
        let visuals;
        try {
            visuals = await exporter.listVisuals();
        } catch (e) {
            showError("Failed to enumerate visuals: " + e.message);
            return;
        }

        selectEl.innerHTML = "";
        if (visuals.length === 0) {
            selectEl.innerHTML = "<option>(no exportable visuals)</option>";
            setStatus("");
            return;
        }
        for (const v of visuals) {
            visualsByKey[v.key] = v;
            const opt = document.createElement("option");
            opt.value = v.key;
            opt.textContent = v.label;
            selectEl.appendChild(opt);
        }
        selectEl.disabled = false;
        exportBtn.disabled = false;
        previewBtn.disabled = false;
        nativeBtn.disabled = false;
        setStatus(`${visuals.length} visual(s)`);
    }

    // "Export all data (CSV)" in each visual's "More options" menu. The click happens inside
    // the report's iframe, so it downloads through the browser rather than a Save As dialog.
    async function addMenuCommand() {
        await exporter.addVisualMenuCommand({
            export: () => ({
                formatted: formatChk.checked,
                sort: sortChk.checked,
                onProgress: p => setStatus(p.message),
                onRequest: (request) => { errorEl.style.display = "none"; renderDebug(request, "visual menu \u2192 browser download"); }
            }),
            onResult: (result) => showWarnings(result),
            onError: (e) => showExportError(e, "Export")
        });
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

    function exportOptions(entry) {
        return {
            page: entry.page,
            formatted: formatChk.checked,
            // Server-side ORDER BY; off avoids sorting multi-million-row results on the capacity.
            sort: sortChk.checked,
            onProgress: p => setStatus(p.message)
        };
    }

    function describeSort(request) {
        if (!request.applySort) return "sort off";
        if (!request.sort.length) return "implicit order";
        return "sort: " + request.sort.map(s =>
            (s.measure || (s.aggregation ? `${s.aggregation}(${s.column})` : s.column)) +
            (s.direction === "Descending" ? " \u2193" : " \u2191")).join(", ");
    }

    function renderDebug(request, note) {
        debugJson.textContent = JSON.stringify(request, null, 2);
        debugMeta.textContent =
            `${embedInfo.workspaceName} / ${embedInfo.datasetName} \u00b7 ${request.fields.length} field(s) \u00b7 ${request.filters.length} filter(s) \u00b7 ${request.applyFormatStrings ? "formatted" : "raw values"} \u00b7 ${describeSort(request)} \u00b7 ${note} \u00b7 ${new Date().toLocaleTimeString()}`;
        debugPanel.style.display = "block";
    }

    async function previewConfig() {
        const entry = visualsByKey[selectEl.value];
        if (!entry) return;
        setStatus("Reading visual configuration\u2026");
        try {
            const request = await exporter.buildRequest(entry.visual, exportOptions(entry));
            renderDebug(request, request.fields.length ? "preview (not sent)" : "preview \u2013 no fields detected");
            setStatus(`${request.fields.length} field(s), ${request.filters.length} filter(s)`);
        } catch (e) {
            setStatus("");
            showError("Failed to read visual configuration: " + e.message);
        }
    }

    // The client streams the CSV to disk without holding it in memory: into a Save As file
    // (Chromium's File System Access API) or, elsewhere, as a browser download
    // (POST {API}/prepare issues a one-time ticket; GET {API}/download/{ticket} streams it).
    async function doExport() {
        const entry = visualsByKey[selectEl.value];
        if (!entry) return;
        setBusy(true);
        errorEl.style.display = "none";
        try {
            const result = await exporter.exportVisual(entry.visual, {
                ...exportOptions(entry),
                onRequest: (request, method) => renderDebug(request, method === "file"
                    ? `sent to POST ${API}/csv \u2192 Save As`
                    : `sent to POST ${API}/prepare, then GET ${API}/download/{ticket} (browser download)`)
            });
            if (result.status === "cancelled") setStatus("Export cancelled.");
            else showWarnings(result);
        } catch (e) {
            showExportError(e, "Export");
        } finally {
            setBusy(false);
        }
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
