using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace PbiVisualExport.Services;

internal enum VisualFieldKind { Column, Measure, Aggregation, HierarchyLevel }

/// <summary>A field in a visual's saved query (a PBIR <c>semanticQuery</c> field expression).</summary>
/// <param name="Property">The column or measure; for a hierarchy level (and an aggregation of one), the hierarchy.</param>
/// <param name="Aggregation">For <see cref="VisualFieldKind.Aggregation"/>: the function name (Sum, Avg, ...).</param>
/// <param name="Level">For a hierarchy level (or an aggregation of one): the level name.</param>
internal sealed record VisualFieldRef(VisualFieldKind Kind, string Entity, string Property, string? Aggregation = null, string? Level = null);

/// <summary>
/// A visual as saved in a PBIR report (<c>definition/pages/{page}/visuals/{visual}/visual.json</c>):
/// the fields bound to its data roles (<c>visual.query.queryState.*.projections</c> and
/// field parameters) and its saved sort (<c>visual.query.sortDefinition</c>).
/// </summary>
internal sealed class VisualDefinition
{
    // semanticQuery QueryAggregateFunction, by code.
    private static readonly string[] AggregateFunctions = ImplicitAggregations.ByCode;

    private VisualDefinition(string pageName, string name, string? visualType, List<VisualFieldRef> fields, List<VisualFieldRef> sortFields)
    {
        PageName = pageName;
        Name = name;
        VisualType = visualType;
        Fields = fields;
        SortFields = sortFields;
    }

    public string PageName { get; }
    public string Name { get; }
    public string? VisualType { get; }

    /// <summary>Fields bound to the visual's data roles (all roles, including tooltips).</summary>
    public IReadOnlyList<VisualFieldRef> Fields { get; }

    /// <summary>Fields in the visual's saved sort.</summary>
    public IReadOnlyList<VisualFieldRef> SortFields { get; }

    /// <summary>Parses a <c>visual.json</c>; null for a container without a visual (a visual group).</summary>
    public static VisualDefinition? Parse(string pageName, string folderName, string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !TryObject(root, "visual", out var visual)) return null;

