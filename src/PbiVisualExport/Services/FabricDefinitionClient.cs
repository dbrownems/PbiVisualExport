using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PbiVisualExport.Auth;

namespace PbiVisualExport.Services;

/// <summary>One file of a Fabric item definition (path + base64 payload).</summary>
internal sealed record DefinitionPart(string Path, string Payload)
{
    public string DecodeText() => Encoding.UTF8.GetString(Convert.FromBase64String(Payload));
}

/// <summary>
/// Calls a Fabric item <c>getDefinition</c> endpoint (semantic models, reports) and
/// returns its parts, following the long-running-operation protocol when the service
/// answers 202 (poll the Location header, then GET <c>{location}/result</c>).
/// </summary>
internal sealed class FabricDefinitionClient
{
    private const string FabricApi = "https://api.fabric.microsoft.com";

    private readonly PbiTokenProvider _tokens;
    private readonly IHttpClientFactory _httpFactory;

    public FabricDefinitionClient(PbiTokenProvider tokens, IHttpClientFactory httpFactory)
    {
        _tokens = tokens;
        _httpFactory = httpFactory;
    }

    /// <param name="itemPath">e.g. <c>semanticModels/{id}</c> or <c>reports/{id}</c>.</param>
    /// <param name="format">Optional <c>format</c> query value; null returns the item's own format.</param>
    public async Task<IReadOnlyList<DefinitionPart>> GetDefinitionAsync(Guid workspaceId, string itemPath, string? format, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient();
        var token = await _tokens.GetFabricTokenAsync(ct);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var url = $"{FabricApi}/v1/workspaces/{workspaceId}/{itemPath}/getDefinition"
                  + (format is null ? "" : "?format=" + Uri.EscapeDataString(format));
        using var resp = await http.PostAsync(url, new StringContent("{}", Encoding.UTF8, "application/json"), ct);

        JsonElement definition;
        if (resp.StatusCode == System.Net.HttpStatusCode.Accepted)
        {
            definition = await PollLongRunningOperationAsync(http, resp, ct);
        }
        else
        {
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"getDefinition for {itemPath} returned {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}",
                    null, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            definition = doc.RootElement.GetProperty("definition").Clone();
        }

        return definition.GetProperty("parts").EnumerateArray()
            .Select(p => new DefinitionPart(p.GetProperty("path").GetString()!, p.GetProperty("payload").GetString() ?? ""))
            .ToList();
    }

    private static async Task<JsonElement> PollLongRunningOperationAsync(HttpClient http, HttpResponseMessage accepted, CancellationToken ct)
    {
        var location = accepted.Headers.Location?.ToString()
                       ?? (accepted.Headers.TryGetValues("Location", out var v) ? v.FirstOrDefault() : null)
                       ?? throw new InvalidOperationException("getDefinition returned 202 without a Location header.");

        for (var attempt = 0; attempt < 60; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            using var status = await http.GetAsync(location, ct);
            status.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await status.Content.ReadAsStringAsync(ct));
            var state = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (string.Equals(state, "Succeeded", StringComparison.OrdinalIgnoreCase))
            {
                var resultUrl = location.TrimEnd('/') + "/result";
                using var result = await http.GetAsync(resultUrl, ct);
                result.EnsureSuccessStatusCode();
                using var rdoc = JsonDocument.Parse(await result.Content.ReadAsStringAsync(ct));
                return rdoc.RootElement.GetProperty("definition").Clone();
            }
            if (string.Equals(state, "Failed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("getDefinition long-running operation failed: " + doc.RootElement);
        }
        throw new TimeoutException("getDefinition long-running operation did not complete in time.");
    }
}
