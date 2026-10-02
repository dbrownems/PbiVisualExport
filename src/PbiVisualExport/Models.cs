using System.Text.Json;
using System.Text.Json.Serialization;

namespace PbiVisualExport;

/// <summary>One field (column, hierarchy level or measure) placed in a visual.</summary>
public sealed class ExportField
{
    public string Table { get; set; } = "";
    public string? Column { get; set; }
    public string? Measure { get; set; }

    /// <summary>
    /// With <see cref="HierarchyLevel"/>: a level of a hierarchy placed on the visual (a user
    /// hierarchy, or a date column's auto date/time hierarchy named after the column). The
    /// server exports the level's column, headed by the level name, as native export does.
    /// </summary>
    public string? Hierarchy { get; set; }

    /// <summary>The level of <see cref="Hierarchy"/> (e.g. <c>Year</c>).</summary>
    public string? HierarchyLevel { get; set; }

    /// <summary>
    /// Implicit aggregation on a column (or hierarchy level) field, named as the Power BI SDK's
    /// <c>getDataFields</c> names it: Sum, Avg, Min, Max, Median, StandardDeviation, Variance,
    /// <c>CountNonNull</c> ("Count") or <c>Count</c> ("Count (Distinct)").
    /// </summary>
    public string? Aggregation { get; set; }

    /// <summary>Visual data role the field is bound to (e.g. Category, Values, Rows).</summary>
    public string? Role { get; set; }

    /// <summary>
    /// Optional server-side column header override. Set today for a resolved hierarchy
    /// level, which is headed by the level name. (A field parameter's label isn't exposed
    /// by the SDK, so such columns keep the column name: a known gap.)
    /// </summary>
    public string? Header { get; set; }

    /// <summary>
    /// Hidden on the visual ("Hide fields on the visual" / <c>visual.setFieldHidden</c>;
    /// reported by <c>getDataFields</c> as <c>hidden: true</c>). Only measures and
    /// aggregations can be hidden this way. The field is still computed, because it
    /// affects which rows the visual shows, but it is not written to the CSV, as in native
    /// summarized export. Model-hidden (TOM IsHidden) fields are unrelated: the visual
    /// displays them, so they are exported.
    /// </summary>
    public bool Hidden { get; set; }

    [JsonIgnore] public bool IsMeasure => !string.IsNullOrEmpty(Measure) || !string.IsNullOrEmpty(Aggregation);
    [JsonIgnore] internal bool IsHierarchyLevel => !string.IsNullOrEmpty(Hierarchy) && !string.IsNullOrEmpty(HierarchyLevel);
    [JsonIgnore] public string DisplayName =>
        !string.IsNullOrEmpty(Header) ? Header! :
        !string.IsNullOrEmpty(Measure) ? Measure! :
        !string.IsNullOrEmpty(Aggregation) ? Services.ImplicitAggregations.Header(Aggregation!, IsHierarchyLevel ? HierarchyLevel : Column) :
        IsHierarchyLevel ? HierarchyLevel! :
        Column ?? Table;
}

/// <summary>
/// One sort key of the visual (the bookmark's <c>singleVisual.orderBy</c>, which holds the
/// live sort or, if the user hasn't changed it, the report's saved default sort).
/// Identifies a column, a hierarchy level, a measure, or an implicit aggregation of a column.
/// </summary>
public sealed class ExportSort
{
    public string Table { get; set; } = "";
    public string? Column { get; set; }
    public string? Measure { get; set; }
    public string? Aggregation { get; set; }

    /// <summary>With <see cref="HierarchyLevel"/>: a hierarchy level (see <see cref="ExportField.Hierarchy"/>).</summary>
    public string? Hierarchy { get; set; }
    public string? HierarchyLevel { get; set; }

    /// <summary>"Ascending" or "Descending".</summary>
    public string? Direction { get; set; }

