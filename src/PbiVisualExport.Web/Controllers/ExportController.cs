using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PbiVisualExport.Web.Auth;
using PbiVisualExport.Web.Models;
using PbiVisualExport.Web.Options;
using PbiVisualExport.Web.Services;

namespace PbiVisualExport.Web.Controllers;

[ApiController]
[Route("export")]
public sealed partial class ExportController : ControllerBase
{
    /// <summary>Cookie set when a form download starts: <c>pbiexport-dl-{downloadToken}=1</c>.</summary>
    public const string DownloadCookiePrefix = "pbiexport-dl-";

    private readonly CsvExportService _csv;
    private readonly PowerBiCatalog _catalog;
    private readonly XmlaTokenProvider _xmlaTokens;
    private readonly PowerBIOptions _pbi;
    private readonly ExportOptions _export;
    private readonly JsonSerializerOptions _json;
    private readonly ILogger<ExportController> _log;

    public ExportController(CsvExportService csv, PowerBiCatalog catalog, XmlaTokenProvider xmlaTokens,
        IOptions<PowerBIOptions> pbi, IOptions<ExportOptions> export, IOptions<JsonOptions> json, ILogger<ExportController> log)
    {
        _csv = csv;
        _catalog = catalog;
        _xmlaTokens = xmlaTokens;
        _pbi = pbi.Value;
        _export = export.Value;
        _json = json.Value.JsonSerializerOptions;
        _log = log;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex DownloadTokenPattern();

    [HttpPost("csv")]
    public Task ExportCsv([FromBody] VisualExportRequest request, CancellationToken ct) =>
        StreamCsvAsync(request, downloadToken: null, ct);

    /// <summary>
    /// Browser-download variant of <c>/export/csv</c>: a plain HTML form POST (fields
    /// <c>request</c> = the JSON request, <c>downloadToken</c>) so the browser itself
    /// handles <c>Content-Disposition</c> and streams the file to disk. Form posts can be
    /// sent cross-site, hence the antiforgery token (rendered into the page).
    /// </summary>
    [HttpPost("csv/form")]
    [Consumes("application/x-www-form-urlencoded", "multipart/form-data")]
    [ValidateAntiForgeryToken]
    public async Task ExportCsvForm([FromForm(Name = "request")] string requestJson, [FromForm] string? downloadToken, CancellationToken ct)
    {
        VisualExportRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize<VisualExportRequest>(requestJson, _json);
        }
        catch (JsonException) { }
        if (request is null)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(new { error = "Invalid export request: the 'request' form field is not a valid JSON request." }, ct);
            return;
        }
        await StreamCsvAsync(request, downloadToken, ct);
    }

    /// <summary>
    /// Pre-flight for the form download, which can't report errors to the page: validates
    /// the request, builds the DAX plan and checks the XMLA credential. Same error shapes
    /// as <c>/export/csv</c> (400 / 401 <c>signInRequired</c> / 500).
    /// </summary>
    [HttpPost("prepare")]
    public async Task<IActionResult> Prepare([FromBody] VisualExportRequest request, CancellationToken ct)
    {
        var problems = await ValidateAsync(request, ct);
        if (problems.Count > 0)
            return BadRequest(new { error = "Invalid export request: " + string.Join(" ", problems), problems });
        try
        {
            var plan = await _csv.PlanAsync(request, ct);
            await _xmlaTokens.GetTokenAsync(ct);
            return Ok(new
            {
                fileName = BuildFileName(request.VisualTitle),
                columns = plan.Columns.Count,
                sort = plan.SortDescription,
                reportMeasures = plan.ReportMeasures,
                measureFilters = plan.MeasureFilters,
                warnings = plan.Warnings
            });
        }
        catch (ExportRequestException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (XmlaSignInRequiredException ex)
        {
            return Unauthorized(new { error = ex.Message, signInRequired = true, verificationUri = ex.VerificationUri, userCode = ex.UserCode });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Export pre-flight failed");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Export failed: " + ex.Message });
        }
    }

    private async Task StreamCsvAsync(VisualExportRequest request, string? downloadToken, CancellationToken ct)
    {
        try
        {
            var problems = await ValidateAsync(request, ct);
            if (problems.Count > 0)
            {
                Response.StatusCode = StatusCodes.Status400BadRequest;
                await Response.WriteAsJsonAsync(new { error = "Invalid export request: " + string.Join(" ", problems), problems }, ct);
                return;
            }

            Response.ContentType = "text/csv; charset=utf-8";
            var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
            disposition.SetHttpFileName(BuildFileName(request.VisualTitle)); // ASCII filename= plus UTF-8 filename*=
            Response.Headers.ContentDisposition = disposition.ToString();
            // Arrives with the response headers, i.e. when the download starts; the page
            // polls document.cookie for it (a form download has no completion event).
            if (downloadToken is not null && DownloadTokenPattern().IsMatch(downloadToken))
                Response.Cookies.Append(DownloadCookiePrefix + downloadToken, "1", new CookieOptions
                {
                    Path = "/",
                    MaxAge = TimeSpan.FromMinutes(5),
                    SameSite = SameSiteMode.Strict,
                    HttpOnly = false,
                    IsEssential = true
                });
            await _csv.ExportAsync(request, Response.Body, ct);
        }
        catch (ExportRequestException ex) when (!Response.HasStarted)
        {
            _log.LogWarning("Export request rejected: {Error}", ex.Message);
            Response.Clear();
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(new { error = ex.Message }, ct);
        }
        catch (XmlaSignInRequiredException ex) when (!Response.HasStarted)
        {
            Response.Clear();
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            await Response.WriteAsJsonAsync(new
            {
                error = ex.Message,
                signInRequired = true,
                verificationUri = ex.VerificationUri,
                userCode = ex.UserCode
            }, ct);
        }
        catch (Exception ex) when (!Response.HasStarted && ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Export failed before streaming started");
            Response.Clear();
            Response.StatusCode = StatusCodes.Status500InternalServerError;
            await Response.WriteAsJsonAsync(new { error = "Export failed: " + ex.Message }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Headers are gone; rethrowing aborts the connection so the client sees a failed
            // download rather than a silently truncated CSV.
            _log.LogError(ex, "Export failed mid-stream");
            throw;
        }
    }

    /// <summary>
    /// Perf baseline, only when <c>Export:EnableBenchmark</c> is true: runs the same query
    /// as <c>/export/csv</c> and reads every value without writing CSV. Returns timings.
    /// </summary>
    [HttpPost("bench")]
    public async Task<IActionResult> Bench([FromBody] VisualExportRequest request, CancellationToken ct)
    {
        if (!_export.EnableBenchmark) return NotFound();
        var problems = await ValidateAsync(request, ct);
        if (problems.Count > 0)
            return BadRequest(new { error = "Invalid export request: " + string.Join(" ", problems), problems });
        try
        {
            return Ok(await _csv.ReadOnlyAsync(request, ct));
        }
        catch (ExportRequestException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (XmlaSignInRequiredException ex)
        {
            return Unauthorized(new { error = ex.Message, signInRequired = true, verificationUri = ex.VerificationUri, userCode = ex.UserCode });
        }
    }

    /// <summary>XMLA credential status (dev device-code sign-in), polled by the UI after a 401.</summary>
    [HttpGet("xmla-auth")]
    public IActionResult XmlaAuthStatus()
    {
        var pending = _xmlaTokens.PendingDeviceCode;
        return Ok(new
        {
            mode = _xmlaTokens.Mode,
            signedIn = _xmlaTokens.SignedIn,
            message = pending?.Message,
            verificationUri = pending?.VerificationUri.ToString(),
            userCode = pending?.UserCode
        });
    }

    /// <summary>
    /// All six identifiers are required. IDs must be GUIDs, the workspace must be the
    /// configured one, and each name must match its ID (else metadata and data could
    /// come from different models).
    /// </summary>
    private async Task<List<string>> ValidateAsync(VisualExportRequest r, CancellationToken ct)
    {
        var problems = new List<string>();
        Guid ParseId(string? value, string field)
        {
            if (string.IsNullOrWhiteSpace(value)) problems.Add($"{field} is required.");
            else if (!Guid.TryParse(value, out var g)) problems.Add($"{field} must be a GUID.");
            else return g;
            return Guid.Empty;
        }
        void RequireName(string? value, string field)
        {
            if (string.IsNullOrWhiteSpace(value)) problems.Add($"{field} is required.");
        }

        var workspaceId = ParseId(r.WorkspaceId, "workspaceId");
        var datasetId = ParseId(r.DatasetId, "datasetId");
        var reportId = ParseId(r.ReportId, "reportId");
        RequireName(r.WorkspaceName, "workspaceName");
        RequireName(r.DatasetName, "datasetName");
        RequireName(r.ReportName, "reportName");
        if (problems.Count > 0) return problems;

        if (!Guid.TryParse(_pbi.WorkspaceId, out var allowed) || workspaceId != allowed)
        {
            problems.Add($"workspaceId {workspaceId} is not the configured workspace.");
            return problems;
        }

        var catalog = await _catalog.GetContainingAsync(workspaceId, datasetId, reportId, ct);
        if (!NameEquals(catalog.Name, r.WorkspaceName))
            problems.Add($"workspaceName '{r.WorkspaceName}' does not match workspace {workspaceId} ('{catalog.Name}').");

        if (!catalog.Datasets.TryGetValue(datasetId, out var datasetName))
            problems.Add($"datasetId {datasetId} was not found in the workspace.");
        else if (!NameEquals(datasetName, r.DatasetName))
            problems.Add($"datasetName '{r.DatasetName}' does not match dataset {datasetId} ('{datasetName}').");

        if (!catalog.Reports.TryGetValue(reportId, out var report))
            problems.Add($"reportId {reportId} was not found in the workspace.");
        else
        {
            if (!NameEquals(report.Name, r.ReportName))
                problems.Add($"reportName '{r.ReportName}' does not match report {reportId} ('{report.Name}').");
            if (report.DatasetId != datasetId)
                problems.Add($"report {reportId} is bound to dataset {report.DatasetId}, not {datasetId}.");
        }
        return problems;
    }

    private static bool NameEquals(string expected, string? actual) =>
        string.Equals(expected, actual?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string BuildFileName(string? visualTitle)
    {
        var baseName = string.IsNullOrWhiteSpace(visualTitle) ? "export" : visualTitle;
        foreach (var c in Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(c, '_');
        baseName = baseName.Replace(',', '_').Trim();
        if (baseName.Length > 80) baseName = baseName[..80];
        return $"{baseName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
    }
}
