using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiVisualExport.Services;

/// <summary>
/// The fields a field parameter can put in a visual. A visual bound to a field parameter
/// saves only the fields selected when the report was saved, plus the parameter column (its
/// <c>fieldParameters</c> entry). The full list is in the semantic model: the parameter
/// table's calculated partition, as Power BI Desktop writes it,
/// <c>{ ("Label", NAMEOF('Table'[Column]), 0), ... }</c>. Field parameters hold columns and
/// measures only; implicit aggregations aren't supported.
/// </summary>
internal static partial class FieldParameters
{
    /// <summary>
    /// The columns and measures the field parameter in <paramref name="table"/> offers. Empty
    /// if the table isn't in the model or isn't a field parameter (no column whose
    /// <c>ParameterMetadata</c> has kind 2). References the model can't resolve are skipped.
    /// </summary>
    public static IReadOnlyList<VisualFieldRef> Fields(TOM.Model model, string table)
    {
        var t = FindTable(model, table);
        if (t is null || !IsFieldParameter(t)) return Array.Empty<VisualFieldRef>();
        var fields = new List<VisualFieldRef>();
        foreach (TOM.Partition partition in t.Partitions)
        {
            if (partition.Source is not TOM.CalculatedPartitionSource { Expression: { } expression }) continue;
            foreach (var (refTable, name) in NameOfReferences(expression))
                if (Resolve(model, refTable, name) is { } field && !fields.Contains(field)) fields.Add(field);
        }
        return fields;
    }

    /// <summary>
    /// The references in a DAX expression's <c>NAMEOF(...)</c> calls: (table, name) for
    /// <c>'Table'[Name]</c> or <c>Table[Name]</c>, (null, name) for <c>[Name]</c>. String
    /// literals and comments are skipped, so a commented-out entry isn't a field.
    /// </summary>
    internal static IEnumerable<(string? Table, string Name)> NameOfReferences(string expression)
    {
        foreach (Match m in NameOfCall().Matches(CodeOnly(expression)))
        {
            var table = m.Groups["quoted"].Success ? m.Groups["quoted"].Value.Replace("''", "'")
                : m.Groups["table"].Success ? m.Groups["table"].Value : null;
            yield return (table, m.Groups["name"].Value.Replace("]]", "]"));
        }
    }

    // A column of the table, or a measure of the table; a measure anywhere when unqualified.
    private static VisualFieldRef? Resolve(TOM.Model model, string? table, string name)
    {
        if (table is null)
            return model.Tables.SelectMany(t => t.Measures).FirstOrDefault(m => Eq(m.Name, name)) is { } any
                ? new VisualFieldRef(VisualFieldKind.Measure, any.Table.Name, any.Name)
                : null;
        var t = FindTable(model, table);
        if (t is null) return null;
        if ((t.Columns.Find(name) ?? t.Columns.FirstOrDefault(c => Eq(c.Name, name))) is { } column)
            return new VisualFieldRef(VisualFieldKind.Column, t.Name, column.Name);
        return (t.Measures.Find(name) ?? t.Measures.FirstOrDefault(m => Eq(m.Name, name))) is { } measure
            ? new VisualFieldRef(VisualFieldKind.Measure, t.Name, measure.Name)
            : null;
    }

    // Power BI marks a field parameter with ParameterMetadata {"version":3,"kind":2} on its hidden column.
    private static bool IsFieldParameter(TOM.Table table) =>
        table.Columns.Any(c => c.ExtendedProperties.Find("ParameterMetadata") is TOM.JsonExtendedProperty { Value: { } json }
                               && Kind(json) == 2);

    private static int? Kind(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("kind", out var k)
                   && k.ValueKind == JsonValueKind.Number && k.TryGetInt32(out var kind) ? kind : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The expression without string literals and comments; quoted and bracketed names are kept.</summary>
    private static string CodeOnly(string dax)
    {
        var code = new StringBuilder(dax.Length);
        var i = 0;
        while (i < dax.Length)
        {
            var c = dax[i];
            var next = i + 1 < dax.Length ? dax[i + 1] : '\0';
            if (c is '\'' or '[')
            {
                var end = TokenEnd(dax, i, c == '[' ? ']' : '\'');
                code.Append(dax, i, end - i);
                i = end;
                continue;
            }
            if (c == '"') i = TokenEnd(dax, i, '"');
            else if (c == '/' && next == '*')
            {
                var close = dax.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? dax.Length : close + 2;
            }
            else if ((c == '/' && next == '/') || (c == '-' && next == '-'))
            {
                var eol = dax.IndexOf('\n', i);
                i = eol < 0 ? dax.Length : eol;
            }
            else
            {
                code.Append(c);
                i++;
                continue;
            }
            code.Append(' ');
        }
        return code.ToString();
    }

    // The index after the quote that closes the token opened at start (a doubled quote is an escaped one).
    private static int TokenEnd(string s, int start, char close)
    {
        var i = start + 1;
        while (i < s.Length)
        {
            if (s[i] != close) i++;
            else if (i + 1 < s.Length && s[i + 1] == close) i += 2;
            else return i + 1;
        }
        return s.Length;
    }

    private static TOM.Table? FindTable(TOM.Model model, string name) =>
        model.Tables.Find(name) ?? model.Tables.FirstOrDefault(t => Eq(t.Name, name));

    private static bool Eq(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\bNAMEOF\s*\(\s*(?:'(?<quoted>(?:[^']|'')+)'|(?<table>[\p{L}_][\p{L}\p{N}_]*))?\s*\[(?<name>(?:[^\]]|\]\])+)\]\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NameOfCall();
}
