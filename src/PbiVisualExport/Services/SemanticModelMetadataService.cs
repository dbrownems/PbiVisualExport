using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiVisualExport.Services;

/// <summary>
/// Downloads a semantic model's TMSL definition via the Fabric getDefinition API
/// and loads it into an in-memory TOM model. This is used (instead of a live XMLA
/// connection) to read column data types, format strings, sort-by columns, measures
/// and M query parameters.
///
/// The download is an expensive long-running Fabric operation but the metadata
/// rarely changes, so results are served from <see cref="MetadataCache"/>: repeat
/// export requests reuse the parsed model in-process and across restarts instead
/// of re-hitting Fabric. This class owns only the download; the cache owns storage.
/// </summary>
internal sealed class SemanticModelMetadataService
{
    private const string CacheKind = "tmsl";

    private readonly FabricDefinitionClient _fabric;
    private readonly MetadataCache _cache;
    private readonly ILogger<SemanticModelMetadataService> _log;

    public SemanticModelMetadataService(
        FabricDefinitionClient fabric,
        MetadataCache cache,
        ILogger<SemanticModelMetadataService> log)
    {
        _fabric = fabric;
        _cache = cache;
        _log = log;
    }

    /// <summary>
    /// Returns the TOM model for a dataset, served from cache when fresh.
    /// <paramref name="maxAge"/>: oldest acceptable cached copy (null = configured TTL,
    /// <see cref="TimeSpan.Zero"/> = re-download).
    /// </summary>
    public Task<TOM.Model> GetModelAsync(Guid workspaceId, Guid datasetId, TimeSpan? maxAge = null, CancellationToken ct = default)
        => _cache.GetOrLoadAsync(
            CacheKind,
            datasetId,
            maxAge,
            () => DownloadTmslAsync(workspaceId, datasetId, ct),
            Parse,
            ct);

    private async Task<string> DownloadTmslAsync(Guid workspaceId, Guid datasetId, CancellationToken ct)
    {
        _log.LogInformation("Downloading TMSL for dataset {DatasetId} from Fabric getDefinition", datasetId);
        var parts = await _fabric.GetDefinitionAsync(workspaceId, $"semanticModels/{datasetId}", "TMSL", ct);
        var bim = parts.FirstOrDefault(p => string.Equals(p.Path, "model.bim", StringComparison.OrdinalIgnoreCase))
                  ?? throw new InvalidOperationException("getDefinition response did not contain a model.bim (TMSL) part.");
        return bim.DecodeText();
    }

    // Fabric/Power BI models must be read in PowerBI compatibility mode; the default
    // (AnalysisServices) rejects Power BI-only properties such as a field parameter's
    // relatedColumnDetails with "Unrecognized JSON property".
    private static TOM.Model Parse(string tmsl)
        => TOM.JsonSerializer.DeserializeDatabase(
            tmsl, new TOM.DeserializeOptions(), Microsoft.AnalysisServices.CompatibilityMode.PowerBI).Model;
}
