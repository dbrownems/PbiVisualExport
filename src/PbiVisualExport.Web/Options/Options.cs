namespace PbiVisualExport.Web.Options;

public sealed class AzureAdOptions
{
    public const string SectionName = "AzureAd";

    /// <summary>AzureCli | ServicePrincipal | MasterUser | DeviceCode</summary>
    public string AuthenticationMode { get; set; } = "AzureCli";
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string PbiUsername { get; set; } = "";
    public string PbiPassword { get; set; } = "";

    /// <summary>
    /// Credential used for the XMLA endpoint (the export data path):
    ///  * Auto (default): DeviceCode when AuthenticationMode is AzureCli (or DeviceCode with no
    ///    ClientId) because the XMLA endpoint rejects Azure CLI tokens; otherwise Shared.
    ///  * Shared: reuse the main credential (ServicePrincipal / MasterUser / custom-app DeviceCode).
    ///  * DeviceCode: always sign in with <see cref="XmlaClientId"/> via device code.
    /// </summary>
    public string XmlaAuthenticationMode { get; set; } = "Auto";

    /// <summary>Public client used for XMLA device-code sign-in (default: the Analysis Services client ADOMD.NET itself uses).</summary>
    public string XmlaClientId { get; set; } = "cf710c6e-dfcc-4fa8-a093-d47294e44c66";
}

public sealed class PowerBIOptions
{
    public const string SectionName = "PowerBI";

    /// <summary>Required. The only workspace the app embeds from and exports (validated at startup).</summary>
    public string WorkspaceId { get; set; } = "";

    /// <summary>Optional default report; when empty the first report in the workspace is selected.</summary>
    public string ReportId { get; set; } = "";

    /// <summary>XMLA endpoint prefix; the workspace name is appended (e.g. powerbi://api.powerbi.com/v1.0/myorg/Sales).</summary>
    public string XmlaEndpoint { get; set; } = "powerbi://api.powerbi.com/v1.0/myorg";
}

public sealed class ExportOptions
{
    public const string SectionName = "Export";

    /// <summary>Hard cap on total exported rows (0 = unlimited). Applied in DAX via TOPN.</summary>
    public long MaxRows { get; set; } = 0;

    /// <summary>XMLA command timeout for the export query in seconds (0 = no timeout).</summary>
    public int CommandTimeoutSeconds { get; set; } = 0;

    /// <summary>
    /// Enables <c>POST /export/bench</c>, which runs an export's query and reads every
    /// value without writing CSV (the baseline for <c>infra/perf-export.ps1</c>). Off by
    /// default: it lets callers run arbitrary-sized queries without downloading anything.
    /// </summary>
    public bool EnableBenchmark { get; set; } = false;
}

/// <summary>
/// Controls caching of downloaded Fabric definitions — semantic-model (TOM) metadata and,
/// when a visual uses report-level measures, the report definition — so repeat export
/// requests do not re-hit the Fabric getDefinition API. The metadata rarely
/// changes, so it is cached in-process and (optionally) on disk across restarts.
/// </summary>
public sealed class MetadataCacheOptions
{
    public const string SectionName = "MetadataCache";

    /// <summary>When false, every request downloads fresh metadata (cache disabled).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long a cached model is considered fresh. A value &lt;= 0 means the cache
    /// never expires on its own (entries live until the process restarts or an
    /// explicit refresh is requested — and, with disk persistence, across restarts).
    /// </summary>
    public int TtlMinutes { get; set; } = 60;

    /// <summary>When true, downloaded definitions (TMSL, report) are also written to disk and reused after a restart.</summary>
    public bool PersistToDisk { get; set; } = true;

    /// <summary>
    /// When a visual references a field that neither the cached model nor the cached report
    /// defines, re-download both once if the cached copies are older than this (a redeploy may
    /// have added it), before failing. &lt;= 0 disables. Deployments normally recycle the app
    /// anyway; note the disk cache survives a restart.
    /// </summary>
    public int RefreshOnMissSeconds { get; set; } = 60;

    /// <summary>
    /// Directory for the on-disk TMSL cache. When empty, a "metadata-cache" folder
    /// under the OS temp path is used. Ignored when <see cref="PersistToDisk"/> is false.
    /// </summary>
    public string DiskCacheDirectory { get; set; } = "";
}
