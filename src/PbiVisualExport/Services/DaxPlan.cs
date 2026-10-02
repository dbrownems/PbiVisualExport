using System.Text;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiVisualExport.Services;

internal sealed record DaxOutputColumn(string Alias, string Header, bool IsMeasure);

/// <summary>A visual column in display order: a dimension or a measure, by index.</summary>
internal sealed record OutputItem(bool IsMeasure, int Index);

/// <summary>A raw (unformatted) sort expression, evaluated per row of the summarized table.</summary>
internal sealed record SortKey(string Expression, bool Descending);

/// <summary>A query-scoped measure (<c>DEFINE MEASURE 'Table'[Name] = expression</c>).</summary>
internal sealed record QueryMeasure(string Name, string Reference, string Expression);

/// <summary>A Top N filter: the first <see cref="Count"/> values of a column ranked by a measure (ties included).</summary>
internal sealed record TopNFilter(string ColumnRef, long Count, bool Bottom, string OrderExpression);

/// <summary>
/// A dynamic M query parameter set from the filters on its bound column
/// (<c>DEFINE MPARAMETER 'Name' = value</c>), as Power BI does: a scalar, or a list
/// when the binding allows multiple values. <see cref="Literals"/> are DAX literals.
/// <see cref="SelectAll"/>: a slicer's "Select all", passing the binding's select-all value
/// (from the model, not a value of the bound column).
/// </summary>
internal sealed record MParameterValue(string Name, string ColumnRef, IReadOnlyList<string> Literals, bool MultiSelect, bool SelectAll = false)
{
    public string Value => MultiSelect ? "{" + string.Join(", ", Literals) + "}" : Literals[0];

    public override string ToString() => $"{Name} = {Value}";
}

/// <summary>
/// Translated filters, applied in Power BI's order: column filters; then Top N filters, each
/// ranked under the column filters only; then one value filter holding every measure
/// condition, evaluated at the visual's grain under all of the above.
/// </summary>
internal sealed class FilterSet
{
    /// <summary>Filter tables on columns (slicers, basic/advanced column filters).</summary>
    public List<string> Columns { get; } = new();

    public List<TopNFilter> TopN { get; } = new();

    /// <summary>Filter-only measures (not in the visual), computed in the value filter as [vf_i] under IGNORE().</summary>
    public List<string> ValueMeasures { get; } = new();

    /// <summary>Predicates over the value filter's [m_i]/[vf_i] columns, ANDed together.</summary>
    public List<string> ValuePredicates { get; } = new();

    /// <summary>Measures referenced by value/Top N filters, for the plan summary.</summary>
    public List<string> Descriptions { get; } = new();

    /// <summary>Dynamic M parameters bound by filters on their bound columns (the column filters are kept too).</summary>
    public List<MParameterValue> Parameters { get; } = new();
}

/// <summary>
/// A visual field or sort key the semantic model doesn't have. <see cref="MeasureName"/> is
/// set for measure references, which may be report-level measures.
/// </summary>
internal sealed record UnresolvedField(string Label, string? MeasureName, bool SortOnly);

/// <summary>
/// A single DAX query reproducing a Power BI visual's data. The whole result is
/// streamed over XMLA (ADOMD.NET), so no paging is needed:
/// <c>SUMMARIZECOLUMNS(group-by columns, filters, measures)</c> projected through
/// <c>SELECTCOLUMNS</c> with positional aliases [c0], [c1], ... in visual order, plus
/// raw sort-key columns [s0], [s1], ... when a formatted column can't be sorted
/// directly, and an <c>ORDER BY</c> reproducing the visual's sort.
/// </summary>
internal sealed class DaxPlan
{
    private readonly IReadOnlyList<DimField> _dims;
    private readonly IReadOnlyList<string> _extraGroupBy;
    private readonly IReadOnlyList<MeasureField> _measures;
    private readonly FilterSet _filters;
    private readonly IReadOnlyList<OutputItem> _items;
    private readonly IReadOnlyList<SortKey> _sortKeys;
    private readonly IReadOnlyList<QueryMeasure> _queryMeasures;

