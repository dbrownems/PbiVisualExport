using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace PbiVisualExport.Services;

/// <summary>Outcome of <see cref="ExportRequestResolver.ResolveAsync"/>: a context, or an HTTP error.</summary>
internal sealed record ResolveResult(VisualExportContext? Context, int StatusCode, string? Error, IReadOnlyList<string> Problems)
{
    public static ResolveResult Ok(VisualExportContext context) => new(context, StatusCodes.Status200OK, null, Array.Empty<string>());
    public static ResolveResult BadRequest(List<string> problems) =>
        new(null, StatusCodes.Status400BadRequest, "Invalid export request: " + string.Join(" ", problems), problems);
    public static ResolveResult Forbidden(string error) => new(null, StatusCodes.Status403Forbidden, error, Array.Empty<string>());
}

/// <summary>
/// Turns a client request into a <see cref="VisualExportContext"/>: parses the IDs, applies
/// the workspace allow-list, resolves the dataset and every name from the workspace
/// catalog (checking any the client sent), fills them into the request, then asks the
/// host's <see cref="IVisualExportAuthorizer"/>.
/// </summary>
internal sealed class ExportRequestResolver
{
    private readonly IPowerBiCatalog _catalog;
    private readonly IVisualExportAuthorizer _authorizer;
    private readonly PbiVisualExportOptions _options;

    public ExportRequestResolver(IPowerBiCatalog catalog, IVisualExportAuthorizer authorizer, IOptions<PbiVisualExportOptions> options)
    {
        _catalog = catalog;
        _authorizer = authorizer;
        _options = options.Value;
    }

    public async Task<ResolveResult> ResolveAsync(HttpContext http, VisualExportRequest r, CancellationToken ct)
    {
        var problems = new List<string>();
        Guid? ParseId(string? value, string field, bool required)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                if (required) problems.Add($"{field} is required.");
                return null;
            }
            if (Guid.TryParse(value, out var g)) return g;
            problems.Add($"{field} must be a GUID.");
            return null;
        }

        var workspaceId = ParseId(r.WorkspaceId, "workspaceId", required: true);
        var reportId = ParseId(r.ReportId, "reportId", required: true);
        var requestedDatasetId = ParseId(r.DatasetId, "datasetId", required: false);
        if (r.Fields.Count == 0) problems.Add("fields must list at least one field of the visual.");
        if (problems.Count > 0) return ResolveResult.BadRequest(problems);

        if (_options.AllowedWorkspaceIds.Count > 0 && !_options.AllowedWorkspaceIds.Contains(workspaceId!.Value))
            return ResolveResult.Forbidden($"Exports from workspace {workspaceId} are not allowed.");

        var catalog = await _catalog.GetContainingAsync(workspaceId!.Value, requestedDatasetId, reportId, ct);
        if (!catalog.Reports.TryGetValue(reportId!.Value, out var report))
            return ResolveResult.BadRequest(new() { $"reportId {reportId} was not found in workspace {workspaceId}." });

        var datasetId = report.DatasetId;
        if (requestedDatasetId is { } d && d != datasetId)
            problems.Add($"report {reportId} is bound to dataset {datasetId}, not {d}.");
        if (!catalog.Datasets.TryGetValue(datasetId, out var datasetName))
            problems.Add($"report {reportId} uses dataset {datasetId}, which is not in workspace {workspaceId} (reports on a semantic model in another workspace are not supported).");

        CheckName(catalog.Name, r.WorkspaceName, "workspaceName", $"workspace {workspaceId}", problems);
        CheckName(report.Name, r.ReportName, "reportName", $"report {reportId}", problems);
        if (datasetName is not null)
            CheckName(datasetName, r.DatasetName, "datasetName", $"dataset {datasetId}", problems);
        if (problems.Count > 0) return ResolveResult.BadRequest(problems);

        r.WorkspaceId = workspaceId.ToString();
        r.WorkspaceName = catalog.Name;
        r.ReportId = reportId.ToString();
        r.ReportName = report.Name;
        r.DatasetId = datasetId.ToString();
        r.DatasetName = datasetName;

        var context = new VisualExportContext
        {
            HttpContext = http,
            Request = r,
            WorkspaceId = workspaceId.Value,
            WorkspaceName = catalog.Name,
            DatasetId = datasetId,
            DatasetName = datasetName!,
            ReportId = reportId.Value,
            ReportName = report.Name
        };

        var decision = await _authorizer.AuthorizeAsync(context, ct);
        return decision.Allowed
            ? ResolveResult.Ok(context)
            : ResolveResult.Forbidden(decision.Reason ?? "You are not allowed to export this visual.");
    }

    private static void CheckName(string expected, string? actual, string field, string what, List<string> problems)
    {
        if (!string.IsNullOrWhiteSpace(actual) && !string.Equals(expected, actual.Trim(), StringComparison.OrdinalIgnoreCase))
            problems.Add($"{field} '{actual}' does not match {what} ('{expected}').");
    }
}

/// <summary>
/// Default <see cref="IVisualExportAuthorizer"/>: the <see cref="PbiVisualExportOptions.AllowedWorkspaceIds"/>
/// list (checked before this runs) is the whole policy, so an empty list denies everything.
/// </summary>
internal sealed class AllowListAuthorizer : IVisualExportAuthorizer
{
    private readonly PbiVisualExportOptions _options;

    public AllowListAuthorizer(IOptions<PbiVisualExportOptions> options) => _options = options.Value;

    public ValueTask<VisualExportAuthorization> AuthorizeAsync(VisualExportContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_options.AllowedWorkspaceIds.Count > 0
            ? VisualExportAuthorization.Allow()
            : VisualExportAuthorization.Deny(
                $"No workspaces are enabled for export: configure {PbiVisualExportOptions.DefaultSectionName}:AllowedWorkspaceIds " +
                "or register an IVisualExportAuthorizer (AddPbiVisualExport(...).AddAuthorizer<T>())."));
}
