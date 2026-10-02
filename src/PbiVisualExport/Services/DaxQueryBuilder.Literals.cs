using System.Globalization;
using System.Text.Json;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiVisualExport.Services;

internal sealed partial class DaxQueryBuilder
{
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
