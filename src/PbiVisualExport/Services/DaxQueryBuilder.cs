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
    private readonly TimeProvider clock;

    /// <param name="clock">The clock relative date/time filters are evaluated against (UTC).</param>
    public DaxQueryBuilder(TimeProvider? clock = null) => this.clock = clock ?? TimeProvider.System;

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
            else if (f.IsHierarchyLevel)
            {
                if (FindHierarchyLevelColumn(model, f.Table, f.Hierarchy!, f.HierarchyLevel!) is null)
                    list.Add(new UnresolvedField(LevelLabel(f.Table, f.Hierarchy!, f.HierarchyLevel!), null, false));
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
        // Columns and hierarchy levels of active column, hierarchy and tuple filters: a (stale) model
        // without them can't translate the filter, or know that it sets a dynamic M parameter.
        foreach (var target in request.Filters.SelectMany(ActiveFilterTargets))
        {
            var table = Str(target, "table");
            if (string.IsNullOrEmpty(table)) continue;
            string label;
            if (Str(target, "column") is { Length: > 0 } column)
            {
                if (FindColumn(model, table, column) is not null) continue;
                label = ColRef(table, column);
            }
            else if (Str(target, "hierarchy") is { Length: > 0 } hierarchy && Str(target, "hierarchyLevel") is { Length: > 0 } level)
            {
                if (FindHierarchyLevelColumn(model, table, hierarchy, level) is not null) continue;
                label = TargetLabel(target);
            }
            else continue;
            if (!list.Any(u => Eq(u.Label, label))) list.Add(new UnresolvedField(label, null, false));
        }
        return list;
    }

    public DaxPlan Build(VisualExportRequest request, TOM.Model model, ReportDefinition? report = null, IEnumerable<string>? warningsSoFar = null)
    {
        var dims = new List<DimField>();
        var measures = new List<MeasureField>();
        var items = new List<OutputItem>();
        var usedReportMeasures = new List<ReportMeasure>();
        var warnings = new List<string>(warningsSoFar ?? Array.Empty<string>());
        request = ResolveHierarchyLevels(request, model);

        // A measure not in the model may be a report-level measure (names are unique across both).
        ReportMeasure? UseReportMeasure(string name)
        {
            var rm = report?.FindMeasure(name);
            if (rm is not null && !usedReportMeasures.Contains(rm)) usedReportMeasures.Add(rm);
            return rm;
        }

        foreach (var f in request.Fields)
        {
            if (string.IsNullOrWhiteSpace(f.Table))
            {
                warnings.Add($"A field of the visual ({f.Role ?? "no role"}) names no table; it was not exported.");
                continue;
            }
            // Dropping a field the visual groups on would export the wrong grain.
            if (string.IsNullOrEmpty(f.Column) && string.IsNullOrEmpty(f.Measure))
                throw new ExportRequestException(
                    $"A field of the visual on table '{f.Table}' ({f.Role ?? "no role"}) is not a column, hierarchy level or " +
                    "measure the export can read. Export aborted.");
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
                    var expr = AggregationExpression(f.Aggregation!, f.Table, f.Column!)
                        ?? throw new ExportRequestException(
                            $"The visual aggregates '{f.Table}'[{f.Column}] with {f.Aggregation}, which the export can't compute. Export aborted.");
                    measures.Add(new MeasureField(f.DisplayName, expr, IsCount(f.Aggregation) ? null : col?.FormatString, f));
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
        var filters = BuildFilters(request.Filters, model, measures, name => UseReportMeasure(name) is not null,
            new ClientTime(request.TimeZone, clock.GetUtcNow().UtcDateTime));

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
            i = measures.FindIndex(m => m.Source is null && m.Expression == expr);
            if (i >= 0) return i;
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
            else
            {
                if (s.IsHierarchyLevel) warnings.Add($"Sort hierarchy level {s.Label} not found in the model; ignored.");
                continue;
            }
            labels.Add(s.Label);
        }
        description = keys.Count > 0 ? string.Join(", ", labels) + " (visual sort)" : "none";
        return keys;
    }

    internal static string NormalizeAggregation(string? aggregation) => ImplicitAggregations.Normalize(aggregation);

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

    private static bool IsCount(string? aggregation) => ImplicitAggregations.IsCount(aggregation);

    /// <summary>DAX for an implicit aggregation of a column (see <see cref="ImplicitAggregations.Dax"/>).</summary>
    private static string? AggregationExpression(string aggregation, string table, string column) =>
        ImplicitAggregations.Dax(aggregation, $"{QuoteTable(table)}[{EscBracket(column)}]");

    // ---- Filters ----

    /// <summary>
    /// Translates the report, page and visual filters and slicer states sent by the client.
    /// Column and hierarchy-level targets become filter tables, as do hierarchy slicer
    /// selections and tuple filters (several columns). Measure and implicit-measure targets
    /// (visual-level value filters such as "Total Sales is greater than 1000") and Top N
    /// filters are evaluated as Power BI does (see <see cref="FilterSet"/>). A filter on a
    /// column bound to a dynamic M query parameter also sets the parameter (<c>MPARAMETER</c>).
    /// Relative date/time filters ("in the last 3 months") become a date window computed from
    /// the UTC clock at export time (an approximation: the report's own anchor time and local
    /// date aren't reproduced). Any filter that can't be translated
    /// (an unsupported condition, a filter measure or hierarchy level that doesn't exist...)
    /// fails the export rather than export rows the visual excludes.
    /// </summary>
    private static FilterSet BuildFilters(IEnumerable<JsonElement> filters, TOM.Model model, List<MeasureField> measures,
        Func<string, bool> isReportMeasure, ClientTime time)
    {
        var set = new FilterSet();
        var bindings = ParameterBindings(model);
        var selections = new List<ParameterSelection>();
        foreach (var filter in filters)
        {
            if (filter.ValueKind != JsonValueKind.Object || !filter.TryGetProperty("target", out var target)) continue;
            if (target.ValueKind == JsonValueKind.Array)
            {
                try
                {
                    if (TranslateMultiColumnFilter(filter, target, model, bindings, time) is { } multi) set.Columns.Add(multi);
                }
                catch (ExportRequestException) { throw; }
                catch (Exception ex)
                {
                    throw new ExportRequestException(
                        $"The filter on {MultiTargetLabel(target)} could not be translated ({ex.Message}). " +
                        "Export aborted rather than ignoring it.");
                }
                continue;
            }
            if (target.ValueKind != JsonValueKind.Object) continue;
            try
            {
                if (IsTopN(filter)) AddTopN(filter, target, model, isReportMeasure, set);
                else if (IsMeasureTarget(target)) AddValueFilter(filter, target, model, measures, isReportMeasure, set, time);
                else if (ResolveTargetColumn(target, model) is { } column)
                {
                    // A filter on a dynamic M parameter's bound column also sets the parameter;
                    // Power BI keeps the column filter as well.
                    if (column.Column is { } boundColumn && bindings.TryGetValue(boundColumn, out var bound))
                        foreach (var b in bound) SelectParameterValues(filter, b, selections, time);
                    if (TranslateColumnFilter(filter, column, time) is { } arg) set.Columns.Add(arg);
                }
            }
            catch (ExportRequestException) { throw; }
            catch (Exception ex)
            {
                throw Untranslatable(target, ex.Message);
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

        if (IsRelative(filter))
        {
            if (RelativeActive(filter)) throw Unsupported($"is a relative {(IsRelativeTime(filter) ? "time" : "date")} filter");
            return;
        }

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

    /// <summary>A relative date (<c>filterType 4</c>) or relative time (<c>filterType 7</c>) filter.</summary>
    private static bool IsRelative(JsonElement filter)
    {
        var type = FilterTypeOf(filter);
        if (type is 4 or 7) return true;
        var schema = Str(filter, "$schema") ?? "";
        return schema.Contains("relativeDate", StringComparison.OrdinalIgnoreCase) ||
               schema.Contains("relativeTime", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRelativeTime(JsonElement filter)
        => FilterTypeOf(filter) == 7 ||
           (Str(filter, "$schema")?.Contains("relativeTime", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>A relative filter restricts rows only once its count is set (a cleared card has none).</summary>
    private static bool RelativeActive(JsonElement filter)
        => filter.TryGetProperty("timeUnitsCount", out var c) && c.ValueKind == JsonValueKind.Number &&
           c.TryGetDouble(out var n) && n > 0;

    private static readonly string[] RelativeOperators = ["InLast", "InThis", "InNext"];
    private static readonly string[] RelativeUnits =
        ["Days", "Weeks", "CalendarWeeks", "Months", "CalendarMonths", "Years", "CalendarYears", "Minutes", "Hours"];

    /// <summary>An enum value sent as its number or its name (powerbi-models enums are numeric).</summary>
    private static string? EnumName(JsonElement filter, string name, string[] names)
    {
        if (!filter.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
            return i >= 0 && i < names.Length ? names[i] : null;
        if (v.ValueKind == JsonValueKind.String)
            return names.FirstOrDefault(n => Eq(n, v.GetString()));
        return null;
    }

    /// <summary>
    /// The half-open <c>[start, end)</c> window of a relative date or time filter, from the UTC
    /// clock at export time (an approximation of the report's own evaluation, which uses its
    /// anchor time and local date). Weeks start on Sunday. Relative date:
    /// "last/next N days|weeks|months|years" are rolling periods ending (starting) today when
    /// <c>includeToday</c> (default true), else yesterday (tomorrow); calendar units are whole
    /// periods before, at or after the current one ("this" is always the calendar period).
    /// Relative time: minutes or hours back from (forward from) the current minute.
    /// </summary>
    private static (DateTime Start, DateTime End) RelativeWindow(JsonElement filter, bool isTime, DateTime utcNow, string colRef)
    {
        var kind = isTime ? "time" : "date";
        ExportRequestException Bad(string problem) =>
            new($"The relative {kind} filter on {colRef} {problem} Export aborted rather than export rows the visual excludes.");
        var op = EnumName(filter, "operator", RelativeOperators) ?? throw Bad("has an unknown operator.");
        var unit = EnumName(filter, "timeUnitType", RelativeUnits) ?? throw Bad("has an unknown time unit.");
        if (!filter.TryGetProperty("timeUnitsCount", out var countEl) || countEl.ValueKind != JsonValueKind.Number ||
            !countEl.TryGetInt32(out var n) || n <= 0)
            throw Bad("needs a whole, positive number of units.");
        var isTimeUnit = unit is "Minutes" or "Hours";
        if (isTime != isTimeUnit)
            throw Bad($"uses the unit {unit}, which a relative {kind} filter doesn't support.");

        if (isTime)
        {
            var anchor = new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, utcNow.Minute, 0, DateTimeKind.Unspecified);
            var step = unit == "Hours" ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(1);
            return op switch
            {
                "InLast" => (anchor - step * n, anchor),
                "InNext" => (anchor, anchor + step * n),
                _ => unit == "Hours"
                    ? (anchor.AddMinutes(-anchor.Minute), anchor.AddMinutes(-anchor.Minute).AddHours(1))
                    : (anchor, anchor.AddMinutes(1)),
            };
        }

        var today = DateTime.SpecifyKind(utcNow.Date, DateTimeKind.Unspecified);
        var includeToday = !filter.TryGetProperty("includeToday", out var it) || it.ValueKind != JsonValueKind.False;
        DateTime Add(DateTime d, int count) => unit switch
        {
            "Days" => d.AddDays(count),
            "Weeks" or "CalendarWeeks" => d.AddDays(7 * count),
            "Months" or "CalendarMonths" => d.AddMonths(count),
            _ => d.AddYears(count),
        };
        DateTime PeriodStart() => unit switch
        {
            "Days" => today,
            "Weeks" or "CalendarWeeks" => today.AddDays(-(int)today.DayOfWeek),
            "Months" or "CalendarMonths" => new DateTime(today.Year, today.Month, 1),
            _ => new DateTime(today.Year, 1, 1),
        };

        if (op == "InThis") { var p = PeriodStart(); return (p, Add(p, 1)); }

        if (unit.StartsWith("Calendar", StringComparison.Ordinal))
        {
            var p = PeriodStart();
            return op == "InLast" ? (Add(p, -n), p) : (Add(p, 1), Add(p, 1 + n));
        }

        // Rolling days, weeks, months, years.
        if (op == "InLast")
        {
            var end = includeToday ? today.AddDays(1) : today;
            return (Add(end, -n), end);
        }
        var start = includeToday ? today : today.AddDays(1);
        return (start, Add(start, n));
    }

    /// <summary>
    /// The columns and hierarchy levels active filters target: a column filter (basic values,
    /// advanced conditions or Top N), or each level of a hierarchy slicer selection or tuple
    /// filter. Measure targets and filters that select everything are skipped.
    /// </summary>
    private static IEnumerable<JsonElement> ActiveFilterTargets(JsonElement filter)
    {
        if (filter.ValueKind != JsonValueKind.Object || !filter.TryGetProperty("target", out var target)) yield break;
        if (target.ValueKind == JsonValueKind.Array)
        {
            if (!NonEmptyArray(filter, "hierarchyData") && !NonEmptyArray(filter, "values")) yield break;
            foreach (var t in target.EnumerateArray())
                if (t.ValueKind == JsonValueKind.Object && !IsMeasureTarget(t)) yield return t;
            yield break;
        }
        if (target.ValueKind != JsonValueKind.Object || IsMeasureTarget(target)) yield break;
        var active = IsTopN(filter)
            ? TopNCount(filter) > 0
            : HasConditions(filter) || (NonEmptyArray(filter, "values") && !Eq(Str(filter, "operator"), "All")) ||
              (IsRelative(filter) && RelativeActive(filter));
        if (active) yield return target;
    }

    private static bool NonEmptyArray(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0;

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
            return AggregationExpression(agg, table, column) ?? throw UnsupportedAggregation(target, agg);
        // An aggregation of a hierarchy level, e.g. Count of 'Orders'[Order Calendar].[Year].
        if (Str(target, "aggregationFunction") is { Length: > 0 } levelAgg && Str(target, "hierarchy") is { Length: > 0 } &&
            ResolveTargetColumn(target, model) is { Column: { } levelColumn })
            return AggregationExpression(levelAgg, levelColumn.Table.Name, levelColumn.Name) ?? throw UnsupportedAggregation(target, levelAgg);
        return null;
    }

    private static ExportRequestException UnsupportedAggregation(JsonElement target, string aggregation) =>
        new($"Filter on {TargetLabel(target)} uses the aggregation {aggregation}, which the export can't compute. Export aborted.");

    /// <summary>
    /// A visual-level filter on a measure's value, evaluated per row of the visual. A measure
    /// the visual shows reuses its [m_i] column; any other is a filter-only [vf_i] column.
    /// </summary>
    private static void AddValueFilter(JsonElement filter, JsonElement target, TOM.Model model, List<MeasureField> measures,
        Func<string, bool> isReportMeasure, FilterSet set, ClientTime time)
    {
        string Literal(JsonElement v) => MeasureLiteral(v, time);
        if (!HasConditions(filter)) return; // a filter card with nothing set
        var expr = MeasureTargetExpression(target, model, isReportMeasure)
            ?? throw Untranslatable(target, "its target isn't a measure or an aggregation of a column");

        var mi = measures.FindIndex(m => string.Equals(m.Expression, expr, StringComparison.OrdinalIgnoreCase));
        var vi = set.ValueMeasures.FindIndex(e => string.Equals(e, expr, StringComparison.OrdinalIgnoreCase));
        var colRef = mi >= 0 ? $"[m_{mi}]" : $"[vf_{(vi >= 0 ? vi : set.ValueMeasures.Count)}]";

        // The predicate filters the [m_i]/[vf_i] column; the description names the measure.
        if (JoinConditions(filter, Literal, cond => UnsupportedCondition(target, cond), colRef, expr) is not { } joined)
            return;

        if (mi < 0 && vi < 0) set.ValueMeasures.Add(expr);
        set.ValuePredicates.Add(joined.Count == 1 ? joined.On[0] : $"({joined.On[0]})");
        set.Descriptions.Add(joined.On[1]);
    }

    /// <summary>Top/Bottom N values of a column by a measure, ranked under the column filters.</summary>
    private static void AddTopN(JsonElement filter, JsonElement target, TOM.Model model, Func<string, bool> isReportMeasure,
        FilterSet set)
    {
        var count = TopNCount(filter);
        if (count <= 0 || !filter.TryGetProperty("orderBy", out var orderBy) || orderBy.ValueKind != JsonValueKind.Object)
            return; // not set
        var order = MeasureTargetExpression(orderBy, model, isReportMeasure);
        var column = IsMeasureTarget(target) ? null : ResolveTargetColumn(target, model);
        if (column is null || order is null)
            throw new ExportRequestException(
                $"The Top N filter on {TargetLabel(target)} by {TargetLabel(orderBy)} could not be translated. " +
                "Export aborted rather than export rows the visual excludes.");
        var bottom = Eq(Str(filter, "operator"), "Bottom");
        var colRef = column.Ref;
        set.TopN.Add(new TopNFilter(colRef, count, bottom, order));
        set.Descriptions.Add($"{(bottom ? "Bottom" : "Top")} {count} {colRef} by {order}");
    }

    private static ExportRequestException Untranslatable(JsonElement target, string reason) =>
        new($"The filter on {TargetLabel(target)} could not be translated ({reason}). " +
            "Export aborted rather than export rows the visual excludes.");

    private static ExportRequestException UnsupportedCondition(JsonElement target, JsonElement cond) =>
        Untranslatable(target, $"the condition '{CondOperator(cond) ?? "?"}' isn't supported");

    /// <summary>A condition's operator, string or numeric.</summary>
    private static string? CondOperator(JsonElement cond)
        => cond.ValueKind == JsonValueKind.Object && cond.TryGetProperty("operator", out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
            : null;

    /// <summary>The numeric filterType, or -1.</summary>
    private static int FilterTypeOf(JsonElement filter)
        => filter.TryGetProperty("filterType", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var n) ? n : -1;

    private static string TargetLabel(JsonElement target)
    {
        var table = Str(target, "table") ?? "?";
        if (Str(target, "measure") is { } m) return $"[{m}]";
        if (Str(target, "column") is { } c)
            return Str(target, "aggregationFunction") is { } a ? $"{a}({ColRef(table, c)})" : ColRef(table, c);
        if (Str(target, "hierarchy") is { } h)
        {
            var level = $"{QuoteTable(table)}[{EscBracket(h)}].[{EscBracket(Str(target, "hierarchyLevel") ?? "?")}]";
            return Str(target, "aggregationFunction") is { } a ? $"{a}({level})" : level;
        }
        return QuoteTable(table);
    }

    private static string MultiTargetLabel(JsonElement targets)
        => string.Join(", ", targets.EnumerateArray().Select(t => t.ValueKind == JsonValueKind.Object ? TargetLabel(t) : "?"));

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    /// <summary>A filter target resolved to a model column: its table, DAX reference and data type.</summary>
    private sealed record FilterColumn(string Table, string Ref, TOM.DataType DataType, TOM.Column? Column);

    /// <summary>
    /// The column a filter target refers to: a column target (<c>{table, column}</c>) as sent,
    /// or a hierarchy level (<c>{table, hierarchy, hierarchyLevel}</c>) resolved to the level's
    /// column. Null for other targets. A hierarchy level that doesn't exist fails the export:
    /// ignoring its filter would export rows the visual excludes.
    /// </summary>
    private static FilterColumn? ResolveTargetColumn(JsonElement target, TOM.Model model)
    {
        var table = Str(target, "table");
        if (string.IsNullOrEmpty(table)) return null;
        if (Str(target, "column") is { Length: > 0 } column)
        {
            var col = FindColumn(model, table, column);
            return new FilterColumn(table, ColRef(table, column), col?.DataType ?? TOM.DataType.String, col);
        }
        if (Str(target, "hierarchy") is not { Length: > 0 } hierarchy || Str(target, "hierarchyLevel") is not { Length: > 0 } level)
            return null;
        var levelColumn = FindHierarchyLevelColumn(model, table, hierarchy, level)
            ?? throw new ExportRequestException($"Hierarchy level {TargetLabel(target)} was not found in the semantic model.");
        return new FilterColumn(levelColumn.Table.Name, ColRef(levelColumn.Table.Name, levelColumn.Name), levelColumn.DataType, levelColumn);
    }

    /// <summary>
    /// The column of a hierarchy level: a user hierarchy of the table or, failing that, the
    /// auto date/time hierarchy of a date column of that name (the levels of the column's
    /// default variation hierarchy, in its hidden LocalDateTable).
    /// </summary>
    private static TOM.Column? FindHierarchyLevelColumn(TOM.Model model, string table, string hierarchy, string level)
        => FindHierarchyLevel(model, table, hierarchy, level)?.Column;

    private static TOM.Level? FindHierarchyLevel(TOM.Model model, string table, string hierarchy, string level)
    {
        var t = FindTable(model, table);
        if (t is null) return null;
        var h = t.Hierarchies.Find(hierarchy) ?? t.Hierarchies.FirstOrDefault(x => Eq(x.Name, hierarchy));
        if (h is null && (t.Columns.Find(hierarchy) ?? t.Columns.FirstOrDefault(c => Eq(c.Name, hierarchy))) is { } dateColumn)
            h = (dateColumn.Variations.FirstOrDefault(v => v.IsDefault) ?? dateColumn.Variations.FirstOrDefault())?.DefaultHierarchy;
        return h is null ? null : h.Levels.Find(level) ?? h.Levels.FirstOrDefault(x => Eq(x.Name, level));
    }

    private static string LevelLabel(string table, string hierarchy, string level)
        => $"{QuoteTable(table)}[{EscBracket(hierarchy)}].[{EscBracket(level)}]";

    /// <summary>
    /// The request with its hierarchy-level fields and sort keys replaced by their level's
    /// column (on the column's table: an auto date/time level lives in its date table),
    /// headed by the level name as in the visual and in native export. Levels the model
    /// doesn't have are left as they are (see <see cref="FindUnresolvedFields"/>).
    /// </summary>
    internal static VisualExportRequest ResolveHierarchyLevels(VisualExportRequest request, TOM.Model model)
    {
        if (!request.Fields.Any(f => f.IsHierarchyLevel) && !request.Sort.Any(s => s.IsHierarchyLevel)) return request;
        var fields = request.Fields.Select(f =>
            f.IsHierarchyLevel && FindHierarchyLevel(model, f.Table, f.Hierarchy!, f.HierarchyLevel!) is { Column: { } c } l
                ? new ExportField
                {
                    Table = c.Table.Name, Column = c.Name, Aggregation = f.Aggregation, Role = f.Role, Hidden = f.Hidden,
                    Header = !string.IsNullOrEmpty(f.Header) ? f.Header :
                             !string.IsNullOrEmpty(f.Aggregation) ? ImplicitAggregations.Header(f.Aggregation!, l.Name) : l.Name,
                }
                : f).ToList();
        var sort = request.Sort.Select(s =>
            s.IsHierarchyLevel && FindHierarchyLevelColumn(model, s.Table, s.Hierarchy!, s.HierarchyLevel!) is { } c
                ? new ExportSort { Table = c.Table.Name, Column = c.Name, Aggregation = s.Aggregation, Direction = s.Direction }
                : s).ToList();
        return request.WithFieldsAndSort(fields, sort);
    }

    private static string? TranslateColumnFilter(JsonElement filter, FilterColumn column, ClientTime time)
    {
        var colRef = column.Ref;
        var dataType = column.DataType;
        var schema = Str(filter, "$schema") ?? "";
        var filterType = FilterTypeOf(filter);
        const string aborted = "Export aborted rather than export rows the visual excludes.";

        // Relative date/time ("in the last 3 months"): a [start, end) window from the UTC clock.
        if (IsRelative(filter))
        {
            if (!RelativeActive(filter)) return null;
            var isTime = IsRelativeTime(filter);
            if (dataType != TOM.DataType.DateTime)
                throw new ExportRequestException(
                    $"The relative {(isTime ? "time" : "date")} filter on {colRef} targets a {dataType} column, not a date. {aborted}");
            var (start, end) = RelativeWindow(filter, isTime, time.UtcNow, colRef);
            return $"FILTER(ALL({colRef}), {colRef} >= {DateTimeLiteral(start)} && {colRef} < {DateTimeLiteral(end)})";
        }

        if (schema.EndsWith("#notSupported", StringComparison.OrdinalIgnoreCase))
            throw new ExportRequestException($"The filter on {colRef} is a filter type Power BI doesn't expose to the API. {aborted}");

        if (filterType == 3 || schema.Contains("includeExclude", StringComparison.OrdinalIgnoreCase))
        {
            if (!NonEmptyArray(filter, "values")) return null;
            throw new ExportRequestException(
                $"The filter on {colRef} includes or excludes specific data points, which the export can't apply. {aborted}");
        }

        // Basic filter: In / NotIn a set of values.
        if (filterType == 1 || schema.Contains("basic", StringComparison.OrdinalIgnoreCase) ||
            (filter.TryGetProperty("operator", out _) && filter.TryGetProperty("values", out _)))
        {
            var op = Str(filter, "operator") ?? "In";
            if (!filter.TryGetProperty("values", out var valuesEl) || valuesEl.ValueKind != JsonValueKind.Array)
                return null;
            if (Eq(op, "All")) return null; // "Select all" (or nothing selected): no restriction
            var literals = valuesEl.EnumerateArray().Select(v => DaxLiteral(dataType, v, time)).ToList();
            if (literals.Count == 0) return null;
            var set = "{" + string.Join(", ", literals) + "}";
            if (Eq(op, "NotIn")) return $"FILTER(ALL({colRef}), NOT({colRef} IN {set}))";
            if (Eq(op, "In")) return $"TREATAS({set}, {colRef})";
            throw new ExportRequestException(
                $"The filter on {colRef} could not be translated (the operator '{op}' isn't supported). {aborted}");
        }

        // Advanced filter: And/Or of comparison conditions.
        if (filterType == 0 || schema.Contains("advanced", StringComparison.OrdinalIgnoreCase) ||
            filter.TryGetProperty("conditions", out _))
        {
            var joined = JoinConditions(filter, v => DaxLiteral(dataType, v, time),
                cond => new ExportRequestException(
                    $"The filter on {colRef} could not be translated (the condition '{CondOperator(cond) ?? "?"}' isn't supported). {aborted}"),
                colRef);
            return joined is null ? null : $"FILTER(ALL({colRef}), {joined.On[0]})";
        }

        // Any other filter that selects something (identity, hierarchy...) can't be dropped.
        if (NonEmptyArray(filter, "values") || NonEmptyArray(filter, "conditions") ||
            NonEmptyArray(filter, "identityValues") || NonEmptyArray(filter, "hierarchyData"))
            throw new ExportRequestException(
                $"The filter on {colRef} (filter type {(filterType >= 0 ? filterType.ToString(CultureInfo.InvariantCulture) : schema)}) " +
                $"could not be translated. {aborted}");
        return null;
    }

    // ---- Filters on several columns: hierarchy slicer selections and tuple filters ----

    /// <summary>
    /// A filter on several columns: a hierarchy slicer's selection (filterType 9,
    /// <c>hierarchyData</c>) or a tuple filter (filterType 6, <c>values</c> = value tuples).
    /// Targets are columns or hierarchy levels, in level order. Null when it selects
    /// everything. Anything that can't be translated exactly is a 400: dropping the filter
    /// would export rows the visual excludes.
    /// </summary>
    private static string? TranslateMultiColumnFilter(JsonElement filter, JsonElement target, TOM.Model model,
        Dictionary<TOM.Column, List<ParameterBinding>> bindings, ClientTime time)
    {
        var label = MultiTargetLabel(target);
        var isHierarchy = filter.TryGetProperty("hierarchyData", out var hierarchyData) && hierarchyData.ValueKind == JsonValueKind.Array;
        var tuples = isHierarchy || !filter.TryGetProperty("values", out var v) || v.ValueKind != JsonValueKind.Array ? (JsonElement?)null : v;
        if (!isHierarchy && tuples is null) return null;
        if ((isHierarchy ? hierarchyData : tuples!.Value).GetArrayLength() == 0) return null;

        var columns = new List<FilterColumn>();
        foreach (var t in target.EnumerateArray())
        {
            var column = t.ValueKind == JsonValueKind.Object && !IsMeasureTarget(t) ? ResolveTargetColumn(t, model) : null;
            if (column is null)
                throw new ExportRequestException(
                    $"The filter on {label} targets {(t.ValueKind == JsonValueKind.Object ? TargetLabel(t) : t.ToString())}, " +
                    "which is not a column or hierarchy level of the semantic model.");
            if (column.Column is { } c && bindings.ContainsKey(c))
                throw new ExportRequestException(
                    $"The filter on {label} includes {column.Ref}, which is bound to a dynamic M query parameter. " +
                    "A hierarchy or multi-column filter can't set the parameter: filter the column on its own.");
            columns.Add(column);
        }
        if (columns.Count == 0) return null;
        return isHierarchy
            ? TranslateHierarchyFilter(hierarchyData, columns, label, time)
            : TranslateTupleFilter(filter, tuples!.Value, columns, label, time);
    }

    /// <summary>
    /// Tuple filter: <c>In</c> → <c>TREATAS({(v1, v2), …}, c1, c2)</c>; <c>NotIn</c> (sent by
    /// the client for a multi-column exclusion read from the bookmark) → every other combination.
    /// </summary>
    private static string TranslateTupleFilter(JsonElement filter, JsonElement tuples, List<FilterColumn> columns, string label, ClientTime time)
    {
        var op = Str(filter, "operator") ?? "In";
        var notIn = Eq(op, "NotIn");
        if (!notIn && !Eq(op, "In"))
            throw new ExportRequestException($"The filter on {label} uses operator '{op}', which is not supported.");
        var rows = new List<string>();
        foreach (var tuple in tuples.EnumerateArray())
        {
            if (tuple.ValueKind != JsonValueKind.Array || tuple.GetArrayLength() != columns.Count)
                throw new ExportRequestException($"The filter on {label} has a value that doesn't match its {columns.Count} column(s).");
            var literals = tuple.EnumerateArray()
                .Select((e, i) => DaxLiteral(columns[i].DataType, NodeValue(e, label), time)).ToList();
            rows.Add(literals.Count == 1 ? literals[0] : "(" + string.Join(", ", literals) + ")");
        }
        var set = DaxSet(rows.Distinct());
        var refs = string.Join(", ", columns.Select(c => c.Ref));
        if (!notIn) return $"TREATAS({set}, {refs})";
        var key = columns.Count == 1 ? refs : $"({refs})";
        return $"FILTER({AllColumns(columns)}, NOT({key} IN {set}))";
    }

    private enum NodeState { Inherited, Selected, NotSelected }

    private sealed record HierarchyNode(JsonElement Value, NodeState State, List<HierarchyNode> Children);

    /// <summary>
    /// Hierarchy slicer selection. Each node is a value of its level's column, Selected,
    /// NotSelected or Inherited (the parent's state, the default), with optional child nodes
    /// for the next level; values that aren't listed also take the parent's state. The
    /// shallowest explicit nodes give the root's state: Selected nodes pick values out of
    /// nothing; NotSelected nodes (after "Select all") remove them from everything. The
    /// result is <c>FILTER(ALL(levels), predicate)</c>.
    /// </summary>
    private static string? TranslateHierarchyFilter(JsonElement data, List<FilterColumn> columns, string label, ClientTime time)
    {
        var nodes = ParseHierarchyNodes(data, label);
        var top = new List<NodeState>();
        void Collect(List<HierarchyNode> level)
        {
            foreach (var n in level)
                if (n.State == NodeState.Inherited) Collect(n.Children);
                else top.Add(n.State);
        }
        Collect(nodes);
        if (top.Count == 0) return null;
        if (top.Distinct().Count() > 1)
            throw new ExportRequestException(
                $"The hierarchy filter on {label} both selects and deselects values at its top level; it can't be translated exactly.");
        var predicate = HierarchyPredicate(nodes, 0, top[0] == NodeState.NotSelected, columns, label, time);
        return predicate == "TRUE()" ? null : $"FILTER({AllColumns(columns)}, {predicate ?? "FALSE()"})";
    }

    private static List<HierarchyNode> ParseHierarchyNodes(JsonElement nodes, string label)
    {
        var list = new List<HierarchyNode>();
        if (nodes.ValueKind != JsonValueKind.Array) return list;
        foreach (var n in nodes.EnumerateArray())
        {
            var op = Str(n, "operator");
            var state = op is null || Eq(op, "Inherited") ? NodeState.Inherited
                : Eq(op, "Selected") ? NodeState.Selected
                : Eq(op, "NotSelected") ? NodeState.NotSelected
                : throw new ExportRequestException($"The hierarchy filter on {label} uses operator '{op}', which is not supported.");
            var children = n.ValueKind == JsonValueKind.Object && n.TryGetProperty("children", out var c)
                ? ParseHierarchyNodes(c, label) : new List<HierarchyNode>();
            list.Add(new HierarchyNode(NodeValue(n, label), state, children));
        }
        return list;
    }

    /// <summary>
    /// The rows a hierarchy selection keeps at one level (and below): null for none,
    /// <c>TRUE()</c> for all. <paramref name="selected"/> is the parent's state.
    /// </summary>
    private static string? HierarchyPredicate(List<HierarchyNode> nodes, int depth, bool selected,
        List<FilterColumn> columns, string label, ClientTime time)
    {
        if (nodes.Count == 0) return selected ? "TRUE()" : null;
        if (depth >= columns.Count)
            throw new ExportRequestException($"The hierarchy filter on {label} has more levels than its {columns.Count} column(s).");
        var column = columns[depth];
        var listed = new List<string>();
        var whole = new List<string>();    // selected with everything below
        var partial = new List<string>();  // selected in part
        foreach (var n in nodes)
        {
            var literal = DaxLiteral(column.DataType, n.Value, time);
            listed.Add(literal);
            var nodeSelected = n.State switch { NodeState.Selected => true, NodeState.NotSelected => false, _ => selected };
            var below = HierarchyPredicate(n.Children, depth + 1, nodeSelected, columns, label, time);
            if (below == "TRUE()") whole.Add(literal);
            else if (below is not null) partial.Add($"({column.Ref} IN {{{literal}}} && {below})");
        }
        var terms = new List<string>();
        if (selected)
        {
            if (whole.Count == listed.Count) return "TRUE()";
            terms.Add($"NOT({column.Ref} IN {DaxSet(listed.Distinct())})");
        }
        if (whole.Count > 0) terms.Add($"{column.Ref} IN {DaxSet(whole.Distinct())}");
        terms.AddRange(partial);
        return terms.Count switch { 0 => null, 1 => terms[0], _ => "(" + string.Join(" || ", terms) + ")" };
    }

    /// <summary>The value of a tuple element or hierarchy node (<c>{value, keyValues?}</c>), or a bare value.</summary>
    private static JsonElement NodeValue(JsonElement e, string label)
    {
        if (e.ValueKind != JsonValueKind.Object) return e;
        if (e.TryGetProperty("value", out var v)) return v;
        throw new ExportRequestException($"The filter on {label} has a value without 'value' (only key values), which is not supported.");
    }

    /// <summary>Every combination of the columns' values: <c>ALL</c> per table, cross-joined across tables.</summary>
    private static string AllColumns(List<FilterColumn> columns)
    {
        var alls = columns.DistinctBy(c => c.Ref, StringComparer.OrdinalIgnoreCase)
            .GroupBy(c => c.Table, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"ALL({string.Join(", ", g.Select(c => c.Ref))})").ToList();
        return alls.Count == 1 ? alls[0] : $"CROSSJOIN({string.Join(", ", alls)})";
    }

    private static string DaxSet(IEnumerable<string> literals) => "{" + string.Join(", ", literals) + "}";

    /// <summary>An advanced filter's set conditions, joined: <see cref="On"/>[i] is on the i-th subject.</summary>
    private sealed record JoinedConditions(int Count, string[] On);

    /// <summary>
    /// The set conditions of an advanced filter (on a column or a measure value) as predicates on
    /// each subject, joined with the filter's <c>logicalOperator</c> (And unless "Or"). Unset
    /// ("None") conditions are skipped. A condition that can't be translated throws
    /// <paramref name="unsupported"/>: dropping it would change the rows either way (And: more,
    /// Or: fewer). Null when no condition is set.
    /// </summary>
    private static JoinedConditions? JoinConditions(JsonElement filter, Func<JsonElement, string> literal,
        Func<JsonElement, Exception> unsupported, params string[] subjects)
    {
        if (!filter.TryGetProperty("conditions", out var conds) || conds.ValueKind != JsonValueKind.Array) return null;
        var preds = subjects.Select(_ => new List<string>()).ToArray();
        foreach (var cond in conds.EnumerateArray())
        {
            if (Eq(Str(cond, "operator"), "None")) continue;
            for (var i = 0; i < subjects.Length; i++)
                preds[i].Add(TranslateCondition(cond, subjects[i], literal) ?? throw unsupported(cond));
        }
        if (preds[0].Count == 0) return null;
        var joiner = Eq(Str(filter, "logicalOperator"), "Or") ? " || " : " && ";
        return new JoinedConditions(preds[0].Count, preds.Select(p => string.Join(joiner, p)).ToArray());
    }

    private static string? TranslateCondition(JsonElement cond, string colRef, Func<JsonElement, string> literal)
    {
        var op = Str(cond, "operator");
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
                if (value.ValueKind == JsonValueKind.String)
                    return NumericLiteral(dataType, value.GetString() ?? "") ?? QuoteString(value.GetString() ?? "");
                return QuoteString(value.ToString());

            case TOM.DataType.Boolean:
                if (value.ValueKind is JsonValueKind.True) return "TRUE()";
                if (value.ValueKind is JsonValueKind.False) return "FALSE()";
                return bool.TryParse(value.GetString(), out var b) ? (b ? "TRUE()" : "FALSE()") : "FALSE()";

            case TOM.DataType.DateTime:
                var raw = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
                if (time.TryParse(raw, out var dt)) return DateTimeLiteral(dt);
                return QuoteString(raw ?? "");

            default:
                return QuoteString(value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString());
        }
    }

    /// <summary>
    /// A numeric value sent as text (the client does that when a number doesn't fit a JS double),
    /// parsed as the column's type so it stays exact: a double would turn 9007199254741113 into
    /// …112. Null if it isn't a number.
    /// </summary>
    private static string? NumericLiteral(TOM.DataType dataType, string s)
    {
        var inv = CultureInfo.InvariantCulture;
        if (dataType == TOM.DataType.Int64 && long.TryParse(s, NumberStyles.Integer, inv, out var l))
            return l.ToString(inv);
        if (dataType == TOM.DataType.Decimal && decimal.TryParse(s, NumberStyles.Float, inv, out var m))
            return m.ToString(inv);
        return double.TryParse(s, NumberStyles.Float, inv, out var d) && double.IsFinite(d) ? d.ToString("R", inv) : null;
    }

    /// <summary><c>DATE(y, m, d)</c>, plus <c>+ TIME(h, m, s)</c> when the value has a time of day.</summary>
    internal static string DateTimeLiteral(DateTime dt)
    {
        var datePart = $"DATE({dt.Year}, {dt.Month}, {dt.Day})";
        if (dt.TimeOfDay == TimeSpan.Zero) return datePart;
        return $"({datePart} + TIME({dt.Hour}, {dt.Minute}, {dt.Second}))";
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
internal sealed class ClientTime(string? timeZoneId, DateTime? utcNow = null)
{
    /// <summary>The clock relative date/time filters are evaluated against (UTC).</summary>
    public DateTime UtcNow { get; } = utcNow ?? DateTime.UtcNow;

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

