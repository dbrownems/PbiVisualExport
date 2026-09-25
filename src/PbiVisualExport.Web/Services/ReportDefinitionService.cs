using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PbiVisualExport.Web.Services;

/// <summary>
/// Downloads a report's definition via Fabric <c>reports/{id}/getDefinition</c> (in the
/// report's own format - no <c>format</c> parameter, since requesting PBIR fails for
/// PBIR-Legacy reports), caches it in <see cref="MetadataCache"/> (kind <c>report</c>)
/// and parses its report-level measures from <c>definition/reportExtensions.json</c>.
///
/// Only loaded when a visual references a field the semantic model doesn't have, so
/// exports that only use model fields never download the report.
/// </summary>
public sealed class ReportDefinitionService
{
    private const string CacheKind = "report";

    private readonly FabricDefinitionClient _fabric;
    private readonly MetadataCache _cache;
    private readonly ILogger<ReportDefinitionService> _log;

    public ReportDefinitionService(FabricDefinitionClient fabric, MetadataCache cache, ILogger<ReportDefinitionService> log)
    {
        _fabric = fabric;
        _cache = cache;
        _log = log;
    }

    /// <param name="maxAge">Oldest acceptable cached copy (null = configured TTL, zero = re-download).</param>
    public Task<ReportDefinition> GetAsync(Guid workspaceId, Guid reportId, TimeSpan? maxAge = null, CancellationToken ct = default)
        => _cache.GetOrLoadAsync(
            CacheKind,
            reportId,
            maxAge,
            () => DownloadAsync(workspaceId, reportId, ct),
            ReportDefinition.Parse,
            ct);

