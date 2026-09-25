using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace PbiVisualExport.Services;

/// <summary>
/// Serves the browser client bundled into the assembly (built from <c>src/pbi-visual-export-js</c>),
/// so a host needs no npm step: <c>&lt;script src="/pbi-export/client.js"&gt;</c> (global
/// <c>PbiVisualExport</c>) or <c>import … from "/pbi-export/client.mjs"</c>. Its version always
/// matches the endpoints it calls.
/// </summary>
internal static class ClientScript
{
    public const string Iife = "PbiVisualExport.Client.pbi-visual-export.js";
    public const string Module = "PbiVisualExport.Client.pbi-visual-export.mjs";

    private sealed record Asset(byte[] Content, string ETag);

    private static readonly ConcurrentDictionary<string, Asset?> Assets = new();

    private static Asset? Load(string resource)
    {
        using var stream = typeof(ClientScript).Assembly.GetManifestResourceStream(resource);
        if (stream is null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();
        return new Asset(bytes, "\"" + Convert.ToHexString(SHA256.HashData(bytes))[..16] + "\"");
    }

    public static async Task Serve(HttpContext http, string resource)
    {
        http.Response.Headers[ExportHandler.ContractHeader] = ExportHandler.ContractVersion;
        var asset = Assets.GetOrAdd(resource, Load);
        if (asset is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // no-cache = revalidate each load (cheap 304), so a package upgrade is picked up at once.
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers.ETag = asset.ETag;
        http.Response.Headers.XContentTypeOptions = "nosniff";
        var inm = http.Request.Headers.IfNoneMatch;
        if (inm.Count > 0 && inm.Any(v => v is not null && v.Split(',').Any(t => t.Trim() == asset.ETag || t.Trim() == "*")))
        {
            http.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }
        http.Response.ContentType = "text/javascript; charset=utf-8";
        http.Response.ContentLength = asset.Content.Length;
        if (!HttpMethods.IsHead(http.Request.Method))
            await http.Response.Body.WriteAsync(asset.Content, http.RequestAborted);
    }
}