    internal DaxPlan(IReadOnlyList<DimField> dims, IReadOnlyList<string> extraGroupBy, IReadOnlyList<MeasureField> measures,
        FilterSet filters, IReadOnlyList<OutputItem> items, IReadOnlyList<DaxOutputColumn> columns,
        IReadOnlyList<SortKey> sortKeys, string sortDescription, IReadOnlyList<string> warnings, bool applyFormatStrings,
        IReadOnlyList<QueryMeasure> queryMeasures)
    {
        _dims = dims;
        _extraGroupBy = extraGroupBy;
        _measures = measures;
        _filters = filters;
        _items = items;
        _sortKeys = sortKeys;
        _queryMeasures = queryMeasures;
        Columns = columns;
        SortDescription = sortDescription;
        Warnings = warnings;
        ApplyFormatStrings = applyFormatStrings;
        ReportMeasures = queryMeasures.Select(q => q.Name).ToList();
        MeasureFilters = filters.Descriptions;
        Parameters = filters.Parameters;
    }

    /// <summary>Output (CSV) columns in visual order.</summary>
    public IReadOnlyList<DaxOutputColumn> Columns { get; }

    /// <summary>Report-level measures defined in the query (used ones plus their dependencies).</summary>
    public IReadOnlyList<string> ReportMeasures { get; }

    /// <summary>Applied measure filters (value and Top N), e.g. "[Total Sales] > 1000".</summary>
    public IReadOnlyList<string> MeasureFilters { get; }

    /// <summary>Dynamic M parameters set from filters on their bound columns; the rest keep their defaults.</summary>
    public IReadOnlyList<MParameterValue> Parameters { get; }

    /// <summary>false = project raw typed values instead of FORMAT()-ed text.</summary>
    public bool ApplyFormatStrings { get; }

    /// <summary>Human-readable row order, e.g. "Total Sales DESC (visual sort)" or "off".</summary>
    public string SortDescription { get; }

    /// <summary>Parts of the visual configuration that could not be reproduced exactly.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>The export query; <paramref name="maxRows"/> &gt; 0 caps the result with TOPN.</summary>
    public string BuildQuery(long maxRows = 0)
    {
        var proj = Projection(out var order);
        var select = "SELECTCOLUMNS(_summ, " + string.Join(", ", proj) + ")";
        var sb = new StringBuilder();
        sb.Append("DEFINE\n");
        // Dynamic M parameters first, as Power BI emits them.
        foreach (var p in _filters.Parameters)
            sb.Append("  MPARAMETER ").Append(DaxQueryBuilder.QuoteName(p.Name)).Append(" = ").Append(p.Value).Append('\n');
        // Report measures, dependencies first. The newline after each expression ends any
        // trailing // comment. Continuation lines aren't re-indented (could alter string literals).
        foreach (var qm in _queryMeasures)
            sb.Append("  MEASURE ").Append(qm.Reference).Append(" =\n    ").Append(qm.Expression.Trim()).Append('\n');

        // Filters as VARs, in the order Power BI applies them (see FilterSet).
        var filterVars = new List<string>();
        for (int i = 0; i < _filters.Columns.Count; i++)
        {
            sb.Append($"  VAR _f{i} = ").Append(_filters.Columns[i]).Append('\n');
            filterVars.Add($"_f{i}");
        }
        var columnFilterVars = filterVars.ToList();
        for (int i = 0; i < _filters.TopN.Count; i++)
        {
            var t = _filters.TopN[i];
            var ranked = "SUMMARIZECOLUMNS(" + string.Join(", ",
                new[] { t.ColumnRef }.Concat(columnFilterVars).Append($"\"tn\", {t.OrderExpression}")) + ")";
            sb.Append($"  VAR _tn{i} = TOPN({t.Count}, {ranked}, [tn], {(t.Bottom ? 1 : 0)})\n");
            filterVars.Add($"_tn{i}");
        }
        if (_filters.ValuePredicates.Count > 0)
        {
            // The visual's rows with all its measures (so blank rows drop out the same way) plus
            // filter-only measures under IGNORE() (they mustn't keep otherwise blank rows);
            // used as a filter on the group-by columns of the final SUMMARIZECOLUMNS.
            var filterOnly = _filters.ValueMeasures.Select((e, j) => $"\"vf_{j}\", IGNORE({e})");
            sb.Append("  VAR _vf = FILTER(KEEPFILTERS(").Append(Summarize(filterVars, filterOnly)).Append("), ")
              .Append(string.Join(" && ", _filters.ValuePredicates)).Append(")\n");
            filterVars.Add("_vf");
        }
        sb.Append("  VAR _summ = ").Append(Summarize(filterVars, Array.Empty<string>())).Append('\n');
        sb.Append("EVALUATE\n");
        if (maxRows <= 0) sb.Append(select);
        else if (order.Count == 0) sb.Append($"TOPN({maxRows}, {select})");
        else sb.Append($"TOPN({maxRows}, {select}, {string.Join(", ", order.Select(o => $"{o.Ref}, {(o.Descending ? 0 : 1)}"))})");
        if (order.Count > 0)
            sb.Append("\nORDER BY ").Append(string.Join(", ", order.Select(o => o.Ref + (o.Descending ? " DESC" : " ASC"))));
        return sb.ToString();
    }