    private async Task<string> DownloadAsync(Guid workspaceId, Guid reportId, CancellationToken ct)
    {
        _log.LogInformation("Downloading definition of report {ReportId} from Fabric getDefinition", reportId);
        var parts = await _fabric.GetDefinitionAsync(workspaceId, $"reports/{reportId}", format: null, ct);
        var snapshot = new ReportDefinitionSnapshot
        {
            Format = ReportDefinition.DetectFormat(parts.Select(p => p.Path)),
            PartPaths = parts.Select(p => p.Path).ToList(),
            // Keep the text parts (report/page/visual JSON, reportExtensions.json, ...); skip
            // StaticResources (themes, images), which report metadata never needs.
            Parts = parts
                .Where(p => !p.Path.StartsWith("StaticResources/", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(p => p.Path, p => p.DecodeText().TrimStart('\uFEFF'), StringComparer.OrdinalIgnoreCase)
        };
        _log.LogInformation("Report {ReportId} definition: format {Format}, {Parts} parts", reportId, snapshot.Format, parts.Count);
        return JsonSerializer.Serialize(snapshot, ReportDefinition.JsonOptions);
    }
}

/// <summary>What <see cref="ReportDefinitionService"/> caches (in memory parsed, on disk as JSON).</summary>
internal sealed class ReportDefinitionSnapshot
{
    public string Format { get; set; } = "";
    public List<string> PartPaths { get; set; } = new();
    public Dictionary<string, string> Parts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A report-level measure from <c>reportExtensions.json</c>.</summary>
/// <param name="Entity">Home table; must exist in the semantic model.</param>
/// <param name="DependsOn">Other report measures its expression references (by name).</param>
public sealed record ReportMeasure(
    string Entity, string Name, string Expression, string? DataType, string? FormatString, IReadOnlyList<string> DependsOn);

/// <summary>A parsed, cached report definition. Shared across requests: read-only.</summary>
public sealed partial class ReportDefinition
{
    public const string Pbir = "PBIR";
    public const string PbirLegacy = "PBIR-Legacy";
    public const string ExtensionsPath = "definition/reportExtensions.json";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Dictionary<string, ReportMeasure> _measuresByName;

    private ReportDefinition(string format, IReadOnlyList<string> partPaths, IReadOnlyDictionary<string, string> parts,
        List<ReportMeasure> measures)
    {
        Format = format;
        PartPaths = partPaths;
        Parts = parts;
        Measures = measures;
        _measuresByName = new Dictionary<string, ReportMeasure>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in measures) _measuresByName.TryAdd(m.Name, m);
    }

    /// <summary><c>PBIR</c> (enhanced report format), <c>PBIR-Legacy</c>, or <c>Unknown</c>.</summary>
    public string Format { get; }
    public bool IsPbir => Format == Pbir;

    /// <summary>Every part path in the definition, including skipped static resources.</summary>
    public IReadOnlyList<string> PartPaths { get; }

    /// <summary>Text parts by path (case-insensitive), excluding <c>StaticResources/</c>.</summary>
    public IReadOnlyDictionary<string, string> Parts { get; }

    public IReadOnlyList<ReportMeasure> Measures { get; }

    /// <summary>Report measure names are unique across the report and the model.</summary>
    public ReportMeasure? FindMeasure(string name) => _measuresByName.GetValueOrDefault(name);

    /// <summary>
    /// <paramref name="used"/> plus every report measure they (transitively) reference,
    /// dependencies first, each once.
    /// </summary>
    public IReadOnlyList<ReportMeasure> WithDependencies(IEnumerable<ReportMeasure> used)
    {
        var ordered = new List<ReportMeasure>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(ReportMeasure m)
        {
            if (!visited.Add(m.Name)) return; // also breaks (invalid) cycles
            foreach (var dep in m.DependsOn)
                if (FindMeasure(dep) is { } d) Visit(d);
            ordered.Add(m);
        }
        foreach (var m in used) Visit(m);
        return ordered;
    }

    /// <summary>PBIR keeps <c>definition/report.json</c>; PBIR-Legacy a single root <c>report.json</c>.</summary>
    internal static string DetectFormat(IEnumerable<string> partPaths)
    {
        var paths = partPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (paths.Contains("definition/report.json")) return Pbir;
        if (paths.Contains("report.json")) return PbirLegacy;
        return "Unknown";
    }

    internal static ReportDefinition Parse(string snapshotJson)
    {
        var s = JsonSerializer.Deserialize<ReportDefinitionSnapshot>(snapshotJson, JsonOptions)
                ?? throw new InvalidOperationException("Empty report definition snapshot.");
        var parts = new Dictionary<string, string>(s.Parts, StringComparer.OrdinalIgnoreCase);
        var measures = s.Format == Pbir && parts.TryGetValue(ExtensionsPath, out var ext)
            ? ParseReportExtensions(ext)
            : new List<ReportMeasure>();
        return new ReportDefinition(s.Format, s.PartPaths, parts, measures);
    }

    /// <summary>
    /// Reads <c>reportExtension/1.0.0</c>: <c>entities[].measures[]</c> with name, expression,
    /// dataType, formatString and <c>references.measures[]</c> (<c>schema</c> empty = model
    /// measure, otherwise the extension name = another report measure).
    /// </summary>
    internal static List<ReportMeasure> ParseReportExtensions(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var raw = new List<(string Entity, string Name, string Expression, string? DataType, string? Format, List<string> Refs)>();
        if (doc.RootElement.TryGetProperty("entities", out var entities) && entities.ValueKind == JsonValueKind.Array)
        {
            foreach (var entity in entities.EnumerateArray())
            {
                var entityName = Str(entity, "name");
                if (entityName is null || !entity.TryGetProperty("measures", out var ms) || ms.ValueKind != JsonValueKind.Array) continue;
                foreach (var m in ms.EnumerateArray())
                {
                    var name = Str(m, "name");
                    var expression = Str(m, "expression");
                    if (string.IsNullOrEmpty(name) || string.IsNullOrWhiteSpace(expression)) continue;
                    var refs = new List<string>();
                    if (m.TryGetProperty("references", out var r) && r.TryGetProperty("measures", out var rms) && rms.ValueKind == JsonValueKind.Array)
                        foreach (var rm in rms.EnumerateArray())
                            if (!string.IsNullOrEmpty(Str(rm, "schema")) && Str(rm, "name") is { } refName)
                                refs.Add(refName);
                    raw.Add((entityName, name, expression, Str(m, "dataType"), Str(m, "formatString"), refs));
                }
            }
        }

        // references is optional metadata, so also scan the expression for [Name] tokens that
        // are report measures. A false positive only defines an unused query measure.
        var names = raw.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return raw.Select(x =>
        {
            var deps = x.Refs
                .Concat(BracketToken().Matches(x.Expression).Select(t => t.Groups[1].Value.Replace("]]", "]")))
                .Where(n => names.Contains(n) && !string.Equals(n, x.Name, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new ReportMeasure(x.Entity, x.Name, x.Expression, x.DataType, x.Format, deps);
        }).ToList();
    }

    private static string? Str(JsonElement e, string property)
        => e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [GeneratedRegex(@"\[((?:[^\]]|\]\])+)\]")]
    private static partial Regex BracketToken();
}