    [JsonIgnore] public bool Descending => string.Equals(Direction, "Descending", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] internal bool IsHierarchyLevel => !string.IsNullOrEmpty(Hierarchy) && !string.IsNullOrEmpty(HierarchyLevel);
    [JsonIgnore] public string Label =>
        (!string.IsNullOrEmpty(Measure) ? Measure! :
         !string.IsNullOrEmpty(Aggregation) ? $"{Aggregation}({Column})" :
         IsHierarchyLevel ? $"{Hierarchy}.{HierarchyLevel}" : Column ?? Table) +
        (Descending ? " DESC" : " ASC");
}

/// <summary>
/// Payload POSTed by the browser client when the user exports a visual (contract version 1).
/// <see cref="WorkspaceId"/> and <see cref="ReportId"/> are required. Metadata (TOM via Fabric
/// getDefinition) is addressed by ID, while the XMLA endpoint that streams the data only
/// accepts names, so the server resolves the dataset and all names from the workspace
/// catalog. Optional IDs / names sent by the client are checked against it (a mismatch is
/// rejected rather than risk reading metadata and data from different models).
/// </summary>
public sealed class VisualExportRequest
{
    public string? WorkspaceId { get; set; }
    public string? WorkspaceName { get; set; }
    public string? DatasetId { get; set; }
    public string? DatasetName { get; set; }
    public string? ReportId { get; set; }
    public string? ReportName { get; set; }

    /// <summary>The report page (its <c>name</c>, not display name) the visual is on.</summary>
    public string? PageName { get; set; }

    /// <summary>The visual's <c>name</c> (container id).</summary>
    public string? VisualName { get; set; }

    /// <summary>The visual's title, used for the file name.</summary>
    public string? VisualTitle { get; set; }

    public List<ExportField> Fields { get; set; } = new();

    /// <summary>
    /// Raw Power BI IFilter objects gathered from the visual, page, report and
    /// any slicers on the page. Parsed server-side into DAX filter predicates.
    /// </summary>
    public List<JsonElement> Filters { get; set; } = new();

    /// <summary>
    /// true (default): apply each column's / measure's model format string via DAX
    /// FORMAT() so values look like the report. false: export raw typed values
    /// (invariant-culture numbers with full precision, ISO-8601 datetimes, true/false).
    /// </summary>
    public bool ApplyFormatStrings { get; set; } = true;

    /// <summary>
    /// The visual's explicit sort, in priority order. Empty = the visual has no explicit
    /// sort (Power BI then uses an implicit order, approximated as the visual's group-by
    /// columns ascending).
    /// </summary>
    public List<ExportSort> Sort { get; set; } = new();

    /// <summary>
    /// true (default): ORDER BY the visual's sort so rows match the report. false: no
    /// ORDER BY - rows stream in engine order, avoiding the memory cost of sorting the
    /// whole result on the capacity (useful for multi-million-row exports).
    /// </summary>
    public bool ApplySort { get; set; } = true;

    /// <summary>
    /// The browser's IANA time zone (e.g. <c>America/Chicago</c>). The Power BI JS SDK sends
    /// date filter values as UTC instants of the browser's local wall time, which the report
    /// filters on; the server converts them back with this zone. Optional (additive): without
    /// it, such values are read as UTC.
    /// </summary>
    public string? TimeZone { get; set; }

    /// <summary>A shallow copy with <paramref name="sort"/> as its sort.</summary>
    internal VisualExportRequest WithSort(List<ExportSort> sort)
    {
        var copy = (VisualExportRequest)MemberwiseClone();
        copy.Sort = sort;
        return copy;
    }

    /// <summary>A shallow copy with <paramref name="fields"/> and <paramref name="sort"/>.</summary>
    internal VisualExportRequest WithFieldsAndSort(List<ExportField> fields, List<ExportSort> sort)
    {
        var copy = WithSort(sort);
        copy.Fields = fields;
        return copy;
    }
}

/// <summary>
/// Result of <c>POST {prefix}/bench</c>: the export's query read end to end without
/// writing CSV. GC figures are process-wide deltas.
/// </summary>
public sealed record ReadBenchmarkResult(
    long Rows, int Columns, long ValueChars,
    double ConnectSeconds, double FirstDataSeconds, double TotalSeconds,
    bool Formatted, string Sort,
    long AllocatedBytes, int Gen0Collections, int Gen1Collections, int Gen2Collections, long HeapSizeBytes);