    private string Summarize(IEnumerable<string> filterArgs, IEnumerable<string> extraMeasures)
    {
        var parts = new List<string>();
        parts.AddRange(_dims.Select(d => d.ColumnRef));
        // Grouped on but not projected: relatedColumnDetails keys and Sort By columns.
        parts.AddRange(_extraGroupBy);
        parts.AddRange(filterArgs);
        for (int i = 0; i < _measures.Count; i++)
            parts.Add($"\"m_{i}\", {_measures[i].Expression}");
        parts.AddRange(extraMeasures);
        return "SUMMARIZECOLUMNS(" + string.Join(", ", parts) + ")";
    }

    private string OutputExpression(OutputItem item)
    {
        if (!item.IsMeasure)
        {
            var d = _dims[item.Index];
            return ApplyFormatStrings ? DaxQueryBuilder.FormatDimExpression(d) : d.ColumnRef;
        }
        var m = $"[m_{item.Index}]";
        return ApplyFormatStrings ? DaxQueryBuilder.FormatMeasureExpression(m, _measures[item.Index].FormatString) : m;
    }

    private List<string> Projection(out List<(string Ref, bool Descending)> order)
    {
        var proj = new List<string>();
        var aliasOf = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < _items.Count; i++)
        {
            var expr = OutputExpression(_items[i]);
            proj.Add($"\"{Columns[i].Alias}\", {expr}");
            aliasOf.TryAdd(expr, Columns[i].Alias);
        }

        // Sort on raw values: reuse a column that already projects the raw expression, otherwise
        // add a hidden [sN] key (formatted text like "$1,000" would sort as text). A repeated key
        // can't change the order, so only its first occurrence is kept.
        order = new List<(string, bool)>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        int si = 0;
        foreach (var key in _sortKeys)
        {
            if (!aliasOf.TryGetValue(key.Expression, out var alias))
            {
                alias = $"s{si++}";
                proj.Add($"\"{alias}\", {key.Expression}");
                aliasOf.Add(key.Expression, alias);
            }
            if (used.Add(alias)) order.Add(($"[{alias}]", key.Descending));
        }
        return proj;
    }
}

internal sealed record DimField(string Header, string ColumnRef, string? FormatString, TOM.DataType DataType, ExportField? Source);
internal sealed record MeasureField(string Header, string Expression, string? FormatString, ExportField? Source);
