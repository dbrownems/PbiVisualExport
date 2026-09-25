using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;

namespace PbiVisualExport;

/// <summary>A credential the host supplied via <see cref="PbiVisualExportBuilder.UseCredential"/>.</summary>
internal sealed record HostCredentials(TokenCredential Main, TokenCredential? Xmla);

/// <summary>
/// The identity the module uses: one <see cref="TokenCredential"/> that mints tokens for the
/// Power BI resource (REST lookups) and the Fabric resource (getDefinition metadata). The
/// XMLA data path uses its own provider, which reuses this credential except in local-dev
/// (Azure CLI) mode.
///
/// The credential is the host's (<see cref="PbiVisualExportBuilder.UseCredential"/>) when one
/// was supplied, otherwise it is built from <see cref="CredentialOptions"/>: AzureCli ("super
/// user" delegated, local development), ServicePrincipal (deployed), MasterUser or DeviceCode.
/// Hosts may reuse it, e.g. to generate embed tokens with the same identity.
/// </summary>
public sealed class PbiTokenProvider
{
    /// <summary>Power BI resource (REST, embed tokens, XMLA).</summary>
    public const string PowerBiScope = "https://analysis.windows.net/powerbi/api/.default";

    /// <summary>Fabric resource (item getDefinition).</summary>
    public const string FabricScope = "https://api.fabric.microsoft.com/.default";

    private readonly ILogger<PbiTokenProvider> _log;

    internal PbiTokenProvider(IOptions<PbiVisualExportOptions> options, HostCredentials? host, ILogger<PbiTokenProvider> log)
    {
        _log = log;
        if (host is not null)
        {
            Credential = host.Main;
            Mode = "Host";
            _log.LogInformation("Using the host-supplied TokenCredential ({Type})", host.Main.GetType().Name);
        }
        else
        {
            Mode = NormalizeMode(options.Value.Credential.Mode);
            Credential = Build(options.Value.Credential, Mode);
        }
    }

    /// <summary>Host | AzureCli | ServicePrincipal | MasterUser | DeviceCode</summary>
    public string Mode { get; }

    public TokenCredential Credential { get; }

    public async Task<string> GetTokenAsync(string scope, CancellationToken ct = default)
    {
        var token = await Credential.GetTokenAsync(new TokenRequestContext(new[] { scope }), ct);
        return token.Token;
    }

    public Task<string> GetPowerBiTokenAsync(CancellationToken ct = default) => GetTokenAsync(PowerBiScope, ct);

    public Task<string> GetFabricTokenAsync(CancellationToken ct = default) => GetTokenAsync(FabricScope, ct);

    internal static string NormalizeMode(string? mode) => (mode ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "azurecli" => "AzureCli",
        "serviceprincipal" => "ServicePrincipal",
        "masteruser" => "MasterUser",
        "devicecode" => "DeviceCode",
        _ => throw new InvalidOperationException(
            $"{PbiVisualExportOptions.DefaultSectionName}:Credential:Mode '{mode}' is not supported. " +
            "Use AzureCli, ServicePrincipal, MasterUser or DeviceCode.")
    };

    /// <summary>Configuration problems for the given credential settings (empty = valid).</summary>
    internal static IEnumerable<string> Validate(CredentialOptions o)
    {
        string mode;
        try { mode = NormalizeMode(o.Mode); }
        catch (InvalidOperationException ex) { return new[] { ex.Message }; }

        var required = mode switch
        {
            "ServicePrincipal" => new[] { (o.TenantId, "TenantId"), (o.ClientId, "ClientId"), (o.ClientSecret, "ClientSecret") },
            "MasterUser" => new[] { (o.TenantId, "TenantId"), (o.ClientId, "ClientId"), (o.Username, "Username"), (o.Password, "Password") },
            _ => Array.Empty<(string, string)>()
        };
        return required.Where(r => string.IsNullOrWhiteSpace(r.Item1)).Select(r => Missing(r.Item2, mode));
    }

    private static string Missing(string name, string mode)
    {
        var key = $"{PbiVisualExportOptions.DefaultSectionName}:Credential:{name}";
        return $"{key} is required for Credential:Mode {mode}. Set it in user secrets locally " +
               $"(dotnet user-secrets set {key} <value>) or the {key.Replace(":", "__")} environment variable when deployed.";
    }

    private TokenCredential Build(CredentialOptions o, string mode)
    {
        _log.LogInformation("Building TokenCredential in {Mode} mode", mode);
        var problems = Validate(o).ToList();
        if (problems.Count > 0) throw new InvalidOperationException(string.Join(" ", problems));

        switch (mode)
        {
            case "ServicePrincipal":
                return new ClientSecretCredential(o.TenantId, o.ClientId, o.ClientSecret);

            case "MasterUser":
#pragma warning disable CS0618 // ROPC is deprecated but still the documented master-user embedding flow.
                return new UsernamePasswordCredential(o.Username, o.Password, o.TenantId, o.ClientId);
#pragma warning restore CS0618

            case "DeviceCode":
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

            default:
                var cliOptions = new AzureCliCredentialOptions();
                if (!string.IsNullOrWhiteSpace(o.TenantId)) cliOptions.TenantId = o.TenantId;
                return new AzureCliCredential(cliOptions);
        }
    }
}
