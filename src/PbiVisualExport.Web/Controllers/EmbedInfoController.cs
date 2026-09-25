using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PbiVisualExport.Web.Options;
using PbiVisualExport.Web.Services;

namespace PbiVisualExport.Web.Controllers;

[ApiController]
[Route("embedinfo")]
public sealed class EmbedInfoController : ControllerBase
{
    private readonly PbiEmbedService _embed;
    private readonly PowerBIOptions _pbi;
    private readonly ILogger<EmbedInfoController> _log;

    public EmbedInfoController(PbiEmbedService embed, IOptions<PowerBIOptions> pbi, ILogger<EmbedInfoController> log)
    {
        _embed = embed;
        _pbi = pbi.Value;
        _log = log;
    }

    [HttpGet("reports")]
    public async Task<IActionResult> GetReports(CancellationToken ct)
    {
        if (!Guid.TryParse(_pbi.WorkspaceId, out var workspaceId))
            return BadRequest("PowerBI:WorkspaceId must be configured.");

        try
        {
            // The picker is the user's "refresh" point, so reload the catalog (names can change).
            var catalog = await _embed.GetCatalogAsync(workspaceId, ct);
            return Ok(catalog.Reports.Values
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(r => new
                {
                    id = r.Id,
                    name = r.Name,
                    datasetId = r.DatasetId,
                    datasetName = catalog.Datasets.TryGetValue(r.DatasetId, out var dn) ? dn : null,
                    workspaceId = catalog.Id,
                    workspaceName = catalog.Name,
                    isDefault = string.Equals(r.Id.ToString(), _pbi.ReportId, StringComparison.OrdinalIgnoreCase)
                }));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to list reports");
            return StatusCode(500, ex.Message);
        }
    }

    [HttpGet("getembedinfo")]
    public async Task<IActionResult> GetEmbedInfo([FromQuery] string? reportId, CancellationToken ct)
    {
        if (!Guid.TryParse(_pbi.WorkspaceId, out var workspaceId))
            return BadRequest("PowerBI:WorkspaceId must be configured.");

        var requestedReportId = string.IsNullOrWhiteSpace(reportId) ? _pbi.ReportId : reportId;
        if (!Guid.TryParse(requestedReportId, out var rid))
            return BadRequest("A valid reportId is required (PowerBI:ReportId or ?reportId=).");

        try
        {
            var embedParams = await _embed.GetEmbedParamsAsync(workspaceId, rid, ct);
            return Ok(new
            {
                type = embedParams.Type,
                reportId = embedParams.EmbedReport[0].ReportId,
                reportName = embedParams.EmbedReport[0].ReportName,
                embedUrl = embedParams.EmbedReport[0].EmbedUrl,
                datasetId = embedParams.DatasetId,
                datasetName = embedParams.DatasetName,
                workspaceId = embedParams.WorkspaceId,
                workspaceName = embedParams.WorkspaceName,
                accessToken = embedParams.EmbedToken!.Token,
                tokenExpiry = embedParams.EmbedToken!.Expiration
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to generate embed info");
            return StatusCode(500, ex.Message);
        }
    }
}
