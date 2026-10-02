using System.Globalization;
using System.Text.Json;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiVisualExport.Services;

internal sealed partial class DaxQueryBuilder
{
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
    /// <paramref name="useReportMeasure"/> returns true for a report measure and records it as used.
    /// </summary>
    private static FilterSet BuildFilters(IEnumerable<JsonElement> filters, TOM.Model model, List<MeasureField> measures,
        Func<string, bool> useReportMeasure, ClientTime time)
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
                if (IsTopN(filter)) AddTopN(filter, target, model, useReportMeasure, set);
                else if (IsMeasureTarget(target)) AddValueFilter(filter, target, model, measures, useReportMeasure, set, time);
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
    private static string? MeasureTargetExpression(JsonElement target, TOM.Model model, Func<string, bool> useReportMeasure)
    {
        if (Str(target, "measure") is { Length: > 0 } name)
        {
            if (FindMeasure(model, name) is null && !useReportMeasure(name))
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
        Func<string, bool> useReportMeasure, FilterSet set, ClientTime time)
    {
        string Literal(JsonElement v) => MeasureLiteral(v, time);
        if (!HasConditions(filter)) return; // a filter card with nothing set
        var expr = MeasureTargetExpression(target, model, useReportMeasure)
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
    private static void AddTopN(JsonElement filter, JsonElement target, TOM.Model model, Func<string, bool> useReportMeasure,
        FilterSet set)
    {
        var count = TopNCount(filter);
        if (count <= 0 || !filter.TryGetProperty("orderBy", out var orderBy) || orderBy.ValueKind != JsonValueKind.Object)
            return; // not set
        var order = MeasureTargetExpression(orderBy, model, useReportMeasure);
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
}
