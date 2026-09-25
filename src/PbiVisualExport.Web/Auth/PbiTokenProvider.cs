using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using PbiVisualExport.Web.Options;

namespace PbiVisualExport.Web.Auth;

/// <summary>
/// Builds a single <see cref="TokenCredential"/> from configuration and mints
/// tokens for the Power BI and Fabric resources: embedding / REST lookups and
/// getDefinition (metadata). The XMLA data path uses <see cref="XmlaTokenProvider"/>,
/// which reuses this credential except in dev (Azure CLI) mode.
///
/// Dev default is AzureCli ("super user" delegated). Switch AuthenticationMode
/// to ServicePrincipal for deployment, or MasterUser / DeviceCode as needed.
/// </summary>
public sealed class PbiTokenProvider
{
    // Power BI resource (embedding, REST, GenerateToken, XMLA).
    public const string PowerBiScope = "https://analysis.windows.net/powerbi/api/.default";

    // Fabric resource (semantic model getDefinition).
    public const string FabricScope = "https://api.fabric.microsoft.com/.default";

    private readonly TokenCredential _credential;
    private readonly ILogger<PbiTokenProvider> _log;

    public PbiTokenProvider(IOptions<AzureAdOptions> options, ILogger<PbiTokenProvider> log)
    {
        _log = log;
        _credential = Build(options.Value);
    }

    public TokenCredential Credential => _credential;

    public async Task<string> GetTokenAsync(string scope, CancellationToken ct = default)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext(new[] { scope }), ct);
        return token.Token;
    }

    public Task<string> GetPowerBiTokenAsync(CancellationToken ct = default) => GetTokenAsync(PowerBiScope, ct);

    public Task<string> GetFabricTokenAsync(CancellationToken ct = default) => GetTokenAsync(FabricScope, ct);

    private TokenCredential Build(AzureAdOptions o)
    {
        var mode = (o.AuthenticationMode ?? "AzureCli").Trim();
        _log.LogInformation("Building TokenCredential in {Mode} mode", mode);

        switch (mode.ToLowerInvariant())
        {
            case "serviceprincipal":
                Require(o.TenantId, "TenantId");
                Require(o.ClientId, "ClientId");
                Require(o.ClientSecret, "ClientSecret");
                return new ClientSecretCredential(o.TenantId, o.ClientId, o.ClientSecret);

            case "masteruser":
                Require(o.TenantId, "TenantId");
                Require(o.ClientId, "ClientId");
                Require(o.PbiUsername, "PbiUsername");
                Require(o.PbiPassword, "PbiPassword");
                return new UsernamePasswordCredential(o.PbiUsername, o.PbiPassword, o.TenantId, o.ClientId);

            case "devicecode":
                return new DeviceCodeCredential(new DeviceCodeCredentialOptions
                {
                    TenantId = string.IsNullOrWhiteSpace(o.TenantId) ? null : o.TenantId,
                    ClientId = string.IsNullOrWhiteSpace(o.ClientId) ? "04b07795-8ddb-461a-bbee-02f9e1bf7b46" : o.ClientId,
                    DeviceCodeCallback = (info, _) =>
                    {
                        _log.LogWarning("DEVICE CODE: {Message}", info.Message);
                        return Task.CompletedTask;
                    }
                });

            case "azurecli":
            default:
                var cliOptions = new AzureCliCredentialOptions();
                if (!string.IsNullOrWhiteSpace(o.TenantId)) cliOptions.TenantId = o.TenantId;
                return new AzureCliCredential(cliOptions);
        }
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"AzureAd:{name} is required for the selected AuthenticationMode.");
    }
}
