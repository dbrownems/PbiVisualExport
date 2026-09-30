using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        var exprs = new List<string>();
        for (int i = 0; i < _items.Count; i++)
        {
            exprs.Add(OutputExpression(_items[i]));
            proj.Add($"\"{Columns[i].Alias}\", {exprs[i]}");
        }

        // Sort on raw values: reuse an output column that already projects the raw expression,
        // otherwise add a hidden [sN] key (formatted text like "$1,000" would sort as text).
        order = new List<(string, bool)>();
        int si = 0;
        foreach (var key in _sortKeys)
        {
            var hit = exprs.IndexOf(key.Expression);
            if (hit >= 0)
            {
                order.Add(($"[{Columns[hit].Alias}]", key.Descending));
                continue;
            }
            var alias = $"s{si++}";
            proj.Add($"\"{alias}\", {key.Expression}");
            exprs.Add(key.Expression);
            order.Add(($"[{alias}]", key.Descending));
        }
        return proj;
    }
}

/// <summary>
/// Translates a visual export request + TOM metadata into a single-query DAX plan.
/// Dimension columns become SUMMARIZECOLUMNS group-by columns; measures (explicit
/// or implicit aggregations) become extension columns. Output columns keep the
/// visual's order. Model format strings are applied via DAX FORMAT(). Model-hidden
/// columns/measures are exported like any other field (the visual shows them).
/// Fields hidden on the visual are still grouped on / computed, so the row set matches
/// the visual, but are not output. Measures the model doesn't have are looked up in the
/// report's measures (see <see cref="Build"/>) and defined in the query.
/// </summary>
internal sealed class DaxQueryBuilder
{
    /// <summary>
    /// Fields, sort measures and filter targets the model doesn't define. Measures may be
    /// report-level measures; <see cref="Build"/> needs the report definition to resolve them.
    /// </summary>
    public static List<UnresolvedField> FindUnresolvedFields(VisualExportRequest request, TOM.Model model)
    {
        var list = new List<UnresolvedField>();
        foreach (var f in request.Fields)
        {
            if (string.IsNullOrWhiteSpace(f.Table)) continue;
            if (!string.IsNullOrEmpty(f.Measure))
            {
                if (FindMeasure(model, f.Measure!) is null) list.Add(new UnresolvedField($"[{f.Measure}]", f.Measure, false));
            }
            else if (!string.IsNullOrEmpty(f.Column) && FindColumn(model, f.Table, f.Column!) is null)
                list.Add(new UnresolvedField(ColRef(f.Table, f.Column!), null, false));
        }
        if (request.ApplySort)
            foreach (var s in request.Sort)
                if (!string.IsNullOrWhiteSpace(s.Table) && !string.IsNullOrEmpty(s.Measure) && FindMeasure(model, s.Measure!) is null)
                    list.Add(new UnresolvedField($"[{s.Measure}]", s.Measure, true));
        // Measures in active value / Top N filters: dropping such a filter would over-export.
        foreach (var name in request.Filters.SelectMany(FilterMeasureNames).Distinct(StringComparer.OrdinalIgnoreCase))
            if (FindMeasure(model, name) is null && !list.Any(u => Eq(u.MeasureName, name) && !u.SortOnly))
                list.Add(new UnresolvedField($"[{name}]", name, false));
        // Columns of active column filters: a (stale) model without the column can't translate the
        // filter, or know that it sets a dynamic M parameter.
        foreach (var (table, column) in request.Filters.Select(ActiveFilterColumn).OfType<(string, string)>())
        {
            var label = ColRef(table, column);
            if (FindColumn(model, table, column) is null && !list.Any(u => Eq(u.Label, label)))
                list.Add(new UnresolvedField(label, null, false));
        }
        return list;
    }