        var fields = new List<VisualFieldRef>();
        var sort = new List<VisualFieldRef>();
        if (TryObject(visual, "query", out var query))
        {
            if (TryObject(query, "queryState", out var state))
                foreach (var role in state.EnumerateObject())
                {
                    if (role.Value.ValueKind != JsonValueKind.Object) continue;
                    AddFields(role.Value, "projections", "field", fields);
                    // A field parameter bound to the role: the parameter column itself.
                    AddFields(role.Value, "fieldParameters", "parameterExpr", fields);
                }
            if (TryObject(query, "sortDefinition", out var sortDefinition))
                AddFields(sortDefinition, "sort", "field", sort);
        }
        return new VisualDefinition(pageName, Str(root, "name") ?? folderName, Str(visual, "visualType"), fields, sort);
    }

    /// <summary>Whether the visual shows <paramref name="field"/> (same kind: a column, a hierarchy level, a measure, or the same aggregation of a column).</summary>
    public bool Shows(ExportField field) => Matches(Fields, field.Table, Target(field.Column, field.Hierarchy, field.HierarchyLevel),
        field.Measure, field.Aggregation);

    /// <summary>Whether <paramref name="sort"/> is a field of the visual or of its saved sort.</summary>
    public bool CanSortBy(ExportSort sort)
    {
        var target = Target(sort.Column, sort.Hierarchy, sort.HierarchyLevel);
        return Matches(Fields, sort.Table, target, sort.Measure, sort.Aggregation) ||
               Matches(SortFields, sort.Table, target, sort.Measure, sort.Aggregation);
    }

    // A column (Level null), or a hierarchy level (Property = the hierarchy). A level wins over a column.
    private static (string? Property, string? Level) Target(string? column, string? hierarchy, string? level) =>
        !string.IsNullOrEmpty(hierarchy) && !string.IsNullOrEmpty(level) ? (hierarchy, level) : (column, null);

    private static bool Matches(IEnumerable<VisualFieldRef> refs, string table, (string? Property, string? Level) target,
        string? measure, string? aggregation)
    {
        // Measure names are unique across the model and the report's measures: match by name.
        if (!string.IsNullOrEmpty(measure))
            return refs.Any(r => r.Kind == VisualFieldKind.Measure && Eq(r.Property, measure));
        if (string.IsNullOrEmpty(target.Property)) return false;
        bool Same(VisualFieldRef r) => Eq(r.Entity, table) && Eq(r.Property, target.Property) && Eq(r.Level, target.Level);
        if (!string.IsNullOrEmpty(aggregation))
        {
            var function = DaxQueryBuilder.NormalizeAggregation(aggregation);
            return refs.Any(r => r.Kind == VisualFieldKind.Aggregation && Same(r)
                                 && DaxQueryBuilder.NormalizeAggregation(r.Aggregation) == function);
        }
        var kind = target.Level is null ? VisualFieldKind.Column : VisualFieldKind.HierarchyLevel;
        return refs.Any(r => r.Kind == kind && Same(r));
    }

    private static void AddFields(JsonElement parent, string arrayName, string fieldName, List<VisualFieldRef> into)
    {
        if (!parent.TryGetProperty(arrayName, out var items) || items.ValueKind != JsonValueKind.Array) return;
        foreach (var item in items.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty(fieldName, out var f) && ParseField(f) is { } r)
                into.Add(r);
    }

    /// <summary>
    /// A <c>Column</c>, <c>Measure</c>, <c>HierarchyLevel</c> or <c>Aggregation</c> (of a
    /// column or hierarchy level) expression; null for anything else (visual calculations,
    /// ...), which the export can't produce either.
    /// </summary>
    internal static VisualFieldRef? ParseField(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (TryObject(e, "Column", out var column)) return Property(VisualFieldKind.Column, column);
        if (TryObject(e, "Measure", out var measure)) return Property(VisualFieldKind.Measure, measure);
        if (TryObject(e, "HierarchyLevel", out var level)) return HierarchyLevel(VisualFieldKind.HierarchyLevel, level);
        if (TryObject(e, "Aggregation", out var agg) && TryObject(agg, "Expression", out var aggExpr)
            && (TryObject(aggExpr, "Column", out var aggColumn) ? Property(VisualFieldKind.Aggregation, aggColumn)
                : TryObject(aggExpr, "HierarchyLevel", out var aggLevel) ? HierarchyLevel(VisualFieldKind.Aggregation, aggLevel)
                : null) is { } r)
        {
            string? function = null;
            if (agg.TryGetProperty("Function", out var fn))
            {
                if (fn.ValueKind == JsonValueKind.Number && fn.TryGetInt32(out var code) && code >= 0 && code < AggregateFunctions.Length)
                    function = AggregateFunctions[code];
                else if (fn.ValueKind == JsonValueKind.String)
                    function = fn.GetString();
            }
            return function is null ? null : r with { Aggregation = function };
        }
        return null;
    }

    // { "Expression": { "Hierarchy": { "Expression": <source>, "Hierarchy": "Calendar" } }, "Level": "Year" }, where
    // <source> is { "SourceRef": { "Entity": "Sales" } } for a user hierarchy, or, for a date column's auto
    // date/time hierarchy, { "PropertyVariationSource": { "Expression": { "SourceRef": ... }, "Property": "Order Date" } }:
    // then the hierarchy is named after the date column, as the embedding API reports it.
    private static VisualFieldRef? HierarchyLevel(VisualFieldKind kind, JsonElement e)
    {
        var level = Str(e, "Level");
        if (string.IsNullOrEmpty(level) || !TryObject(e, "Expression", out var expr) || !TryObject(expr, "Hierarchy", out var h)
            || !TryObject(h, "Expression", out var source)) return null;
        string? entity, hierarchy;
        if (TryObject(source, "PropertyVariationSource", out var variation))
        {
            hierarchy = Str(variation, "Property");
            entity = TryObject(variation, "Expression", out var vExpr) && TryObject(vExpr, "SourceRef", out var vSource) ? Str(vSource, "Entity") : null;
        }
        else
        {
            hierarchy = Str(h, "Hierarchy");
            entity = TryObject(source, "SourceRef", out var sourceRef) ? Str(sourceRef, "Entity") : null;
        }
        return string.IsNullOrEmpty(entity) || string.IsNullOrEmpty(hierarchy) ? null : new VisualFieldRef(kind, entity, hierarchy, Level: level);
    }

    // { "Expression": { "SourceRef": { "Entity": "Sales" (, "Schema": "extension") } }, "Property": "Total" }
    private static VisualFieldRef? Property(VisualFieldKind kind, JsonElement e)
    {
        var property = Str(e, "Property");
        var entity = TryObject(e, "Expression", out var expr) && TryObject(expr, "SourceRef", out var source) ? Str(source, "Entity") : null;
        return string.IsNullOrEmpty(property) || string.IsNullOrEmpty(entity) ? null : new VisualFieldRef(kind, entity, property);
    }

    private static bool TryObject(JsonElement e, string property, out JsonElement value) =>
        e.TryGetProperty(property, out value) && value.ValueKind == JsonValueKind.Object;

    private static string? Str(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Eq(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Outcome of <see cref="VisualFieldValidator.Validate"/>.</summary>
/// <param name="Request">The request to export: the original, or a copy without the sort keys that were dropped.</param>
/// <param name="Error">Why the request is refused (null = allowed), returned with <paramref name="StatusCode"/>.</param>
/// <param name="Stale">The refusal may be due to an outdated cached report definition (the visual or a field wasn't found).</param>
/// <param name="Checked">false = allowed without a check (PBIR-Legacy report under <see cref="FieldValidationMode.PbirOnly"/>).</param>
internal sealed record FieldValidationResult(
    VisualExportRequest Request, IReadOnlyList<string> Warnings, string? Error, int StatusCode, bool Stale, bool Checked);

/// <summary>
/// Checks an export request against the visual's saved definition
/// (<see cref="ExportOptions.FieldValidation"/>), so a caller can only export what the
/// visual shows: every output field must be one of the visual's fields (same column,
/// measure, or aggregation of a column). Sort keys outside the visual are dropped with a
/// warning. Filters aren't restricted: the embedding API can apply any filter to the live
/// visual, and filters only narrow the rows.
/// </summary>
internal static class VisualFieldValidator
{
    public static FieldValidationResult Validate(VisualExportRequest request, ReportDefinition report, FieldValidationMode mode)
    {
        var none = Array.Empty<string>();
        if (mode == FieldValidationMode.Off) return new(request, none, null, StatusCodes.Status200OK, false, Checked: false);

        if (!report.IsPbir)
        {
            if (mode == FieldValidationMode.PbirOnly) return new(request, none, null, StatusCodes.Status200OK, false, Checked: false);
            return Refuse(request,
                $"Report '{request.ReportName}' is in {report.Format} format, so the export can't check the requested fields " +
                "against the visual. Save the report in PBIR (enhanced report format) and republish it, or set " +
                $"{PbiVisualExportOptions.DefaultSectionName}:Export:FieldValidation to PbirOnly or Off.",
                StatusCodes.Status403Forbidden, stale: false);
        }

        if (string.IsNullOrWhiteSpace(request.PageName) || string.IsNullOrWhiteSpace(request.VisualName))
            return Refuse(request, "Invalid export request: pageName and visualName are required to check the fields against the visual.",
                StatusCodes.Status400BadRequest, stale: false);

        var title = string.IsNullOrWhiteSpace(request.VisualTitle) ? request.VisualName : request.VisualTitle;
        var visual = report.FindVisual(request.PageName!, request.VisualName!);
        if (visual is null)
            return Refuse(request,
                $"Visual '{title}' ({request.VisualName}) on page {request.PageName} is not in the published definition of report " +
                $"'{request.ReportName}'. Only saved visuals can be exported: save and republish the report (unsaved or " +
                "personalized visuals aren't supported).",
                StatusCodes.Status403Forbidden, stale: true);

        // The fields the query would use (see DaxQueryBuilder.Build): a table plus a measure, column or hierarchy level.
        var notShown = request.Fields
            .Where(f => !string.IsNullOrWhiteSpace(f.Table)
                        && (!string.IsNullOrEmpty(f.Measure) || !string.IsNullOrEmpty(f.Column) || f.IsHierarchyLevel))
            .Where(f => !visual.Shows(f))
            .Select(Label)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (notShown.Count > 0)
            return Refuse(request,
                $"The export asks for fields that visual '{title}' doesn't show in the published report '{request.ReportName}': " +
                $"{string.Join(", ", notShown)}. Save and republish the report if the visual changed.",
                StatusCodes.Status403Forbidden, stale: true);

        var warnings = new List<string>();
        if (request.ApplySort && request.Sort.Any(s => !string.IsNullOrWhiteSpace(s.Table) && !visual.CanSortBy(s)))
        {
            var kept = new List<ExportSort>();
            foreach (var s in request.Sort)
            {
                if (string.IsNullOrWhiteSpace(s.Table) || visual.CanSortBy(s)) kept.Add(s);
                else warnings.Add($"Sort on {s.Label} ignored: not a field of the visual.");
            }
            request = request.WithSort(kept);
        }
        return new(request, warnings, null, StatusCodes.Status200OK, false, Checked: true);
    }

    private static FieldValidationResult Refuse(VisualExportRequest request, string error, int status, bool stale) =>
        new(request, Array.Empty<string>(), error, status, stale, Checked: true);

    private static string Label(ExportField f)
    {
        var target = f.IsHierarchyLevel ? $"'{f.Table}'[{f.Hierarchy}].[{f.HierarchyLevel}]" : $"'{f.Table}'[{f.Column}]";
        return !string.IsNullOrEmpty(f.Measure) ? $"[{f.Measure}]" :
               !string.IsNullOrEmpty(f.Aggregation) ? $"{f.Aggregation}({target})" : target;
    }
}
