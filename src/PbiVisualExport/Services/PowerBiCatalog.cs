using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using PbiVisualExport.Auth;

namespace PbiVisualExport.Services;

internal sealed record CatalogReport(Guid Id, string Name, Guid DatasetId);

/// <summary>Names of a workspace and its datasets/reports, keyed by ID.</summary>
internal sealed record WorkspaceCatalog(
    Guid Id,
    string Name,
    IReadOnlyDictionary<Guid, string> Datasets,
    IReadOnlyDictionary<Guid, CatalogReport> Reports);

/// <summary>Looks up workspace, dataset and report names by ID.</summary>
internal interface IPowerBiCatalog
{
    Task<WorkspaceCatalog> GetAsync(Guid workspaceId, bool forceRefresh = false, CancellationToken ct = default);

    /// <summary>Returns the catalog, refreshing once if any of the given IDs is unknown.</summary>
    Task<WorkspaceCatalog> GetContainingAsync(Guid workspaceId, Guid? datasetId, Guid? reportId, CancellationToken ct = default);
}

/// <summary>
/// Cached ID -> name lookups for a workspace's datasets and reports (Power BI REST).
/// XMLA addresses workspaces and models by <b>name</b> while the REST/Fabric APIs use
/// <b>IDs</b>, so export requests are resolved against this catalog.
/// A lookup miss (e.g. a newly published report) forces one refresh.
/// </summary>
internal sealed class PowerBiCatalog : IPowerBiCatalog
{
    private const string PowerBiApi = "https://api.powerbi.com/v1.0/myorg";
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    private readonly PbiTokenProvider _tokens;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IMemoryCache _cache;

    public PowerBiCatalog(PbiTokenProvider tokens, IHttpClientFactory httpFactory, IMemoryCache cache)
    {
        _tokens = tokens;
        _httpFactory = httpFactory;
        _cache = cache;
    }

    public async Task<WorkspaceCatalog> GetAsync(Guid workspaceId, bool forceRefresh = false, CancellationToken ct = default)
    {
        var key = "PbiVisualExport:catalog:" + workspaceId;
        if (!forceRefresh && _cache.TryGetValue(key, out WorkspaceCatalog? cached) && cached is not null)
            return cached;

        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await _tokens.GetPowerBiTokenAsync(ct));

        using var groups = await GetJsonAsync(http, $"{PowerBiApi}/groups?$filter={Uri.EscapeDataString($"id eq '{workspaceId}'")}", ct);
        var group = groups.RootElement.GetProperty("value").EnumerateArray().FirstOrDefault();
        if (group.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Workspace {workspaceId} was not found or is not accessible.");

        using var datasets = await GetJsonAsync(http, $"{PowerBiApi}/groups/{workspaceId}/datasets", ct);
        using var reports = await GetJsonAsync(http, $"{PowerBiApi}/groups/{workspaceId}/reports", ct);

        var catalog = new WorkspaceCatalog(
            workspaceId,
            group.GetProperty("name").GetString() ?? "",
            datasets.RootElement.GetProperty("value").EnumerateArray()
                .ToDictionary(d => Guid.Parse(d.GetProperty("id").GetString()!), d => d.GetProperty("name").GetString() ?? ""),
            reports.RootElement.GetProperty("value").EnumerateArray()
                .Where(r => !string.Equals(Str(r, "reportType"), "PaginatedReport", StringComparison.OrdinalIgnoreCase)
                            && Guid.TryParse(Str(r, "datasetId"), out _))
                .Select(r => new CatalogReport(
                    Guid.Parse(Str(r, "id")!), Str(r, "name") ?? "", Guid.Parse(Str(r, "datasetId")!)))
                .ToDictionary(r => r.Id));

        _cache.Set(key, catalog, Ttl);
        return catalog;
    }

    public async Task<WorkspaceCatalog> GetContainingAsync(Guid workspaceId, Guid? datasetId, Guid? reportId, CancellationToken ct = default)
    {
        var catalog = await GetAsync(workspaceId, false, ct);
        var missing = (datasetId is { } d && !catalog.Datasets.ContainsKey(d)) ||
                      (reportId is { } r && !catalog.Reports.ContainsKey(r));
        return missing ? await GetAsync(workspaceId, true, ct) : catalog;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static async Task<JsonDocument> GetJsonAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"GET {url} returned {(int)resp.StatusCode}: {body}", null, resp.StatusCode);
        return JsonDocument.Parse(body);
    }
}
