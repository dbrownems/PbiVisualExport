using System.Globalization;
using System.Text.RegularExpressions;

namespace PbiVisualExport.Services;

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
