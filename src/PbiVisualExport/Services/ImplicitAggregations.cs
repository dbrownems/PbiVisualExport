namespace PbiVisualExport.Services;

/// <summary>
/// Implicit aggregations of a column (a "Sum of Amount" field), named as the Power BI JS SDK
/// names them in <c>getDataFields</c> and filter targets: the semantic query's
/// <c>QueryAggregateFunction</c> names. Note that <c>Count</c> is "Count (Distinct)" and
/// <c>CountNonNull</c> is "Count". DAX and header labels are what Power BI's own export
/// produces (verified against native <c>exportData</c>).
/// </summary>
internal static class ImplicitAggregations
{
    /// <summary>The PBIR <c>QueryAggregateFunction</c> names, by code.</summary>
    internal static readonly string[] ByCode =
        { "Sum", "Avg", "Count", "Min", "Max", "CountNonNull", "Median", "StandardDeviation", "Variance" };

    /// <summary>
    /// A case-insensitive key for comparing aggregation names: <c>sum</c>, <c>avg</c>,
    /// <c>distinctcount</c>, <c>min</c>, <c>max</c>, <c>countnonnull</c>, <c>median</c>,
    /// <c>standarddeviation</c> or <c>variance</c>; any other name lower-cased.
    /// </summary>
    internal static string Normalize(string? name) => (name ?? "").Trim().ToLowerInvariant() switch
    {
        "average" => "avg",
        "count" or "countdistinct" => "distinctcount",
        "stdev" or "stddev" => "standarddeviation",
        "var" => "variance",
        var a => a
    };

    /// <summary>Counts are whole numbers, whatever the counted column's format string.</summary>
    internal static bool IsCount(string? name) => Normalize(name) is "countnonnull" or "distinctcount";

    /// <summary>
    /// DAX for the aggregation of <paramref name="columnRef"/>; null for a name Power BI doesn't
    /// offer. Count is COUNTA (non-blank values of any type, including Boolean, which COUNT
    /// rejects); Count (Distinct) counts BLANK as a value; Standard deviation and Variance are
    /// the population forms.
    /// </summary>
    internal static string? Dax(string? name, string columnRef) => Normalize(name) switch
    {
        "sum" => $"SUM({columnRef})",
        "avg" => $"AVERAGE({columnRef})",
        "distinctcount" => $"DISTINCTCOUNT({columnRef})",
        "min" => $"MIN({columnRef})",
        "max" => $"MAX({columnRef})",
        "countnonnull" => $"COUNTA({columnRef})",
        "median" => $"MEDIAN({columnRef})",
        "standarddeviation" => $"STDEV.P({columnRef})",
        "variance" => $"VAR.P({columnRef})",
        _ => null
    };

    /// <summary>The header Power BI gives the field, e.g. "Average of Amount".</summary>
    internal static string Header(string name, string? of)
    {
        var label = Normalize(name) switch
        {
            "sum" => "Sum",
            "avg" => "Average",
            "distinctcount" or "countnonnull" => "Count",
            "min" => "Min",
            "max" => "Max",
            "median" => "Median",
            "standarddeviation" => "Standard deviation",
            "variance" => "Variance",
            _ => name
        };
        return $"{label} of {of}";
    }
}
