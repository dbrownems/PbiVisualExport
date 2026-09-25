using Microsoft.PowerBI.Api;
using Microsoft.PowerBI.Api.Models;
using Microsoft.Rest;

namespace PbiVisualExport.SampleHost.Services;

public sealed record SampleReport(Guid Id, string Name, Guid DatasetId, string? DatasetName, Guid WorkspaceId, string WorkspaceName);

public sealed record SampleEmbedInfo(
    Guid ReportId, string ReportName, string EmbedUrl,
    Guid DatasetId, string? DatasetName,
    Guid WorkspaceId, string WorkspaceName,
    string AccessToken, DateTime TokenExpiry);

/// <summary>
/// The host's side of embedding: lists a workspace's reports and generates embed tokens. It
/// reuses the module's <see cref="PbiTokenProvider"/> so both run as one identity (your
/// <c>az login</c> user in development, a service principal when deployed).
/// </summary>
public sealed class PbiEmbedService
{
    private static readonly Uri PowerBiApiUrl = new("https://api.powerbi.com");
    private readonly PbiTokenProvider _tokens;

    public PbiEmbedService(PbiTokenProvider tokens) => _tokens = tokens;

    private async Task<PowerBIClient> GetClientAsync(CancellationToken ct)
    {
        var accessToken = await _tokens.GetPowerBiTokenAsync(ct);
        return new PowerBIClient(PowerBiApiUrl, new TokenCredentials(accessToken, "Bearer"));
    }

    /// <summary>The workspace's Power BI (non-paginated) reports, with dataset names.</summary>
    public async Task<IReadOnlyList<SampleReport>> ListReportsAsync(Guid workspaceId, CancellationToken ct = default)
    {
        using var client = await GetClientAsync(ct);
        var workspaceName = await GetWorkspaceNameAsync(client, workspaceId, ct);
        var datasets = (await client.Datasets.GetDatasetsInGroupAsync(workspaceId, ct)).Value
            .ToDictionary(d => Guid.Parse(d.Id), d => d.Name);
        var reports = (await client.Reports.GetReportsInGroupAsync(workspaceId, cancellationToken: ct)).Value;
        return reports
            .Where(r => !string.Equals(r.ReportType, "PaginatedReport", StringComparison.OrdinalIgnoreCase)
                        && Guid.TryParse(r.DatasetId, out _))
            .Select(r =>
            {
                var dsId = Guid.Parse(r.DatasetId);
                return new SampleReport(r.Id, r.Name, dsId, datasets.GetValueOrDefault(dsId), workspaceId, workspaceName);
            })
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<SampleEmbedInfo> GetEmbedInfoAsync(Guid workspaceId, Guid reportId, CancellationToken ct = default)
    {
        using var client = await GetClientAsync(ct);
        var report = await client.Reports.GetReportInGroupAsync(workspaceId, reportId, ct);
        var datasetId = Guid.Parse(report.DatasetId);

        var tokenRequest = new GenerateTokenRequestV2(
            reports: new List<GenerateTokenRequestV2Report> { new(reportId) },
            datasets: new List<GenerateTokenRequestV2Dataset> { new(datasetId.ToString()) },
            targetWorkspaces: new List<GenerateTokenRequestV2TargetWorkspace> { new(workspaceId) });
        var embedToken = await client.EmbedToken.GenerateTokenAsync(tokenRequest, ct);

        string? datasetName = null;
        try
        {
            datasetName = (await client.Datasets.GetDatasetInGroupAsync(workspaceId, datasetId.ToString(), ct)).Name;
        }
        catch (HttpOperationException)
        {
            // The dataset may live in another workspace; the name is informational only.
        }

        return new SampleEmbedInfo(report.Id, report.Name, report.EmbedUrl, datasetId, datasetName,
            workspaceId, await GetWorkspaceNameAsync(client, workspaceId, ct), embedToken.Token, embedToken.Expiration);
    }

    private static async Task<string> GetWorkspaceNameAsync(PowerBIClient client, Guid workspaceId, CancellationToken ct)
    {
        var groups = await client.Groups.GetGroupsAsync(filter: $"id eq '{workspaceId}'", cancellationToken: ct);
        return groups.Value.FirstOrDefault()?.Name
               ?? throw new InvalidOperationException($"Workspace {workspaceId} was not found or is not accessible.");
    }
}