    public DaxPlan Build(VisualExportRequest request, TOM.Model model, ReportDefinition? report = null, IEnumerable<string>? warningsSoFar = null)
    {
        var dims = new List<DimField>();
        var measures = new List<MeasureField>();
        var items = new List<OutputItem>();
        var usedReportMeasures = new List<ReportMeasure>();

        // A measure not in the model may be a report-level measure (names are unique across both).
        ReportMeasure? UseReportMeasure(string name)
        {
            var rm = report?.FindMeasure(name);
            if (rm is not null && !usedReportMeasures.Contains(rm)) usedReportMeasures.Add(rm);
            return rm;
        }

        foreach (var f in request.Fields)
        {
            if (string.IsNullOrWhiteSpace(f.Table)) continue;
            var table = model.Tables.Find(f.Table);

            if (f.IsMeasure)
            {
                if (!string.IsNullOrEmpty(f.Measure))
                {
                    var format = FindMeasure(model, f.Measure!) is { } m ? m.FormatString : UseReportMeasure(f.Measure!)?.FormatString;
                    measures.Add(new MeasureField(f.DisplayName, $"[{EscBracket(f.Measure!)}]", format, f));
                }
                else if (!string.IsNullOrEmpty(f.Column) && !string.IsNullOrEmpty(f.Aggregation))
                {
                    var col = table?.Columns.Find(f.Column);
                    var expr = AggregationExpression(f.Aggregation!, f.Table, f.Column!);
                    if (expr is null) continue;
                    measures.Add(new MeasureField(f.DisplayName, expr, col?.FormatString, f));
                }
                else continue;
                // Hidden on the visual: computed (it can decide which rows are non-blank) but not output.
                if (!f.Hidden) items.Add(new OutputItem(true, measures.Count - 1));
                continue;
            }

            // Dimension column. Always grouped on, even when hidden: dropping a grouping
            // column would merge rows and re-aggregate the measures.
            if (string.IsNullOrEmpty(f.Column)) continue;
            var column = table?.Columns.Find(f.Column);
            dims.Add(new DimField(
                Header: f.DisplayName,
                ColumnRef: ColRef(f.Table, f.Column!),
                FormatString: column?.FormatString,
                DataType: column?.DataType ?? TOM.DataType.String,
                Source: f));
            if (!f.Hidden) items.Add(new OutputItem(false, dims.Count - 1));
        }

        if (items.Count == 0)
            throw new ExportRequestException("The visual has no exportable columns or measures (all fields are hidden on the visual).");

        var extraGroupBy = GroupByKeyColumns(request, model, dims);
        var warnings = new List<string>(warningsSoFar ?? Array.Empty<string>());
        var filters = BuildFilters(request.Filters, model, measures, name => UseReportMeasure(name) is not null, warnings,
            new ClientTime(request.TimeZone));

        List<SortKey> sortKeys;
        string sortDescription;
        if (request.ApplySort)
            sortKeys = BuildSortKeys(request.Sort, model, dims, measures, extraGroupBy, warnings,
                name => UseReportMeasure(name) is not null, out sortDescription);
        else
        {
            sortKeys = new List<SortKey>();
            sortDescription = "off";
        }

        // Output columns in visual order, with stable positional DAX aliases.
        var outCols = items
            .Select((it, i) => new DaxOutputColumn($"c{i}", it.IsMeasure ? measures[it.Index].Header : dims[it.Index].Header, it.IsMeasure))
            .ToList();

        return new DaxPlan(dims, extraGroupBy, measures, filters, items, outCols, sortKeys, sortDescription, warnings,
            request.ApplyFormatStrings, QueryMeasures(model, report, usedReportMeasures));
    }

    /// <summary>
    /// DEFINE MEASURE entries for the report measures used, plus the report measures they
    /// reference (dependencies first). Each is defined on its home table, which must exist.
    /// </summary>
    private static List<QueryMeasure> QueryMeasures(TOM.Model model, ReportDefinition? report, List<ReportMeasure> used)
    {
        if (report is null || used.Count == 0) return new List<QueryMeasure>();
        var result = new List<QueryMeasure>();
        foreach (var rm in report.WithDependencies(used))
        {
            var table = FindTable(model, rm.Entity)
                ?? throw new ExportRequestException(
                    $"Report measure [{rm.Name}] is defined on table '{rm.Entity}', which is not in the semantic model.");
            result.Add(new QueryMeasure(rm.Name, ColRef(table.Name, rm.Name), rm.Expression));
        }
        return result;
    }

    /// <summary>
    /// Raw sort expressions for the visual's sort. Explicit sort fields are matched to the
    /// visual's columns/measures; a measure or aggregation the visual sorts by but doesn't
    /// show is added as a sort-only measure. A column is ordered by its Sort By Column, as
    /// Power BI does (e.g. Month Name by Month Number), which is grouped on too - it's 1:1
    /// with the column so granularity is unchanged. Without an explicit sort, Power BI's
    /// implicit order is approximated by the group-by columns ascending in visual order.
    /// </summary>
    private static List<SortKey> BuildSortKeys(List<ExportSort> sort, TOM.Model model, List<DimField> dims,
        List<MeasureField> measures, List<string> extraGroupBy, List<string> warnings,
        Func<string, bool> isReportMeasure, out string description)
    {
        var keys = new List<SortKey>();
        var labels = new List<string>();
        var grouped = new HashSet<string>(dims.Select(d => d.ColumnRef).Concat(extraGroupBy), StringComparer.OrdinalIgnoreCase);

        string DimSortExpression(string table, string column)
        {
            var sortBy = model.Tables.Find(table)?.Columns.Find(column)?.SortByColumn;
            if (sortBy is null) return ColRef(table, column);
            var sortRef = ColRef(sortBy.Table.Name, sortBy.Name);
            if (grouped.Add(sortRef)) extraGroupBy.Add(sortRef);
            return sortRef;
        }

        int MeasureIndex(Func<ExportField, bool> match, Func<string?> sortOnlyExpression, string label)
        {
            var i = measures.FindIndex(m => m.Source is { } f && match(f));
            if (i >= 0) return i;
            var expr = sortOnlyExpression();
            if (expr is null) return -1;
            measures.Add(new MeasureField(label, expr, null, null));
            return measures.Count - 1;
        }

        if (sort.Count == 0)
        {
            foreach (var d in dims)
            {
                keys.Add(new SortKey(DimSortExpression(d.Source!.Table, d.Source.Column!), false));
                labels.Add(d.Header + " ASC");
            }
            description = keys.Count > 0 ? string.Join(", ", labels) + " (implicit default)" : "none";
            return keys;
        }

        foreach (var s in sort)
        {
            if (string.IsNullOrWhiteSpace(s.Table)) continue;
            if (!string.IsNullOrEmpty(s.Measure))
            {
                var i = MeasureIndex(
                    f => Eq(f.Measure, s.Measure),
                    () => FindMeasure(model, s.Measure!) is not null || isReportMeasure(s.Measure!) ? $"[{EscBracket(s.Measure!)}]" : null,
                    s.Measure!);
                if (i < 0) { warnings.Add($"Sort measure [{s.Measure}] not found in the model or the report's measures; ignored."); continue; }
                keys.Add(new SortKey($"[m_{i}]", s.Descending));
            }
            else if (!string.IsNullOrEmpty(s.Column) && !string.IsNullOrEmpty(s.Aggregation))
            {
                var agg = NormalizeAggregation(s.Aggregation);
                var i = MeasureIndex(
                    f => Eq(f.Table, s.Table) && Eq(f.Column, s.Column) && NormalizeAggregation(f.Aggregation) == agg,
                    () => AggregationExpression(s.Aggregation!, s.Table, s.Column!),
                    s.Label);
                if (i < 0) { warnings.Add($"Sort aggregation {s.Label} could not be built; ignored."); continue; }
                keys.Add(new SortKey($"[m_{i}]", s.Descending));
            }
            else if (!string.IsNullOrEmpty(s.Column))
            {
                if (!dims.Any(d => d.Source is { } f && Eq(f.Table, s.Table) && Eq(f.Column, s.Column)))
                {
                    // Grouping on a column the visual doesn't group on would change the rows.
                    warnings.Add($"Sort column '{s.Table}'[{s.Column}] is not a visible column of the visual; ignored.");
                    continue;
                }
                keys.Add(new SortKey(DimSortExpression(s.Table, s.Column!), s.Descending));
            }
            else continue;
            labels.Add(s.Label);
        }
        description = keys.Count > 0 ? string.Join(", ", labels) + " (visual sort)" : "none";
        return keys;
    }

