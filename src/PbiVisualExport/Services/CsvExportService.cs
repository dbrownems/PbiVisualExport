using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.AnalysisServices.AdomdClient;
using Microsoft.Extensions.Options;

namespace PbiVisualExport.Services;

/// <summary>
/// Orchestrates an export: load TOM metadata (cached), build one DAX query for the
/// visual, run it over the workspace XMLA endpoint (ADOMD.NET) and stream the rows
/// to the output as CSV while they arrive. XMLA has no row / value / byte caps like
/// the REST executeQueries API, so the entire result comes back from a single query.
///
/// The response is not started until the query has begun returning data, so query
/// errors still produce a clean HTTP error instead of a truncated file.
/// </summary>
internal sealed class CsvExportService
{
    private const int FlushEveryRows = 16_384;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly SemanticModelMetadataService _metadata;
    private readonly ReportDefinitionService _reports;
    private readonly DaxQueryBuilder _builder;
    private readonly XmlaConnectionFactory _xmla;
    private readonly ExportOptions _export;
    private readonly MetadataCacheOptions _cacheOptions;
    private readonly ILogger<CsvExportService> _log;

    public CsvExportService(
        SemanticModelMetadataService metadata,
        ReportDefinitionService reports,
        DaxQueryBuilder builder,
        XmlaConnectionFactory xmla,
        IOptions<PbiVisualExportOptions> options,
        ILogger<CsvExportService> log)
    {
        _metadata = metadata;
        _reports = reports;
        _builder = builder;
        _xmla = xmla;
        _export = options.Value.Export;
        _cacheOptions = options.Value.MetadataCache;
        _log = log;
    }

    /// <summary>
    /// Exports a resolved request (IDs and names present and consistent).
    /// <paramref name="onProgress"/> receives the running row count at each flush and at the end.
    /// </summary>
    public async Task<long> ExportAsync(VisualExportRequest request, Stream output, EffectiveIdentity? identity,
        Action<long>? onProgress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var gc = GcSnapshot.Take();
        using var q = await OpenQueryAsync(request, identity, sw, ct);
        var plan = q.Plan;
        var reader = q.Reader;
        var ordinals = q.Ordinals;

        long rows = 0;
        await using (var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 1 << 16, leaveOpen: true))
        {
            writer.NewLine = "\r\n"; // RFC 4180, on every OS
            await writer.WriteLineAsync(string.Join(",", plan.Columns.Select(c => CsvEscape(c.Header))));

            var line = new StringBuilder(512);
            while (reader.Read())
            {
                line.Clear();
                for (int i = 0; i < ordinals.Length; i++)
                {
                    if (i > 0) line.Append(',');
                    line.Append(CsvEscape(FormatValue(reader.GetValue(ordinals[i]))));
                }
                await writer.WriteLineAsync(line, ct);
                if (++rows % FlushEveryRows == 0)
                {
                    await writer.FlushAsync(ct);
                    onProgress?.Invoke(rows);
                }
            }
            await writer.FlushAsync(ct);
            onProgress?.Invoke(rows);
        }

