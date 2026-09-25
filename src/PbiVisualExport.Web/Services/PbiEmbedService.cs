using Microsoft.PowerBI.Api;
using Microsoft.PowerBI.Api.Models;
using Microsoft.Rest;
using PbiVisualExport.Web.Auth;
using PbiVisualExport.Web.Models;

namespace PbiVisualExport.Web.Services;

/// <summary>
/// Generates embed URL + embed token for a report, using a token minted by
/// <see cref="PbiTokenProvider"/> (delegated user in dev, service principal in prod).
/// </summary>
public sealed class PbiEmbedService
{
    private const string PowerBiApiUrl = "https://api.powerbi.com";
    private readonly PbiTokenProvider _tokens;
    private readonly PowerBiCatalog _catalog;

    public PbiEmbedService(PbiTokenProvider tokens, PowerBiCatalog catalog)
    {
        _tokens = tokens;
        _catalog = catalog;
    }

    public async Task<PowerBIClient> GetClientAsync(CancellationToken ct = default)
    {
        var accessToken = await _tokens.GetPowerBiTokenAsync(ct);
        var credentials = new TokenCredentials(accessToken, "Bearer");
        return new PowerBIClient(new Uri(PowerBiApiUrl), credentials);
    }

    /// <summary>Lists the Power BI (non-paginated) reports in a workspace, with dataset names.</summary>
    public async Task<WorkspaceCatalog> GetCatalogAsync(Guid workspaceId, CancellationToken ct = default)
        => await _catalog.GetAsync(workspaceId, forceRefresh: true, ct);

    public async Task<EmbedParams> GetEmbedParamsAsync(Guid workspaceId, Guid reportId, CancellationToken ct = default)
    {
        using var client = await GetClientAsync(ct);

        var report = await client.Reports.GetReportInGroupAsync(workspaceId, reportId, ct);
        var datasetId = Guid.Parse(report.DatasetId);

        var tokenRequest = new GenerateTokenRequestV2(
            reports: new List<GenerateTokenRequestV2Report> { new(reportId) },
            datasets: new List<GenerateTokenRequestV2Dataset> { new(datasetId.ToString()) },
            targetWorkspaces: new List<GenerateTokenRequestV2TargetWorkspace> { new(workspaceId) });

        var embedToken = await client.EmbedToken.GenerateTokenAsync(tokenRequest, ct);
        var catalog = await _catalog.GetContainingAsync(workspaceId, datasetId, reportId, ct);

        return new EmbedParams
        {
            Type = "Report",
            WorkspaceId = workspaceId,
            WorkspaceName = catalog.Name,
            DatasetId = datasetId,
            DatasetName = catalog.Datasets.TryGetValue(datasetId, out var dsName) ? dsName : "",
            EmbedToken = embedToken,
            EmbedReport = new List<EmbedReport>
            {
                new() { ReportId = report.Id, ReportName = report.Name, EmbedUrl = report.EmbedUrl }
            }
        };
    }
}
