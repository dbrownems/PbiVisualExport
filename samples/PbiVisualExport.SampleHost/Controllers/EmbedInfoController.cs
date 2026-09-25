using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PbiVisualExport.SampleHost.Services;

namespace PbiVisualExport.SampleHost.Controllers;

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
        var workspaceId = Guid.Parse(_pbi.WorkspaceId); // validated on start
        try
        {
            var reports = await _embed.ListReportsAsync(workspaceId, ct);
            return Ok(reports.Select(r => new
            {
                id = r.Id,
                name = r.Name,
                datasetId = r.DatasetId,
                datasetName = r.DatasetName,
                workspaceId = r.WorkspaceId,
                workspaceName = r.WorkspaceName,
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
        var workspaceId = Guid.Parse(_pbi.WorkspaceId);
        var requestedReportId = string.IsNullOrWhiteSpace(reportId) ? _pbi.ReportId : reportId;
        if (!Guid.TryParse(requestedReportId, out var rid))
            return BadRequest("A valid reportId is required (PowerBI:ReportId or ?reportId=).");

        try
        {
            var e = await _embed.GetEmbedInfoAsync(workspaceId, rid, ct);
            return Ok(new
            {
                type = "Report",
                reportId = e.ReportId,
                reportName = e.ReportName,
                embedUrl = e.EmbedUrl,
                datasetId = e.DatasetId,
                datasetName = e.DatasetName,
                workspaceId = e.WorkspaceId,
                workspaceName = e.WorkspaceName,
                accessToken = e.AccessToken,
                tokenExpiry = e.TokenExpiry
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to generate embed info");
            return StatusCode(500, ex.Message);
        }
    }
}