        var secs = sw.Elapsed.TotalSeconds;
        _log.LogInformation(
            "Export complete: {Rows:N0} rows x {Cols} columns in {Sec:F1}s ({RowsPerSec:N0} rows/s; connect {Connect:F1}s, first data {First:F1}s), formatted={Formatted}, sort={Sort}{Capped}",
            rows, plan.Columns.Count, secs, rows / Math.Max(secs, 0.001), q.ConnectedAt.TotalSeconds, q.FirstDataAt.TotalSeconds,
            plan.ApplyFormatStrings, plan.SortDescription, _export.MaxRows > 0 && rows >= _export.MaxRows ? $" (MaxRows cap {_export.MaxRows:N0} reached)" : "");
        _log.LogDebug("Export GC (process-wide): {Gc}", GcSnapshot.Take().Since(gc));
        return rows;
    }

    /// <summary>
    /// Perf baseline (<c>POST {prefix}/bench</c>): runs exactly the export's query and reads
    /// every value, but writes nothing. The export should take about as long as this.
    /// </summary>
    public async Task<ReadBenchmarkResult> ReadOnlyAsync(VisualExportRequest request, EffectiveIdentity? identity, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var gc = GcSnapshot.Take();
        using var q = await OpenQueryAsync(request, identity, sw, ct);
        var reader = q.Reader;
        var ordinals = q.Ordinals;

        long rows = 0, chars = 0;
        while (reader.Read())
        {
            for (int i = 0; i < ordinals.Length; i++)
            {
                // Touch each value so nothing is skipped; FORMAT()-ed values are strings.
                var v = reader.GetValue(ordinals[i]);
                chars += v is string s ? s.Length : v is null or DBNull ? 0 : 1;
            }
            rows++;
            ct.ThrowIfCancellationRequested();
        }

        var total = sw.Elapsed;
        var g = GcSnapshot.Take().Since(gc);
        _log.LogInformation("Read-only benchmark: {Rows:N0} rows x {Cols} columns in {Sec:F1}s (connect {Connect:F1}s, first data {First:F1}s); {Gc}",
            rows, ordinals.Length, total.TotalSeconds, q.ConnectedAt.TotalSeconds, q.FirstDataAt.TotalSeconds, g);
        return new ReadBenchmarkResult(rows, ordinals.Length, chars, q.ConnectedAt.TotalSeconds, q.FirstDataAt.TotalSeconds,
            total.TotalSeconds, q.Plan.ApplyFormatStrings, q.Plan.SortDescription, g.AllocatedBytes, g.Gen0, g.Gen1, g.Gen2,
            GC.GetGCMemoryInfo().HeapSizeBytes);
    }

    /// <summary>
    /// Loads the (cached) model metadata and builds the DAX plan without touching XMLA.
    /// Used by the export itself and by the pre-flight check.
    ///
    /// Unless <see cref="ExportOptions.FieldValidation"/> is Off, the report definition is
    /// loaded (cached) and the requested fields are checked against the visual's saved
    /// definition first (<see cref="VisualFieldValidator"/>); a refusal throws an
    /// <see cref="ExportRequestException"/> with its status code (403 / 400).
    ///
    /// A measure the model doesn't have may be a report-level measure: then the report's
    /// <c>reportExtensions.json</c> measures are consulted, which requires a PBIR (enhanced
    /// format) report. A visual or fields not found trigger one re-download of the definitions
    /// involved (see <see cref="MetadataCacheOptions.RefreshOnMissSeconds"/>) and then an
    /// <see cref="ExportRequestException"/>.
    /// </summary>
    public async Task<DaxPlan> PlanAsync(VisualExportRequest request, CancellationToken ct)
    {
        var workspaceId = Guid.Parse(request.WorkspaceId!);
        var datasetId = Guid.Parse(request.DatasetId!);
        var reportId = Guid.Parse(request.ReportId!);
        TimeSpan? refreshAge = _cacheOptions.RefreshOnMissSeconds > 0 ? TimeSpan.FromSeconds(_cacheOptions.RefreshOnMissSeconds) : null;

        ReportDefinition? report = null;
        IReadOnlyList<string> validationWarnings = Array.Empty<string>();
        if (_export.FieldValidation != FieldValidationMode.Off)
        {
            report = await _reports.GetAsync(workspaceId, reportId, maxAge: null, ct);
            var check = VisualFieldValidator.Validate(request, report, _export.FieldValidation);
            if (check.Stale && refreshAge is { } age)
            {
                _log.LogInformation("Visual {Visual} or its fields not in the cached report definition; refreshing it if older than {MaxAge}",
                    request.VisualName, age);
                report = await _reports.GetAsync(workspaceId, reportId, age, ct);
                check = VisualFieldValidator.Validate(request, report, _export.FieldValidation);
            }
            if (check.Error is not null)
            {
                foreach (var e in report.VisualErrors) _log.LogWarning("Unreadable visual definition in report {ReportId}: {Error}", reportId, e);
                throw new ExportRequestException(check.Error, check.StatusCode);
            }
            if (!check.Checked)
                _log.LogWarning("Report {ReportId} is in {Format} format: exported fields not checked against the visual (FieldValidation=PbirOnly)",
                    reportId, report.Format);
            request = check.Request;
            validationWarnings = check.Warnings;
        }

        var model = await _metadata.GetModelAsync(workspaceId, datasetId, maxAge: null, ct);
        var unresolved = DaxQueryBuilder.FindUnresolvedFields(request, model);
        if (unresolved.Count == 0) return _builder.Build(request, model, report, validationWarnings);

        // Only measures can be report measures; an unknown column never needs the report.
        if (NeedsReport(unresolved)) report ??= await _reports.GetAsync(workspaceId, reportId, maxAge: null, ct);
        var missing = Missing(unresolved, report);
        if (missing.Count > 0 && refreshAge is { } maxAge)
        {
            _log.LogInformation("Fields not in cached metadata ({Missing}); refreshing definitions older than {MaxAge}",
                string.Join(", ", missing.Select(u => u.Label)), maxAge);
            model = await _metadata.GetModelAsync(workspaceId, datasetId, maxAge, ct);
            unresolved = DaxQueryBuilder.FindUnresolvedFields(request, model);
            if (NeedsReport(unresolved)) report = await _reports.GetAsync(workspaceId, reportId, maxAge, ct);
            missing = Missing(unresolved, report);
        }

        if (missing.Count > 0) throw UnresolvedError(request, missing, report);

        var plan = _builder.Build(request, model, report, validationWarnings);
        if (plan.ReportMeasures.Count > 0)
            _log.LogInformation("Report-level measures defined in the query: {Measures}", string.Join(", ", plan.ReportMeasures));
        return plan;
    }

    private static bool NeedsReport(List<UnresolvedField> unresolved) => unresolved.Any(u => u.MeasureName is not null);

    /// <summary>Unresolved fields (sort-only keys excluded: those just warn) that aren't report measures.</summary>
    private static List<UnresolvedField> Missing(List<UnresolvedField> unresolved, ReportDefinition? report)
        => unresolved
            .Where(u => !u.SortOnly && !(u.MeasureName is not null && report?.FindMeasure(u.MeasureName) is not null))
            .ToList();

    private static ExportRequestException UnresolvedError(VisualExportRequest request, List<UnresolvedField> missing, ReportDefinition? report)
    {
        var columns = missing.Where(u => u.MeasureName is null).Select(u => u.Label).ToList();
        var measures = missing.Where(u => u.MeasureName is not null).Select(u => u.Label).ToList();
        var parts = new List<string>();
        if (columns.Count > 0)
            parts.Add($"Columns not found in semantic model '{request.DatasetName}': {string.Join(", ", columns)}.");
        if (measures.Count > 0 && report is { IsPbir: false })
            parts.Add($"Measures not found in semantic model '{request.DatasetName}': {string.Join(", ", measures)}. " +
                      $"They may be report-level measures, which can only be read from a PBIR (enhanced format) report " +
                      $"definition, but report '{request.ReportName}' is in {report.Format} format. " +
                      "Save the report in PBIR format and republish it.");
        else if (measures.Count > 0)
            parts.Add($"Measures not found in semantic model '{request.DatasetName}' or in the report-level measures of " +
                      $"report '{request.ReportName}' ({ReportDefinition.ExtensionsPath}): {string.Join(", ", measures)}.");
        return new ExportRequestException(string.Join(" ", parts));
    }

    /// <summary>Builds the plan, connects and executes; the reader is positioned before the first row.</summary>
    private async Task<OpenQuery> OpenQueryAsync(VisualExportRequest request, EffectiveIdentity? identity, Stopwatch sw, CancellationToken ct)
    {
        var plan = await PlanAsync(request, ct);
        var dax = plan.BuildQuery(_export.MaxRows);
        _log.LogInformation("Export row order: {Sort}", plan.SortDescription);
        if (plan.MeasureFilters.Count > 0)
            _log.LogInformation("Measure filters: {Filters}", string.Join("; ", plan.MeasureFilters));
        if (plan.Parameters.Count > 0)
            _log.LogInformation("Dynamic M parameters: {Parameters}", string.Join("; ", plan.Parameters));
        foreach (var w in plan.Warnings) _log.LogWarning("Export: {Warning}", w);
        _log.LogDebug("Export query:\n{Dax}", dax);

        var q = new OpenQuery { Plan = plan };
        try
        {
            q.Connection = await _xmla.OpenAsync(request.WorkspaceName!, request.DatasetName!, identity, ct);
            q.ConnectedAt = sw.Elapsed;
            if (_export.ValidateParameterValues)
                foreach (var p in plan.Parameters.Where(p => !p.SelectAll)) await ValidateParameterAsync(q.Connection, p, ct);

            var cmd = q.Command = q.Connection.CreateCommand();
            cmd.CommandText = dax;
            cmd.CommandTimeout = Math.Max(0, _export.CommandTimeoutSeconds);
            q.Cancel = ct.Register(() =>
            {
                try { cmd.Cancel(); } catch { /* best effort */ }
            });

            q.Reader = await Task.Run(() => cmd.ExecuteReader(), ct);
            q.FirstDataAt = sw.Elapsed;
            q.Ordinals = MapOrdinals(q.Reader, plan);
            return q;
        }
        catch
        {
            q.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Rejects (400) M parameter values that aren't in the parameter's bound column, so the
    /// values that reach the model's M (and its source query) are ones the report offers.
    /// </summary>
    private static async Task ValidateParameterAsync(AdomdConnection connection, MParameterValue p, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"EVALUATE EXCEPT({{{string.Join(", ", p.Literals)}}}, VALUES({p.ColumnRef}))";
        var unknown = new List<string>();
        using (var reader = await Task.Run(() => cmd.ExecuteReader(), ct))
            while (reader.Read()) unknown.Add(DaxQueryBuilder.QuoteString(FormatValue(reader.GetValue(0))));
        if (unknown.Count > 0)
            throw new ExportRequestException(
                $"Dynamic M query parameter {p.Name} can only be set to values of {p.ColumnRef}; not found: {string.Join(", ", unknown)}.");
    }

    private sealed class OpenQuery : IDisposable
    {
        public required DaxPlan Plan { get; init; }
        public AdomdConnection? Connection { get; set; }
        public AdomdCommand? Command { get; set; }
        public CancellationTokenRegistration Cancel { get; set; }
        public AdomdDataReader Reader { get; set; } = null!;
        public int[] Ordinals { get; set; } = Array.Empty<int>();
        public TimeSpan ConnectedAt { get; set; }
        public TimeSpan FirstDataAt { get; set; }

        public void Dispose()
        {
            Reader?.Dispose();
            Cancel.Dispose();
            Command?.Dispose();
            Connection?.Dispose();
        }
    }

    /// <summary>Process-wide GC counters; deltas are per-export only when exports don't overlap.</summary>
    private readonly record struct GcSnapshot(long AllocatedBytes, int Gen0, int Gen1, int Gen2)
    {
        public static GcSnapshot Take() =>
            new(GC.GetTotalAllocatedBytes(false), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

        public GcSnapshot Since(GcSnapshot start) =>
            new(AllocatedBytes - start.AllocatedBytes, Gen0 - start.Gen0, Gen1 - start.Gen1, Gen2 - start.Gen2);

        public override string ToString() =>
            $"allocated {AllocatedBytes / 1048576.0:N0} MB, collections gen0 {Gen0} / gen1 {Gen1} / gen2 {Gen2}, heap after last GC {GC.GetGCMemoryInfo().HeapSizeBytes / 1048576.0:N0} MB";
    }

    /// <summary>Maps each output column alias ([c0], [c1], ...) to its reader ordinal.</summary>
    private static int[] MapOrdinals(AdomdDataReader reader, DaxPlan plan)
    {
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < reader.FieldCount; i++)
            byName[reader.GetName(i).Trim('[', ']')] = i;

        var ordinals = new int[plan.Columns.Count];
        for (int i = 0; i < ordinals.Length; i++)
        {
            if (!byName.TryGetValue(plan.Columns[i].Alias, out ordinals[i]))
                throw new InvalidOperationException(
                    $"Query result is missing column [{plan.Columns[i].Alias}] ({plan.Columns[i].Header}).");
        }
        return ordinals;
    }

    /// <summary>
    /// Culture-invariant text for a value. Formatted exports are already strings (DAX
    /// FORMAT()); raw exports keep full precision: integers/decimals exact, doubles
    /// round-trip, datetimes ISO-8601, booleans true/false, BLANK as empty.
    /// </summary>
    internal static string FormatValue(object? value) => value switch
    {
        null or DBNull => "",
        string s => s,
        bool b => b ? "true" : "false",
        long l => l.ToString(Inv),
        int n => n.ToString(Inv),
        decimal m => m.ToString("0.############################", Inv),
        double d => d.ToString("R", Inv),
        float f => f.ToString("R", Inv),
        DateTime dt => dt.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", Inv),
        IFormattable f => f.ToString(null, Inv),
        _ => value.ToString() ?? ""
    };

    private static string CsvEscape(string value)
    {
        if (value.AsSpan().IndexOfAny(",\"\n\r") < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
