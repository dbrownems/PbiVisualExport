using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using PbiVisualExport.Auth;

namespace PbiVisualExport.Services;

/// <summary>
/// Implements the endpoints mapped by <see cref="PbiVisualExportEndpointRouteBuilderExtensions.MapPbiVisualExport"/>.
/// Error responses are JSON <c>{ error }</c>, plus <c>problems</c> (a list) on request-validation
/// 400s from <see cref="ExportRequestResolver"/>: 400 invalid or unexportable request, 403 not
/// allowed (allow-list, authorizer, field validation, another user's ticket), 404 unknown,
/// expired or used ticket, 415 not JSON, 429 too many concurrent exports, 503
/// <c>{ signInRequired, verificationUri, userCode }</c> when the XMLA device-code sign-in
/// (development) is pending, 500 otherwise.
/// A failure after the CSV has started streaming aborts the connection, so the client sees
/// a failed download rather than a silently truncated file.
/// </summary>
internal sealed class ExportHandler
{
    public const string ContractHeader = "X-PbiVisualExport-Contract";
    public const string ContractVersion = "1";

    internal static readonly JsonSerializerOptions Json = CreateJsonOptions();

    private readonly ExportRequestResolver _resolver;
    private readonly CsvExportService _csv;
    private readonly XmlaTokenProvider _xmlaTokens;
    private readonly DownloadTicketStore _tickets;
    private readonly ExportConcurrencyLimiter _limiter;
    private readonly IEffectiveIdentityProvider? _identities;
    private readonly IEnumerable<IExportAuditSink> _audit;
    private readonly PbiVisualExportOptions _options;
    private readonly ILogger<ExportHandler> _log;

