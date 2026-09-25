using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PbiVisualExport.Services;

namespace PbiVisualExport;

public static class PbiVisualExportEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the export endpoints under <paramref name="prefix"/> (contract version
    /// <see cref="ExportHandler.ContractVersion"/>, sent as the <c>X-PbiVisualExport-Contract</c> header):
    /// <list type="bullet">
    /// <item><c>POST csv</c>: streams the visual's CSV (for fetch / programmatic callers).</item>
    /// <item><c>POST prepare</c>: validates and plans an export, returns a one-time download ticket.</item>
    /// <item><c>GET download/{ticket}</c>: streams the prepared CSV as an attachment (anonymous; the ticket is the capability).</item>
    /// <item><c>GET download/{ticket}/status</c>: progress and outcome of a ticket's download (anonymous).</item>
    /// <item><c>GET xmla-auth</c>: device-code sign-in status (only in XMLA DeviceCode mode).</item>
    /// <item><c>POST bench</c>: read-only perf baseline (only with Export:EnableBenchmark).</item>
    /// <item><c>GET client.js</c> / <c>client.mjs</c>: the browser client (anonymous).</item>
    /// </list>
    /// The group requires an authenticated user by default (<c>RequireAuthorization()</c>);
    /// chain <c>.RequireAuthorization("policy")</c> for a specific policy. JSON POSTs must be
    /// <c>application/json</c>, which cross-site forms cannot send without a CORS pre-flight.
    /// </summary>
    public static RouteGroupBuilder MapPbiVisualExport(this IEndpointRouteBuilder endpoints, string prefix = "/pbi-export")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup(prefix);
        group.RequireAuthorization();

        group.MapPost("csv", (HttpContext http, ExportHandler h) => h.CsvAsync(http));
        group.MapPost("prepare", (HttpContext http, ExportHandler h) => h.PrepareAsync(http));
        group.MapGet("download/{ticket}", (HttpContext http, string ticket, ExportHandler h) => h.DownloadAsync(http, ticket))
            .AllowAnonymous();
        group.MapGet("download/{ticket}/status", (HttpContext http, string ticket, ExportHandler h) => h.Status(http, ticket))
            .AllowAnonymous();
        group.MapGet("xmla-auth", (HttpContext http, ExportHandler h) => h.XmlaAuth(http));
        group.MapPost("bench", (HttpContext http, ExportHandler h) => h.BenchAsync(http));
        group.MapGet("client.js", (HttpContext http) => ClientScript.Serve(http, ClientScript.Iife)).AllowAnonymous();
        group.MapGet("client.mjs", (HttpContext http) => ClientScript.Serve(http, ClientScript.Module)).AllowAnonymous();
        return group;
    }
}
