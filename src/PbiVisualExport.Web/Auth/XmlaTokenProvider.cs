using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using PbiVisualExport.Web.Options;

namespace PbiVisualExport.Web.Auth;

/// <summary>Thrown when the XMLA device-code sign-in has not been completed yet.</summary>
public sealed class XmlaSignInRequiredException : Exception
{
    public XmlaSignInRequiredException(string message, string? verificationUri, string? userCode) : base(message)
    {
        VerificationUri = verificationUri;
        UserCode = userCode;
    }

    public string? VerificationUri { get; }
    public string? UserCode { get; }
}

/// <summary>
/// Mints Power BI tokens for the XMLA endpoint (ADOMD.NET export queries).
///
/// The XMLA endpoint rejects Azure CLI tokens ("Authentication failed for all
/// authenticators") even though the REST APIs accept them, so in dev mode this uses
/// a separate device-code sign-in with the Analysis Services public client. The
/// sign-in is non-blocking: the first request starts it and gets an
/// <see cref="XmlaSignInRequiredException"/> carrying the device code; once the user
/// completes it the refresh token is kept in a DPAPI-protected MSAL cache (plus an
/// AuthenticationRecord on disk) so later requests and restarts are silent.
/// Service principal / master user modes simply reuse the main credential.
/// </summary>
public sealed class XmlaTokenProvider
{
    private static readonly TokenRequestContext Scope = new(new[] { PbiTokenProvider.PowerBiScope });
    private static readonly TimeSpan SignInWait = TimeSpan.FromSeconds(5);

    private readonly TokenCredential _credential;
    private readonly DeviceCodeCredential? _deviceCode;
    private readonly string _recordPath;
    private readonly ILogger<XmlaTokenProvider> _log;
    private readonly object _gate = new();
    private Task? _signIn;
    private DeviceCodeInfo? _pending;
    private volatile bool _signedIn;

    public XmlaTokenProvider(PbiTokenProvider main, IOptions<AzureAdOptions> options, ILogger<XmlaTokenProvider> log)
    {
        _log = log;
        var o = options.Value;
        _recordPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PbiVisualExport", $"xmla-auth-record-{o.XmlaClientId}.json");

        Mode = ResolveMode(o);
        if (Mode == "Shared")
        {
            _credential = main.Credential;
            _signedIn = true;
        }
        else
        {
            var record = LoadRecord();
            _deviceCode = new DeviceCodeCredential(new DeviceCodeCredentialOptions
            {
                TenantId = string.IsNullOrWhiteSpace(o.TenantId) ? null : o.TenantId,
                ClientId = o.XmlaClientId,
                AuthenticationRecord = record,
                TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = "PbiVisualExport.Xmla" },
                // Never block a request on interactive sign-in; SignInAsync handles it explicitly.
                DisableAutomaticAuthentication = true,
                DeviceCodeCallback = (info, _) =>
                {
                    _pending = info;
                    _log.LogWarning("XMLA DEVICE CODE: {Message}", info.Message);
                    return Task.CompletedTask;
                }
            });
            _credential = _deviceCode;
            _signedIn = record is not null;
        }
        _log.LogInformation("XMLA credential mode: {Mode}{Client}", Mode, Mode == "DeviceCode" ? $" (client {o.XmlaClientId})" : "");
    }

    /// <summary>Shared | DeviceCode</summary>
    public string Mode { get; }

    public bool SignedIn => _signedIn;
    public DeviceCodeInfo? PendingDeviceCode => _signedIn ? null : _pending;

    public async Task<AccessToken> GetTokenAsync(CancellationToken ct)
    {
        if (_signedIn)
        {
            try
            {
                return await _credential.GetTokenAsync(Scope, ct);
            }
            catch (AuthenticationRequiredException) when (_deviceCode is not null)
            {
                _log.LogWarning("Cached XMLA sign-in is no longer valid; a new device-code sign-in is required");
                _signedIn = false;
            }
        }

        Task signIn;
        lock (_gate)
        {
            if (_signIn is null || _signIn.IsFaulted || _signIn.IsCanceled || (_signIn.IsCompleted && !_signedIn))
            {
                _pending = null;
                _signIn = Task.Run(SignInAsync);
            }
            signIn = _signIn;
        }

        var finished = await Task.WhenAny(signIn, Task.Delay(SignInWait, ct));
        if (finished == signIn)
        {
            await signIn; // surfaces sign-in failures
            return await _credential.GetTokenAsync(Scope, ct);
        }
        ct.ThrowIfCancellationRequested();

        var p = _pending;
        throw new XmlaSignInRequiredException(
            p is null
                ? "XMLA sign-in is in progress; see the server log for the device code."
                : $"XMLA sign-in required. {p.Value.Message} Then run the export again.",
            p?.VerificationUri.ToString(), p?.UserCode);
    }

    private async Task SignInAsync()
    {
        var record = await _deviceCode!.AuthenticateAsync(Scope);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_recordPath)!);
            await using var fs = File.Create(_recordPath);
            await record.SerializeAsync(fs);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not persist the XMLA AuthenticationRecord to {Path}", _recordPath);
        }
        _signedIn = true;
        _pending = null;
        _log.LogInformation("XMLA sign-in completed for {User}", record.Username);
    }

    private AuthenticationRecord? LoadRecord()
    {
        if (!File.Exists(_recordPath)) return null;
        try
        {
            using var fs = File.OpenRead(_recordPath);
            return AuthenticationRecord.Deserialize(fs);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ignoring unreadable XMLA AuthenticationRecord at {Path}", _recordPath);
            return null;
        }
    }

    private static string ResolveMode(AzureAdOptions o)
    {
        switch ((o.XmlaAuthenticationMode ?? "Auto").Trim().ToLowerInvariant())
        {
            case "shared": return "Shared";
            case "devicecode": return "DeviceCode";
        }
        var main = (o.AuthenticationMode ?? "AzureCli").Trim().ToLowerInvariant();
        var usesCliClient = main == "azurecli" || (main == "devicecode" && string.IsNullOrWhiteSpace(o.ClientId));
        return usesCliClient ? "DeviceCode" : "Shared";
    }
}