    internal static string NormalizeAggregation(string? aggregation) => (aggregation ?? "").Trim().ToLowerInvariant() switch
    {
        "average" => "avg",
        "countnonnull" => "count",
        "countdistinct" => "distinctcount",
        var a => a
    };

    private static bool Eq(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A column with <c>relatedColumnDetails</c> (e.g. a field parameter's display column,
    /// grouped by its hidden 'Fields' column) can only be grouped together with its
    /// group-by key columns, as Power BI does implicitly. Returns those keys, excluding
    /// any the visual already groups on.
    /// </summary>
    private static List<string> GroupByKeyColumns(VisualExportRequest request, TOM.Model model, List<DimField> dims)
    {
        var present = new HashSet<string>(dims.Select(d => d.ColumnRef), StringComparer.OrdinalIgnoreCase);
        var keys = new List<string>();
        foreach (var f in request.Fields)
        {
            if (f.IsMeasure || string.IsNullOrEmpty(f.Table) || string.IsNullOrEmpty(f.Column)) continue;
            var column = model.Tables.Find(f.Table)?.Columns.Find(f.Column);
            if (column is null || column.RelatedColumnDetails is null) continue;
            foreach (var g in column.RelatedColumnDetails.GroupByColumns)
            {
                if (g.GroupingColumn is null) continue;
                var keyRef = $"{QuoteTable(g.GroupingColumn.Table.Name)}[{EscBracket(g.GroupingColumn.Name)}]";
                if (present.Add(keyRef)) keys.Add(keyRef);
            }
        }
        return keys;
    }

    internal static string FormatDimExpression(DimField d)
    {
        // Apply the model format string for numeric/date/boolean columns; strings pass through.
        if (!string.IsNullOrEmpty(d.FormatString) && IsFormattable(d.DataType))
            return $"FORMAT({d.ColumnRef}, {QuoteString(d.FormatString!)})";
        return d.ColumnRef;
    }

    internal static string FormatMeasureExpression(string measureRef, string? formatString)
        => !string.IsNullOrEmpty(formatString)
            ? $"FORMAT({measureRef}, {QuoteString(formatString!)})"
            : measureRef;

    private static bool IsFormattable(TOM.DataType t) => t is
        TOM.DataType.Int64 or TOM.DataType.Double or TOM.DataType.Decimal or
        TOM.DataType.DateTime or TOM.DataType.Boolean;

    private static string? AggregationExpression(string aggregation, string table, string column)
    {
        var colRef = $"{QuoteTable(table)}[{EscBracket(column)}]";
        return aggregation.Trim().ToLowerInvariant() switch
        {
            "sum" => $"SUM({colRef})",
            "avg" or "average" => $"AVERAGE({colRef})",
            "min" => $"MIN({colRef})",
            "max" => $"MAX({colRef})",
            "count" => $"COUNT({colRef})",
            "countnonnull" => $"COUNT({colRef})",
            "distinctcount" or "countdistinct" => $"DISTINCTCOUNT({colRef})",
            _ => $"SUM({colRef})"
        };
    }

    // ---- Filters ----

    /// <summary>
    /// Translates the report, page and visual filters and slicer states sent by the client.
    /// Column targets become filter tables. Measure and implicit-measure targets (visual-level
    /// value filters such as "Total Sales is greater than 1000") and Top N filters are
    /// evaluated as Power BI does (see <see cref="FilterSet"/>). A filter on a column bound to a
    /// dynamic M query parameter also sets the parameter (<c>MPARAMETER</c>). Filters that fail to
    /// translate are skipped with a warning; a filter measure that doesn't exist fails the export.
    /// </summary>
    private static FilterSet BuildFilters(IEnumerable<JsonElement> filters, TOM.Model model, List<MeasureField> measures,
        Func<string, bool> isReportMeasure, List<string> warnings, ClientTime time)
    {
        var set = new FilterSet();
        var bindings = ParameterBindings(model);
        var selections = new List<ParameterSelection>();
        foreach (var filter in filters)
        {
            if (filter.ValueKind != JsonValueKind.Object || !filter.TryGetProperty("target", out var target)) continue;
            if (target.ValueKind == JsonValueKind.Array)
            {
                // Hierarchy slicer (filterType 9, hierarchyData) or tuple filter (filterType 6, values).
                if ((filter.TryGetProperty("hierarchyData", out var hd) && hd.ValueKind == JsonValueKind.Array && hd.GetArrayLength() > 0) ||
                    (filter.TryGetProperty("values", out var tv) && tv.ValueKind == JsonValueKind.Array && tv.GetArrayLength() > 0))
                    warnings.Add($"Multi-column filter on {string.Join(" > ", target.EnumerateArray().Select(TargetLabel))} is not supported; ignored.");
                continue;
            }
            if (target.ValueKind != JsonValueKind.Object) continue;
            try
            {
                if (IsTopN(filter)) AddTopN(filter, target, model, isReportMeasure, set, warnings);
                else if (IsMeasureTarget(target)) AddValueFilter(filter, target, model, measures, isReportMeasure, set, warnings, time);
                else
                {
                    // A filter on a dynamic M parameter's bound column also sets the parameter;
                    // Power BI keeps the column filter as well.
                    if (bindings.Count > 0 && Str(target, "table") is { } bt && Str(target, "column") is { } bc &&
                        FindColumn(model, bt, bc) is { } boundColumn && bindings.TryGetValue(boundColumn, out var bound))
                        foreach (var b in bound) SelectParameterValues(filter, b, selections, time);
                    if (TranslateColumnFilter(filter, target, model, time) is { } arg) set.Columns.Add(arg);
                }
            }
            catch (ExportRequestException) { throw; }
            catch (Exception ex)
            {
                warnings.Add($"Filter on {TargetLabel(target)} could not be translated ({ex.Message}); ignored.");
            }
        }

        foreach (var s in selections)
        {
            var b = s.Binding;
            if (s.Literals.Count == 0)
                throw new ExportRequestException(
                    $"The filters on {b.ColumnRef} have no value in common, so dynamic M query parameter {b.Name} (bound to it) can't be set.");
            if (!b.MultiSelect && s.Literals.Count > 1)
                throw new ExportRequestException(
                    $"{b.ColumnRef} is bound to dynamic M query parameter {b.Name}, which takes a single value, but the filters " +
                    $"select {s.Literals.Count} ({string.Join(", ", s.Literals)}). Power BI can't export this selection either: " +
                    "select one value, or allow multi-select on the parameter binding.");
            set.Parameters.Add(new MParameterValue(b.Name, b.ColumnRef, s.Literals, b.MultiSelect, s.SelectAll));
        }
        return set;
    }

    // ---- Dynamic M query parameters ----

    /// <summary>An M parameter bound to a model column (the expression's <c>ParameterValuesColumn</c>).</summary>
    private sealed record ParameterBinding(string Name, string ColumnRef, TOM.DataType DataType, bool MultiSelect, JsonElement? SelectAllValue);

    /// <summary>
    /// The values selected so far for a parameter (DAX literals), intersected across its filters.
    /// <paramref name="SelectAll"/>: only "Select all" so far (Literals = the binding's select-all value).
    /// </summary>
    private sealed record ParameterSelection(ParameterBinding Binding, List<string> Literals, bool SelectAll = false);

    private static Dictionary<TOM.Column, List<ParameterBinding>> ParameterBindings(TOM.Model model)
    {
        var map = new Dictionary<TOM.Column, List<ParameterBinding>>();
        foreach (TOM.NamedExpression e in model.Expressions)
        {
            if (e.ParameterValuesColumn is not { } col) continue;
            if (!map.TryGetValue(col, out var list)) map[col] = list = new();
            var (multi, selectAll) = ReadParameterMetadata(col);
            list.Add(new ParameterBinding(e.Name, ColRef(col.Table.Name, col.Name), col.DataType, multi, selectAll));
        }
        return map;
    }

    /// <summary>
    /// The bound column's <c>ParameterMetadata</c> extended property, as Power BI Desktop writes
    /// it: <c>"supportsMultipleValues": true</c> makes the binding multi-select (the parameter
    /// then receives a list, even for one value), and <c>"selectAllValue"</c> is what a slicer's
    /// "Select all" passes (e.g. <c>"__SelectAll__"</c>).
    /// </summary>
    private static (bool MultiSelect, JsonElement? SelectAllValue) ReadParameterMetadata(TOM.Column column)
    {
        if (column.ExtendedProperties.Find("ParameterMetadata") is not TOM.JsonExtendedProperty p || string.IsNullOrWhiteSpace(p.Value))
            return (false, null);
        try
        {
            using var doc = JsonDocument.Parse(p.Value);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (false, null);
            var multi = root.TryGetProperty("supportsMultipleValues", out var v) && v.ValueKind == JsonValueKind.True;
            JsonElement? selectAll = root.TryGetProperty("selectAllValue", out var s) &&
                                     s.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? s.Clone() : null;
            return (multi, selectAll);
        }
        catch (JsonException)
        {
            return (false, null);
        }
    }

    /// <summary>
    /// Records the values a filter on a bound column selects, as Power BI binds them: In (or
    /// Is conditions) sets the parameter to those values; All or an empty selection leaves its
    /// default; a slicer's "Select all" (<c>selectAll: true</c>, sent by the client) passes the
    /// binding's select-all value. Anything else (NotIn, comparisons, ...) can't become
    /// parameter values; Power BI fails to export it too, so it's a 400 rather than an export
    /// with the default value.
    /// </summary>
    private static void SelectParameterValues(JsonElement filter, ParameterBinding b, List<ParameterSelection> selections, ClientTime time)
    {
        ExportRequestException Unsupported(string what) => new(
            $"The filter on {b.ColumnRef} {what}, but the column is bound to dynamic M query parameter {b.Name}, which needs " +
            "the selected values. Power BI can't export this selection either: select values with a slicer or an 'In' filter.");

        var i = selections.FindIndex(s => s.Binding == b);
        if (filter.TryGetProperty("selectAll", out var sa) && sa.ValueKind == JsonValueKind.True)
        {
            if (b.SelectAllValue is not { } value)
                throw new ExportRequestException(
                    $"The slicer on {b.ColumnRef} has \"Select all\" selected, but the column's binding to dynamic M query parameter " +
                    $"{b.Name} has no \"Select all\" value. Enable \"Select all\" on the parameter binding in the model, or select values.");
            // Other filters on the column narrow "Select all" to their values.
            if (i < 0) selections.Add(new ParameterSelection(b, new List<string> { DaxLiteral(b.DataType, value, time) }, SelectAll: true));
            return;
        }

        List<string>? literals = null;
        if (filter.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array && filter.TryGetProperty("operator", out _))
        {
            var op = Str(filter, "operator") ?? "In";
            if (Eq(op, "All") || values.GetArrayLength() == 0) return;
            if (!Eq(op, "In")) throw Unsupported(Eq(op, "NotIn") ? "excludes values" : $"uses operator '{op}'");
            literals = values.EnumerateArray().Select(v => DaxLiteral(b.DataType, v, time)).ToList();
        }
        else if (filter.TryGetProperty("conditions", out var conds) && conds.ValueKind == JsonValueKind.Array)
        {
            var and = !Eq(Str(filter, "logicalOperator"), "Or");
            foreach (var c in conds.EnumerateArray())
            {
                var op = Str(c, "operator");
                if (Eq(op, "None")) continue;
                if (!Eq(op, "Is") || !c.TryGetProperty("value", out var v)) throw Unsupported($"uses condition '{op}'");
                var lit = DaxLiteral(b.DataType, v, time);
                if (literals is null) literals = new List<string> { lit };
                else if (and) literals = literals.Where(l => Eq(l, lit)).ToList();
                else literals.Add(lit);
            }
            if (literals is null) return;
        }
        else if (Str(filter, "operator") is { } other && !Eq(other, "All"))
            throw Unsupported($"uses operator '{other}'");
        else return;

        literals = literals.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (i < 0) selections.Add(new ParameterSelection(b, literals));
        else if (selections[i].SelectAll) selections[i] = new ParameterSelection(b, literals);
        else selections[i] = selections[i] with { Literals = selections[i].Literals.Where(l => literals.Contains(l, StringComparer.OrdinalIgnoreCase)).ToList() };
    }

    private static bool IsTopN(JsonElement filter)
        => (Str(filter, "$schema")?.EndsWith("#topN", StringComparison.OrdinalIgnoreCase) ?? false) ||
           (filter.TryGetProperty("filterType", out var t) && t.ValueKind == JsonValueKind.Number && t.GetInt32() == 5);

    /// <summary>A measure ({table, measure}) or implicit measure ({table, column, aggregationFunction}).</summary>
    private static bool IsMeasureTarget(JsonElement target)
        => !string.IsNullOrEmpty(Str(target, "measure")) || !string.IsNullOrEmpty(Str(target, "aggregationFunction"));

    private static bool HasConditions(JsonElement filter)
        => filter.TryGetProperty("conditions", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0;

    private static long TopNCount(JsonElement filter)
        => filter.TryGetProperty("itemCount", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt64(out var n) ? n : 0;

    /// <summary>
    /// The column an active column filter (basic values, advanced conditions or Top N) targets;
    /// null for measure targets, hierarchy targets and filters that select everything.
    /// </summary>
    private static (string Table, string Column)? ActiveFilterColumn(JsonElement filter)
    {
        if (filter.ValueKind != JsonValueKind.Object || !filter.TryGetProperty("target", out var target) ||
            target.ValueKind != JsonValueKind.Object || IsMeasureTarget(target) ||
            Str(target, "table") is not { Length: > 0 } table || Str(target, "column") is not { Length: > 0 } column)
            return null;
        var active = IsTopN(filter)
            ? TopNCount(filter) > 0
            : HasConditions(filter) ||
              (filter.TryGetProperty("values", out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0 &&
               !Eq(Str(filter, "operator"), "All"));
        return active ? (table, column) : null;
    }

    /// <summary>Measure names referenced by active value and Top N filters.</summary>
    private static IEnumerable<string> FilterMeasureNames(JsonElement filter)
    {
        if (filter.ValueKind != JsonValueKind.Object || !filter.TryGetProperty("target", out var target)) yield break;
        if (IsTopN(filter))
        {
            if (TopNCount(filter) > 0 && filter.TryGetProperty("orderBy", out var orderBy) && Str(orderBy, "measure") is { Length: > 0 } o)
                yield return o;
        }
        else if (Str(target, "measure") is { Length: > 0 } m && HasConditions(filter))
            yield return m;
    }

    /// <summary>DAX for a measure or implicit-measure target; null if the target is neither.</summary>
    private static string? MeasureTargetExpression(JsonElement target, TOM.Model model, Func<string, bool> isReportMeasure)
    {
        if (Str(target, "measure") is { Length: > 0 } name)
        {
            if (FindMeasure(model, name) is null && !isReportMeasure(name))
                throw new ExportRequestException(
                    $"Filter measure [{name}] was not found in the semantic model or the report's measures.");
            return $"[{EscBracket(name)}]";
        }
        if (Str(target, "table") is { Length: > 0 } table && Str(target, "column") is { Length: > 0 } column &&
            Str(target, "aggregationFunction") is { Length: > 0 } agg)
            return AggregationExpression(agg, table, column);
        return null;
    }

    /// <summary>
    /// A visual-level filter on a measure's value, evaluated per row of the visual. A measure
    /// the visual shows reuses its [m_i] column; any other is a filter-only [vf_i] column.
    /// </summary>
    private static void AddValueFilter(JsonElement filter, JsonElement target, TOM.Model model, List<MeasureField> measures,
        Func<string, bool> isReportMeasure, FilterSet set, List<string> warnings, ClientTime time)
    {
        string Literal(JsonElement v) => MeasureLiteral(v, time);
        if (!HasConditions(filter)) return; // a filter card with nothing set
        var expr = MeasureTargetExpression(target, model, isReportMeasure);
        if (expr is null) { warnings.Add($"Filter on {TargetLabel(target)} is not supported; ignored."); return; }

        var mi = measures.FindIndex(m => string.Equals(m.Expression, expr, StringComparison.OrdinalIgnoreCase));
        var vi = set.ValueMeasures.FindIndex(e => string.Equals(e, expr, StringComparison.OrdinalIgnoreCase));
        var colRef = mi >= 0 ? $"[m_{mi}]" : $"[vf_{(vi >= 0 ? vi : set.ValueMeasures.Count)}]";

        var joiner = Eq(Str(filter, "logicalOperator"), "Or") ? " || " : " && ";
        var preds = new List<string>();
        var labels = new List<string>();
        foreach (var cond in filter.GetProperty("conditions").EnumerateArray())
        {
            if (Eq(Str(cond, "operator"), "None")) continue;
            var p = TranslateCondition(cond, colRef, Literal);
            if (p is null)
            {
                // Dropping one condition would change the rows either way (And: more, Or: fewer).
                warnings.Add($"Filter on {TargetLabel(target)} uses unsupported condition '{Str(cond, "operator")}'; filter ignored.");
                return;
            }
            preds.Add(p);
            labels.Add(TranslateCondition(cond, expr, Literal)!);
        }
        if (preds.Count == 0) return;

        if (mi < 0 && vi < 0) set.ValueMeasures.Add(expr);
        set.ValuePredicates.Add(preds.Count == 1 ? preds[0] : "(" + string.Join(joiner, preds) + ")");
        set.Descriptions.Add(string.Join(joiner, labels));
    }

    /// <summary>Top/Bottom N values of a column by a measure, ranked under the column filters.</summary>
    private static void AddTopN(JsonElement filter, JsonElement target, TOM.Model model, Func<string, bool> isReportMeasure,
        FilterSet set, List<string> warnings)
    {
        var count = TopNCount(filter);
        if (count <= 0 || !filter.TryGetProperty("orderBy", out var orderBy) || orderBy.ValueKind != JsonValueKind.Object)
            return; // not set
        var table = Str(target, "table");
        var column = Str(target, "column");
        var order = MeasureTargetExpression(orderBy, model, isReportMeasure);
        if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(column) || IsMeasureTarget(target) || order is null)
        {
            warnings.Add($"Top N filter on {TargetLabel(target)} by {TargetLabel(orderBy)} is not supported; ignored.");
            return;
        }
        var bottom = Eq(Str(filter, "operator"), "Bottom");
        var colRef = ColRef(table, column);
        set.TopN.Add(new TopNFilter(colRef, count, bottom, order));
        set.Descriptions.Add($"{(bottom ? "Bottom" : "Top")} {count} {colRef} by {order}");
    }

    private static string TargetLabel(JsonElement target)
    {
        var table = Str(target, "table") ?? "?";
        if (Str(target, "measure") is { } m) return $"[{m}]";
        if (Str(target, "column") is { } c)
            return Str(target, "aggregationFunction") is { } a ? $"{a}({ColRef(table, c)})" : ColRef(table, c);
        if (Str(target, "hierarchy") is { } h) return $"{QuoteTable(table)} hierarchy [{h}] level [{Str(target, "hierarchyLevel")}]";
        return QuoteTable(table);
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string? TranslateColumnFilter(JsonElement filter, JsonElement target, TOM.Model model, ClientTime time)
    {
        if (!target.TryGetProperty("table", out var tEl) || !target.TryGetProperty("column", out var cEl))
            return null; // only column targets supported (skip hierarchy filters)

        var tableName = tEl.GetString();
        var columnName = cEl.GetString();
        if (string.IsNullOrEmpty(tableName) || string.IsNullOrEmpty(columnName)) return null;

        var colRef = $"{QuoteTable(tableName)}[{EscBracket(columnName)}]";
        var dataType = model.Tables.Find(tableName)?.Columns.Find(columnName)?.DataType ?? TOM.DataType.String;

        var schema = filter.TryGetProperty("$schema", out var sEl) ? sEl.GetString() ?? "" : "";

        // Basic filter: In / NotIn a set of values.
        if (schema.Contains("basic", StringComparison.OrdinalIgnoreCase) ||
            (filter.TryGetProperty("operator", out _) && filter.TryGetProperty("values", out _)))
        {
            var op = filter.TryGetProperty("operator", out var opEl) ? opEl.GetString() ?? "In" : "In";
            if (!filter.TryGetProperty("values", out var valuesEl) || valuesEl.ValueKind != JsonValueKind.Array)
                return null;
            var literals = valuesEl.EnumerateArray().Select(v => DaxLiteral(dataType, v, time)).ToList();
            if (literals.Count == 0) return null;
            var set = "{" + string.Join(", ", literals) + "}";
            return op.Equals("NotIn", StringComparison.OrdinalIgnoreCase)
                ? $"FILTER(ALL({colRef}), NOT({colRef} IN {set}))"
                : $"TREATAS({set}, {colRef})";
        }

        // Advanced filter: And/Or of comparison conditions.
        if (schema.Contains("advanced", StringComparison.OrdinalIgnoreCase) ||
            filter.TryGetProperty("conditions", out _))
        {
            if (!filter.TryGetProperty("conditions", out var condsEl) || condsEl.ValueKind != JsonValueKind.Array)
                return null;
            var logical = filter.TryGetProperty("logicalOperator", out var lEl) ? lEl.GetString() ?? "And" : "And";
            var joiner = logical.Equals("Or", StringComparison.OrdinalIgnoreCase) ? " || " : " && ";
            var preds = new List<string>();
            foreach (var cond in condsEl.EnumerateArray())
            {
                var p = TranslateCondition(cond, colRef, v => DaxLiteral(dataType, v, time));
                if (p is not null) preds.Add(p);
            }
            if (preds.Count == 0) return null;
            return $"FILTER(ALL({colRef}), {string.Join(joiner, preds)})";
        }

        return null;
    }

    private static string? TranslateCondition(JsonElement cond, string colRef, Func<JsonElement, string> literal)
    {
        var op = cond.TryGetProperty("operator", out var opEl) ? opEl.GetString() : null;
        if (op is null) return null;
        var hasValue = cond.TryGetProperty("value", out var valEl);
        var lit = hasValue ? literal(valEl) : null;
        var text = hasValue ? QuoteString(valEl.ToString()) : null;

        return op switch
        {
            "LessThan" when lit != null => $"{colRef} < {lit}",
            "LessThanOrEqual" when lit != null => $"{colRef} <= {lit}",
            "GreaterThan" when lit != null => $"{colRef} > {lit}",
            "GreaterThanOrEqual" when lit != null => $"{colRef} >= {lit}",
            "Is" when lit != null => $"{colRef} = {lit}",
            "IsNot" when lit != null => $"{colRef} <> {lit}",
            "IsBlank" => $"ISBLANK({colRef})",
            "IsNotBlank" => $"NOT(ISBLANK({colRef}))",
            "Contains" when hasValue => $"SEARCH({text}, {colRef}, 1, 0) > 0",
            "DoesNotContain" when hasValue => $"SEARCH({text}, {colRef}, 1, 0) = 0",
            "StartsWith" when hasValue => $"LEFT({colRef}, LEN({text})) = {text}",
            "DoesNotStartWith" when hasValue => $"LEFT({colRef}, LEN({text})) <> {text}",
            _ => null
        };
    }

    // ---- Literal + identifier encoding ----

    /// <summary>A literal compared with a measure value (measure data types aren't in the metadata).</summary>
    private static string MeasureLiteral(JsonElement value, ClientTime time)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined: return "BLANK()";
            case JsonValueKind.True: return "TRUE()";
            case JsonValueKind.False: return "FALSE()";
            case JsonValueKind.Number:
                return value.TryGetDecimal(out var dec) ? dec.ToString(CultureInfo.InvariantCulture) : value.GetRawText();
            case JsonValueKind.String:
                var s = value.GetString() ?? "";
                if (decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                    return n.ToString(CultureInfo.InvariantCulture);
                if (s.Length >= 10 && char.IsDigit(s[0]) && s[4] == '-' && s[7] == '-')
                    return DaxLiteral(TOM.DataType.DateTime, value, time);
                return QuoteString(s);
            default: return QuoteString(value.ToString());
        }
    }

    private static string DaxLiteral(TOM.DataType dataType, JsonElement value, ClientTime time)
    {
        if (value.ValueKind == JsonValueKind.Null) return "BLANK()";

        switch (dataType)
        {
            case TOM.DataType.Int64:
            case TOM.DataType.Double:
            case TOM.DataType.Decimal:
                if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
                if (value.ValueKind == JsonValueKind.String &&
                    double.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                    return d.ToString(CultureInfo.InvariantCulture);
                return QuoteString(value.ToString());

            case TOM.DataType.Boolean:
                if (value.ValueKind is JsonValueKind.True) return "TRUE()";
                if (value.ValueKind is JsonValueKind.False) return "FALSE()";
                return bool.TryParse(value.GetString(), out var b) ? (b ? "TRUE()" : "FALSE()") : "FALSE()";

            case TOM.DataType.DateTime:
                var raw = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
                if (time.TryParse(raw, out var dt))
                {
                    var datePart = $"DATE({dt.Year}, {dt.Month}, {dt.Day})";
                    if (dt.TimeOfDay == TimeSpan.Zero) return datePart;
                    return $"({datePart} + TIME({dt.Hour}, {dt.Minute}, {dt.Second}))";
                }
                return QuoteString(raw ?? "");

            default:
                return QuoteString(value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString());
        }
    }

    internal static string QuoteString(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    /// <summary>A quoted object name, e.g. a table or M parameter: <c>'Name'</c>.</summary>
    internal static string QuoteName(string name) => QuoteTable(name);
    private static string QuoteTable(string t) => "'" + t.Replace("'", "''") + "'";
    private static string EscBracket(string s) => s.Replace("]", "]]");
    private static string ColRef(string table, string column) => $"{QuoteTable(table)}[{EscBracket(column)}]";

    private static TOM.Table? FindTable(TOM.Model model, string name)
        => model.Tables.Find(name) ?? model.Tables.FirstOrDefault(t => Eq(t.Name, name));

    private static TOM.Column? FindColumn(TOM.Model model, string table, string column)
    {
        var t = FindTable(model, table);
        return t is null ? null : t.Columns.Find(column) ?? t.Columns.FirstOrDefault(c => Eq(c.Name, column));
    }

    private static TOM.Measure? FindMeasure(TOM.Model model, string name)
    {
        foreach (TOM.Table t in model.Tables)
        {
            var m = t.Measures.Find(name);
            if (m is not null) return m;
        }
        // DAX names are case-insensitive.
        return model.Tables.SelectMany(t => t.Measures).FirstOrDefault(m => Eq(m.Name, name));
    }
}

internal sealed record DimField(string Header, string ColumnRef, string? FormatString, TOM.DataType DataType, ExportField? Source);
internal sealed record MeasureField(string Header, string Expression, string? FormatString, ExportField? Source);

/// <summary>
/// Reads the date-time values in filters as the report does. The Power BI JS SDK serializes
/// them with <c>Date.toISOString()</c>: the browser's local wall time as a UTC instant
/// (local 2024-02-01 00:00 at UTC-6 is <c>"2024-02-01T06:00:00.000Z"</c>), and the report
/// engine filters on the local wall time. A value with an offset (or <c>Z</c>) is converted
/// to the wall time in the browser's time zone (<see cref="VisualExportRequest.TimeZone"/>);
/// without one, the wall time as written is used. Values without an offset are wall times.
/// </summary>
internal sealed class ClientTime(string? timeZoneId)
{
    private static readonly Regex HasOffset = new(@"(?:T|\s)\d{1,2}:\d{2}.*(?:Z|[+-]\d{2}:?\d{2})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private TimeZoneInfo? zone;

    public bool TryParse(string? raw, out DateTime wallTime)
    {
        wallTime = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        raw = raw.Trim();
        if (HasOffset.IsMatch(raw) &&
            DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
        {
            wallTime = Zone is { } z ? TimeZoneInfo.ConvertTime(instant, z).DateTime : instant.DateTime;
            return true;
        }
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out wallTime);
    }

    // Resolved on first use, so an unknown zone only fails exports that filter on a date.
    private TimeZoneInfo? Zone
    {
        get
        {
            if (zone is not null || string.IsNullOrWhiteSpace(timeZoneId)) return zone;
            try
            {
                // IANA ids ("America/Chicago") resolve on every platform (ICU on Windows).
                return zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                throw new ExportRequestException(
                    $"Unknown time zone '{timeZoneId}': the date filters can't be converted to the report's local time.");
            }
        }
    }
}

