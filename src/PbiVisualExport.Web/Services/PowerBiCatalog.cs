using Microsoft.Extensions.Caching.Memory;
using Microsoft.PowerBI.Api;
using Microsoft.Rest;
using PbiVisualExport.Web.Auth;

namespace PbiVisualExport.Web.Services;

public sealed record CatalogReport(Guid Id, string Name, Guid DatasetId, string? EmbedUrl);

/// <summary>Names of a workspace and its datasets/reports, keyed by ID.</summary>
public sealed record WorkspaceCatalog(
    Guid Id,
    string Name,
    IReadOnlyDictionary<Guid, string> Datasets,
    IReadOnlyDictionary<Guid, CatalogReport> Reports);

/// <summary>
/// Cached ID -> name lookups for a workspace's datasets and reports (Power BI REST).
/// XMLA addresses workspaces and models by <b>name</b> while the REST/Fabric APIs use
/// <b>IDs</b>, so export requests carry both and are validated against this catalog.
/// A lookup miss (e.g. a newly published report) forces one refresh.
/// </summary>
public sealed class PowerBiCatalog
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    private readonly PbiTokenProvider _tokens;
    private readonly IMemoryCache _cache;

    public PowerBiCatalog(PbiTokenProvider tokens, IMemoryCache cache)
    {
        _tokens = tokens;
        _cache = cache;
    }

    public async Task<WorkspaceCatalog> GetAsync(Guid workspaceId, bool forceRefresh = false, CancellationToken ct = default)
    {
        var key = "catalog:" + workspaceId;
        if (!forceRefresh && _cache.TryGetValue(key, out WorkspaceCatalog? cached) && cached is not null)
            return cached;

        using var client = new PowerBIClient(new Uri("https://api.powerbi.com"),
            new TokenCredentials(await _tokens.GetPowerBiTokenAsync(ct), "Bearer"));

        var groups = await client.Groups.GetGroupsAsync(filter: $"id eq '{workspaceId}'", cancellationToken: ct);
        var group = groups.Value.FirstOrDefault()
            ?? throw new InvalidOperationException($"Workspace {workspaceId} was not found or is not accessible.");
        var datasets = await client.Datasets.GetDatasetsInGroupAsync(workspaceId, ct);
        var reports = await client.Reports.GetReportsInGroupAsync(workspaceId, ct);

        var catalog = new WorkspaceCatalog(
            workspaceId,
            group.Name,
            datasets.Value.ToDictionary(d => Guid.Parse(d.Id), d => d.Name),
            reports.Value
                .Where(r => !string.Equals(r.ReportType, "PaginatedReport", StringComparison.OrdinalIgnoreCase)
                            && Guid.TryParse(r.DatasetId, out _))
                .ToDictionary(r => r.Id, r => new CatalogReport(r.Id, r.Name, Guid.Parse(r.DatasetId), r.EmbedUrl)));

        _cache.Set(key, catalog, Ttl);
        return catalog;
    }

    /// <summary>Returns the catalog, refreshing once if any of the given IDs is unknown.</summary>
    public async Task<WorkspaceCatalog> GetContainingAsync(Guid workspaceId, Guid? datasetId, Guid? reportId, CancellationToken ct = default)
    {
        var catalog = await GetAsync(workspaceId, false, ct);
        var missing = (datasetId is { } d && !catalog.Datasets.ContainsKey(d)) ||
                      (reportId is { } r && !catalog.Reports.ContainsKey(r));
        return missing ? await GetAsync(workspaceId, true, ct) : catalog;
    }
}
