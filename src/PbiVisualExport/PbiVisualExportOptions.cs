namespace PbiVisualExport;

/// <summary>
/// Options for the visual export module, bound from the <c>PbiVisualExport</c> configuration
/// section by <see cref="PbiVisualExportServiceCollectionExtensions.AddPbiVisualExport(Microsoft.Extensions.DependencyInjection.IServiceCollection, Microsoft.Extensions.Configuration.IConfiguration, Action{PbiVisualExportOptions}?)"/>
/// (environment variables use <c>__</c>, e.g. <c>PbiVisualExport__Credential__Mode</c>).
/// </summary>
public sealed class PbiVisualExportOptions
{
    public const string DefaultSectionName = "PbiVisualExport";

    /// <summary>
    /// The identity the module uses for Power BI REST lookups, Fabric getDefinition (metadata)
    /// and the XMLA endpoint (data). Ignored when the host supplies a credential with
    /// <see cref="PbiVisualExportBuilder.UseCredential"/>.
    /// </summary>
    public CredentialOptions Credential { get; set; } = new();

    /// <summary>
    /// Workspaces exports may read from. Requests for any other workspace are rejected before
    /// anything is called. With the default <see cref="IVisualExportAuthorizer"/> this list is
    /// the whole policy, and an empty list rejects every export; a host-supplied authorizer
    /// can decide per user instead (the list, when not empty, still applies).
    /// Environment variables: <c>PbiVisualExport__AllowedWorkspaceIds__0=&lt;guid&gt;</c>.
    /// </summary>
    public List<Guid> AllowedWorkspaceIds { get; set; } = new();

    /// <summary>XMLA endpoint prefix; the workspace name is appended (e.g. powerbi://api.powerbi.com/v1.0/myorg/Sales).</summary>
    public string XmlaEndpoint { get; set; } = "powerbi://api.powerbi.com/v1.0/myorg";

    public ExportOptions Export { get; set; } = new();

    public MetadataCacheOptions MetadataCache { get; set; } = new();
}

public sealed class CredentialOptions
{
    /// <summary>
    /// AzureCli (local development: your <c>az login</c> identity) | ServicePrincipal | MasterUser | DeviceCode.
    /// Use ServicePrincipal (or <see cref="PbiVisualExportBuilder.UseCredential"/>) when deployed.
    /// </summary>
    public string Mode { get; set; } = "AzureCli";
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>
    /// Credential used for the XMLA endpoint (the export data path):
    ///  * Auto (default): DeviceCode when <see cref="Mode"/> is AzureCli (or DeviceCode with no
    ///    ClientId) because the XMLA endpoint rejects Azure CLI tokens; otherwise Shared.
    ///  * Shared: reuse the main credential (ServicePrincipal / MasterUser / custom-app DeviceCode
    ///    / a host-supplied credential).
    ///  * DeviceCode: always sign in with <see cref="XmlaClientId"/> via device code (development only).
    /// </summary>
    public string XmlaMode { get; set; } = "Auto";

    /// <summary>Public client used for XMLA device-code sign-in (default: the Analysis Services client ADOMD.NET itself uses).</summary>
    public string XmlaClientId { get; set; } = "cf710c6e-dfcc-4fa8-a093-d47294e44c66";
}

public sealed class ExportOptions
{
    /// <summary>
    /// Exact cap on exported rows (0 = unlimited). The query is wrapped in DAX <c>TOPN</c> to limit
    /// server work, and since <c>TOPN</c> keeps ties the stream also stops after this many rows.
    /// </summary>
    public long MaxRows { get; set; } = 0;

    /// <summary>XMLA command timeout for the export query in seconds (0 = no timeout).</summary>
    public int CommandTimeoutSeconds { get; set; } = 0;

    /// <summary>Exports one user may run at once (0 = unlimited); more get HTTP 429.</summary>
    public int MaxConcurrentExportsPerUser { get; set; } = 2;

    /// <summary>Exports the process runs at once across all users (0 = unlimited); more get HTTP 429.</summary>
    public int MaxConcurrentExports { get; set; } = 0;

    /// <summary>
    /// How the requested fields are checked against the visual as saved in the published report
    /// definition (downloaded and cached), so a caller can only export what the visual shows:
    ///  * Strict (default): the report must be PBIR (enhanced format). The request must name
    ///    the page and visual (400 otherwise), the visual must be in the saved definition, and
    ///    every output field must be one of its fields: the same column, the same measure, or
    ///    the same aggregation of a column, in any of its data roles (403 otherwise; unsaved or
    ///    personalized visuals can't be exported). Sort keys that are neither fields of the
    ///    visual nor in its saved sort are dropped with a warning. PBIR-Legacy reports get 403.
    ///  * PbirOnly: as Strict for PBIR reports; PBIR-Legacy reports are exported unchecked.
    ///  * Off: no check (the request's field list is trusted).
    /// Filters are not restricted: the embedding API can apply any filter to the live visual
    /// anyway, and filters only narrow the rows, except filters on a column bound to a dynamic
    /// M query parameter, which choose the data the source query reads (see
    /// <see cref="ValidateParameterValues"/>). A miss re-downloads a cached definition older
    /// than <see cref="MetadataCacheOptions.RefreshOnMissSeconds"/> once (the report may have
    /// just been republished).
    /// </summary>
    public FieldValidationMode FieldValidation { get; set; } = FieldValidationMode.Strict;

    /// <summary>
    /// A filter on a column bound to a dynamic M query parameter sets the parameter
    /// (<c>MPARAMETER</c>), which the model's M passes to the source (e.g. into a native SQL
    /// query). When true (default), each value must exist in the bound column, checked with a
    /// small DAX query before the export (400 otherwise), so a caller can't pass arbitrary text
    /// into the source query. A slicer's "Select all" passes the binding's select-all value,
    /// which comes from the model, so it isn't checked. Power BI itself passes unknown values
    /// through (usually giving an empty result); set false to match that.
    /// </summary>
    public bool ValidateParameterValues { get; set; } = true;

    /// <summary>
    /// Enables <c>POST {prefix}/bench</c>, which runs an export's query and reads every
    /// value without writing CSV (the baseline for <c>infra/perf-export.ps1</c>). Off by
    /// default: it lets callers run arbitrary-sized queries without downloading anything.
    /// </summary>
    public bool EnableBenchmark { get; set; } = false;
}

/// <summary>See <see cref="ExportOptions.FieldValidation"/>.</summary>
public enum FieldValidationMode
{
    Strict,
    PbirOnly,
    Off
}

/// <summary>
/// Controls caching of downloaded Fabric definitions — semantic-model (TOM) metadata and
/// report definitions — so repeat export requests do not re-hit the Fabric getDefinition API.
/// The metadata rarely changes, so it is cached in-process and (optionally) on disk across restarts.
/// </summary>
public sealed class MetadataCacheOptions
{
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
    /// Directory for the on-disk cache. When empty, a "pbivisualexport-metadata-cache" folder
    /// under the OS temp path is used. Ignored when <see cref="PersistToDisk"/> is false.
    /// </summary>
    public string DiskCacheDirectory { get; set; } = "";
}