    public ExportHandler(
        ExportRequestResolver resolver,
        CsvExportService csv,
        XmlaTokenProvider xmlaTokens,
        DownloadTicketStore tickets,
        ExportConcurrencyLimiter limiter,
        IEnumerable<IExportAuditSink> audit,
        IOptions<PbiVisualExportOptions> options,
        ILogger<ExportHandler> log,
        IEffectiveIdentityProvider? identities = null)
    {
        _resolver = resolver;
        _csv = csv;
        _xmlaTokens = xmlaTokens;
        _tickets = tickets;
        _limiter = limiter;
        _identities = identities;
        _audit = audit;
        _options = options.Value;
        _log = log;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    /// <summary><c>POST csv</c>: resolve, authorize and stream the CSV in the response.</summary>
    public async Task CsvAsync(HttpContext http)
    {
        Stamp(http);
        var (context, error) = await ReadAndResolveAsync(http);
        if (context is null)
        {
            await error!.ExecuteAsync(http);
            return;
        }
        var (userKey, userName) = UserOf(http);
        var identity = await GetIdentityAsync(context, http.RequestAborted);
        await StreamAsync(http, ExportTarget.From(context), identity, userKey, userName, "csv", BuildFileName(context.Request.VisualTitle), ticket: null);
    }

    /// <summary>
    /// <c>POST prepare</c>: validates the request, builds the DAX plan and checks the XMLA
    /// credential, then issues a one-time download ticket for <c>GET download/{ticket}</c>.
    /// </summary>
    public async Task<IResult> PrepareAsync(HttpContext http)
    {
        Stamp(http);
        var (context, error) = await ReadAndResolveAsync(http);
        if (context is null) return error!;
        var (userKey, userName) = UserOf(http);
        if (!_limiter.WouldAllow(userKey)) return TooManyExports();

        try
        {
            var identity = await GetIdentityAsync(context, http.RequestAborted);
            var plan = await _csv.PlanAsync(context.Request, http.RequestAborted);
            await _xmlaTokens.GetTokenAsync(http.RequestAborted);
            var fileName = BuildFileName(context.Request.VisualTitle);
            var ticket = _tickets.Issue(ExportTarget.From(context), identity, userKey, userName, fileName);
            return Results.Json(new
            {
                fileName,
                columns = plan.Columns.Count,
                sort = plan.SortDescription,
                reportMeasures = plan.ReportMeasures,
                measureFilters = plan.MeasureFilters,
                parameters = plan.Parameters.Select(p => p.ToString()),
                warnings = plan.Warnings,
                ticket = ticket.Id,
                ticketExpiresInSeconds = (int)_tickets.Lifetime.TotalSeconds,
                workspaceName = context.WorkspaceName,
                datasetId = context.DatasetId,
                datasetName = context.DatasetName,
                reportName = context.ReportName
            }, Json);
        }
        catch (ExportRequestException ex)
        {
            return Rejected(ex);
        }
        catch (XmlaSignInRequiredException ex)
        {
            return SignInRequired(ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Export pre-flight failed");
            return Error(StatusCodes.Status500InternalServerError, "Export failed: " + ex.Message);
        }
    }

    /// <summary>
    /// <c>GET download/{ticket}</c>: streams a prepared export with Content-Disposition, so a
    /// plain navigation (e.g. a hidden iframe) saves it without buffering it in the page.
    /// Anonymous: the ticket is an unguessable one-time capability. If the caller is signed
    /// in, it must be the user the ticket was issued to.
    /// </summary>
    public async Task DownloadAsync(HttpContext http, string id)
    {
        Stamp(http);
        http.Response.Headers.CacheControl = "no-store";
        var ticket = _tickets.Find(id);
        if (ticket is null || ticket.State == DownloadState.Expired)
        {
            await Error(StatusCodes.Status404NotFound, "This download link is unknown or has expired. Export the visual again.").ExecuteAsync(http);
            return;
        }
        var (callerKey, _) = UserOf(http);
        if (ticket.UserKey is { } owner && owner.StartsWith("user:", StringComparison.Ordinal)
            && callerKey.StartsWith("user:", StringComparison.Ordinal) && callerKey != owner)
        {
            await Error(StatusCodes.Status403Forbidden, "This download link was issued to a different user.").ExecuteAsync(http);
            return;
        }
        if (!ticket.TryRedeem())
        {
            await Error(StatusCodes.Status404NotFound, "This download link has already been used. Export the visual again.").ExecuteAsync(http);
            return;
        }
        await StreamAsync(http, ticket.Target, ticket.Identity, ticket.UserKey ?? callerKey, ticket.UserName, "download", ticket.FileName, ticket);
    }

    /// <summary><c>GET download/{ticket}/status</c>: <c>{ state, rows, error, fileName }</c>.</summary>
    public IResult Status(HttpContext http, string id)
    {
        Stamp(http);
        http.Response.Headers.CacheControl = "no-store";
        var t = _tickets.Find(id);
        if (t is null) return Error(StatusCodes.Status404NotFound, "Unknown download ticket.");
        return Results.Json(new { state = t.State, rows = Interlocked.Read(ref t.Rows), error = t.Error, fileName = t.FileName }, Json);
    }

    /// <summary><c>GET xmla-auth</c>: XMLA device-code sign-in status, polled by the client after a sign-in-required error.</summary>
    public IResult XmlaAuth(HttpContext http)
    {
        Stamp(http);
        if (_xmlaTokens.Mode != "DeviceCode") return Results.NotFound();
        var pending = _xmlaTokens.PendingDeviceCode;
        return Results.Json(new
        {
            mode = _xmlaTokens.Mode,
            signedIn = _xmlaTokens.SignedIn,
            message = pending?.Message,
            verificationUri = pending?.VerificationUri.ToString(),
            userCode = pending?.UserCode
        }, Json);
    }

    /// <summary>
    /// <c>POST bench</c> (only when <see cref="ExportOptions.EnableBenchmark"/>): runs the same query
    /// as <c>csv</c> and reads every value without writing CSV. Returns timings.
    /// </summary>
    public async Task<IResult> BenchAsync(HttpContext http)
    {
        Stamp(http);
        if (!_options.Export.EnableBenchmark) return Results.NotFound();
        var (context, error) = await ReadAndResolveAsync(http);
        if (context is null) return error!;
        var (userKey, _) = UserOf(http);
        using var lease = _limiter.TryAcquire(userKey);
        if (lease is null) return TooManyExports();
        try
        {
            var identity = await GetIdentityAsync(context, http.RequestAborted);
            return Results.Json(await _csv.ReadOnlyAsync(context.Request, identity, http.RequestAborted), Json);
        }
        catch (ExportRequestException ex)
        {
            return Rejected(ex);
        }
        catch (XmlaSignInRequiredException ex)
        {
            return SignInRequired(ex);
        }
    }

    private async Task StreamAsync(HttpContext http, ExportTarget target, EffectiveIdentity? identity, string userKey, string? userName,
        string channel, string fileName, DownloadTicket? ticket)
    {
        var response = http.Response;
        var ct = http.RequestAborted;
        using var lease = _limiter.TryAcquire(userKey);
        if (lease is null)
        {
            ticket?.Finish(DownloadState.Failed, TooManyExportsMessage);
            await TooManyExports().ExecuteAsync(http);
            return;
        }

        var started = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        long streamed = 0;
        var outcome = VisualExportOutcome.Failed;
        string? failure = null;
        try
        {
            response.ContentType = "text/csv; charset=utf-8";
            var disposition = new ContentDispositionHeaderValue("attachment");
            disposition.SetHttpFileName(fileName); // ASCII filename= plus UTF-8 filename*=
            response.Headers.ContentDisposition = disposition.ToString();
            response.Headers.CacheControl = "no-store";
            response.Headers.XContentTypeOptions = "nosniff";

            await _csv.ExportAsync(target.Request, response.Body, identity, n =>
            {
                Interlocked.Exchange(ref streamed, n);
                if (ticket is not null) Interlocked.Exchange(ref ticket.Rows, n);
            }, ct);
            outcome = VisualExportOutcome.Completed;
            ticket?.Finish(DownloadState.Completed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = VisualExportOutcome.Canceled;
            failure = "The download was canceled.";
            ticket?.Finish(DownloadState.Failed, failure);
        }
        catch (ExportRequestException ex) when (!response.HasStarted)
        {
            failure = ex.Message;
            ticket?.Finish(DownloadState.Failed, failure);
            response.Clear();
            await Rejected(ex).ExecuteAsync(http);
        }
        catch (XmlaSignInRequiredException ex) when (!response.HasStarted)
        {
            failure = ex.Message;
            ticket?.Finish(DownloadState.Failed, failure);
            response.Clear();
            await SignInRequired(ex).ExecuteAsync(http);
        }
        catch (Exception ex) when (!response.HasStarted)
        {
            _log.LogError(ex, "Export failed before streaming started");
            failure = "Export failed: " + ex.Message;
            ticket?.Finish(DownloadState.Failed, failure);
            response.Clear();
            await Error(StatusCodes.Status500InternalServerError, failure).ExecuteAsync(http);
        }
        catch (Exception ex)
        {
            // Headers are gone; rethrowing aborts the connection so the client sees a failed
            // download rather than a silently truncated CSV.
            _log.LogError(ex, "Export failed mid-stream");
            failure = "Export failed mid-stream: " + ex.Message;
            ticket?.Finish(DownloadState.Failed, failure);
            throw;
        }
        finally
        {
            await AuditAsync(new VisualExportAuditRecord(
                started, sw.Elapsed, userName, channel,
                target.WorkspaceId, target.WorkspaceName, target.DatasetId, target.DatasetName, target.ReportId, target.ReportName,
                target.Request.PageName, target.Request.VisualName, target.Request.VisualTitle,
                target.Request.Fields.Count, Interlocked.Read(ref streamed),
                identity?.UserName, outcome, failure));
        }
    }

    private async Task<(VisualExportContext? Context, IResult? Error)> ReadAndResolveAsync(HttpContext http)
    {
        if (!http.Request.HasJsonContentType())
            return (null, Error(StatusCodes.Status415UnsupportedMediaType, "Content-Type must be application/json."));

        VisualExportRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<VisualExportRequest>(Json, http.RequestAborted);
        }
        catch (JsonException ex)
        {
            return (null, Error(StatusCodes.Status400BadRequest, "Invalid export request: the body is not valid JSON (" + ex.Message + ")."));
        }
        if (request is null)
            return (null, Error(StatusCodes.Status400BadRequest, "Invalid export request: the body is empty."));

        try
        {
            var result = await _resolver.ResolveAsync(http, request, http.RequestAborted);
            if (result.Context is not null) return (result.Context, null);
            if (result.StatusCode == StatusCodes.Status403Forbidden)
                _log.LogWarning("Export denied for {User}: {Error}", UserOf(http).Name ?? "anonymous", result.Error);
            object body = result.Problems.Count > 0
                ? new { error = result.Error, problems = result.Problems }
                : new { error = result.Error };
            return (null, Results.Json(body, Json, statusCode: result.StatusCode));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Could not resolve the export request");
            return (null, Error(StatusCodes.Status500InternalServerError, "Export failed: " + ex.Message));
        }
    }

    private async Task<EffectiveIdentity?> GetIdentityAsync(VisualExportContext context, CancellationToken ct) =>
        _identities is null ? null : await _identities.GetEffectiveIdentityAsync(context, ct);

    private async Task AuditAsync(VisualExportAuditRecord record)
    {
        foreach (var sink in _audit)
        {
            try
            {
                await sink.WriteAsync(record, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Export audit sink {Sink} failed", sink.GetType().Name);
            }
        }
    }

    /// <summary>A stable key for the calling user (concurrency limits, ticket ownership) and a display name.</summary>
    internal static (string Key, string? Name) UserOf(HttpContext http)
    {
        var u = http.User;
        if (u.Identity?.IsAuthenticated == true)
        {
            var id = u.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
                     ?? u.FindFirst("oid")?.Value
                     ?? u.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? u.FindFirst("sub")?.Value
                     ?? u.Identity.Name
                     ?? "authenticated";
            return ("user:" + id, u.Identity.Name ?? id);
        }
        return ("anon:" + http.Connection.RemoteIpAddress, null);
    }

    private const string TooManyExportsMessage = "Too many exports are running; wait for one to finish and try again.";

    private static IResult TooManyExports() => Error(StatusCodes.Status429TooManyRequests, TooManyExportsMessage);

    private static IResult SignInRequired(XmlaSignInRequiredException ex) =>
        Results.Json(new { error = ex.Message, signInRequired = true, verificationUri = ex.VerificationUri, userCode = ex.UserCode },
            Json, statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult Error(int status, string message) => Results.Json(new { error = message }, Json, statusCode: status);

    private IResult Rejected(ExportRequestException ex)
    {
        if (ex.StatusCode == StatusCodes.Status403Forbidden) _log.LogWarning("Export refused: {Error}", ex.Message);
        else _log.LogWarning("Export request rejected ({Status}): {Error}", ex.StatusCode, ex.Message);
        return Error(ex.StatusCode, ex.Message);
    }

    private static void Stamp(HttpContext http) => http.Response.Headers[ContractHeader] = ContractVersion;

    // Fixed (not Path.GetInvalidFileNameChars, which varies by OS) so names are the same on any host.
    private static readonly char[] InvalidFileNameChars =
        "\"<>|:*?\\/,".Concat(Enumerable.Range(0, 32).Select(i => (char)i)).ToArray();

    internal static string BuildFileName(string? visualTitle, DateTime? utcNow = null)
    {
        var baseName = string.IsNullOrWhiteSpace(visualTitle) ? "export" : visualTitle;
        foreach (var c in InvalidFileNameChars)
            baseName = baseName.Replace(c, '_');
        baseName = baseName.Trim();
        if (baseName.Length > 80) baseName = baseName[..80];
        return $"{baseName}-{utcNow ?? DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
    }
}
