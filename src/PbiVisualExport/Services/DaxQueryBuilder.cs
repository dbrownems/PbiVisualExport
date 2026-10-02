using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiVisualExport.Services;

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
internal sealed partial class DaxQueryBuilder
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
}
