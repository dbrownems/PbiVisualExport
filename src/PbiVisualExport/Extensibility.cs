using Microsoft.AspNetCore.Http;

namespace PbiVisualExport;

/// <summary>
/// A resolved export request: every ID and name has been checked against the workspace
/// catalog. Passed to the host's <see cref="IVisualExportAuthorizer"/> and
/// <see cref="IEffectiveIdentityProvider"/>.
/// </summary>
public sealed class VisualExportContext
{
    public required HttpContext HttpContext { get; init; }
    public required VisualExportRequest Request { get; init; }
    public required Guid WorkspaceId { get; init; }
    public required string WorkspaceName { get; init; }
    public required Guid DatasetId { get; init; }
    public required string DatasetName { get; init; }
    public required Guid ReportId { get; init; }
    public required string ReportName { get; init; }
    public string? PageName => Request.PageName;
    public string? VisualName => Request.VisualName;
}

/// <summary>The outcome of <see cref="IVisualExportAuthorizer.AuthorizeAsync"/>.</summary>
public sealed record VisualExportAuthorization(bool Allowed, string? Reason = null)
{
    public static VisualExportAuthorization Allow() => new(true);
    public static VisualExportAuthorization Deny(string reason) => new(false, reason);
}

/// <summary>
/// Decides whether the calling user may export a visual (HTTP 403 when denied). Runs after
/// the endpoint's ASP.NET Core authorization and the <see cref="PbiVisualExportOptions.AllowedWorkspaceIds"/>
/// check, for every export, pre-flight and benchmark request. The module reads data with its
/// own identity, so this is where a host enforces that the user may see the report: e.g. that
/// the user's tenant owns the workspace. The default allows any request that passed the
/// allow-list and denies everything when the list is empty.
/// Register with <see cref="PbiVisualExportBuilder.AddAuthorizer{T}"/>.
/// </summary>
public interface IVisualExportAuthorizer
{
    ValueTask<VisualExportAuthorization> AuthorizeAsync(VisualExportContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Identity the export query runs as, so row-level security applies to the user rather than
/// to the module's own (typically admin) identity. Applied as the XMLA connection-string
/// properties <c>EffectiveUserName</c>, <c>Roles</c> and <c>CustomData</c>
/// (see "Impersonation" in the Power BI XMLA endpoint docs):
/// <c>EffectiveUserName</c> must be an account in the tenant's Entra ID with Read and Build
/// permission on the semantic model; with <c>Roles</c>, a module identity that is not a
/// workspace admin must itself belong to the roles. For "embed for your customers" users who
/// are not Entra accounts, map them to <c>Roles</c> (and <c>CustomData</c>) instead.
/// </summary>
/// <param name="UserName">A UPN in the tenant (<c>EffectiveUserName</c>); null to apply only roles.</param>
/// <param name="Roles">Model roles to apply (<c>Roles</c>); null or empty = the user's own role membership.</param>
/// <param name="CustomData">Value returned by <c>CUSTOMDATA()</c> in RLS expressions.</param>
public sealed record EffectiveIdentity(string? UserName, IReadOnlyList<string>? Roles = null, string? CustomData = null);

/// <summary>
/// Supplies the <see cref="EffectiveIdentity"/> for an export (null = run as the module's
/// identity, i.e. no RLS). Register with <see cref="PbiVisualExportBuilder.AddEffectiveIdentity{T}"/>.
/// </summary>
public interface IEffectiveIdentityProvider
{
    ValueTask<EffectiveIdentity?> GetEffectiveIdentityAsync(VisualExportContext context, CancellationToken cancellationToken);
}

public enum VisualExportOutcome
{
    Completed,
    Failed,
    Canceled
}

/// <summary>
/// One export that reached the data step, reported to every <see cref="IExportAuditSink"/>.
/// <see cref="Fields"/> is the number of requested fields; <see cref="Rows"/> the rows written.
/// </summary>
public sealed record VisualExportAuditRecord(
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    string? User,
    string Channel,
    Guid WorkspaceId,
    string WorkspaceName,
    Guid DatasetId,
    string DatasetName,
    Guid ReportId,
    string ReportName,
    string? PageName,
    string? VisualName,
    string? VisualTitle,
    int Fields,
    long Rows,
    string? EffectiveUserName,
    VisualExportOutcome Outcome,
    string? Error);

/// <summary>
/// Receives an audit record for each <c>csv</c> request and each <c>download</c> with a valid
/// ticket, once it has a concurrency slot, whether it completed, failed or was canceled.
/// Requests rejected before that (request checks, invalid tickets, 429), <c>prepare</c> and
/// <c>bench</c> are not audited. Sink failures are logged and ignored. Register with <see cref="PbiVisualExportBuilder.AddAuditSink{T}"/>
/// (any number of sinks).
/// </summary>
public interface IExportAuditSink
{
    ValueTask WriteAsync(VisualExportAuditRecord record, CancellationToken cancellationToken);
}
