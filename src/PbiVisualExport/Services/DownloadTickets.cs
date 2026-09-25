using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace PbiVisualExport.Services;

internal enum DownloadState
{
    Issued,
    Streaming,
    Completed,
    Failed,
    Expired
}

/// <summary>A resolved export without its HTTP request (safe to keep beyond it).</summary>
internal sealed record ExportTarget(
    VisualExportRequest Request,
    Guid WorkspaceId, string WorkspaceName,
    Guid DatasetId, string DatasetName,
    Guid ReportId, string ReportName)
{
    public static ExportTarget From(VisualExportContext c) =>
        new(c.Request, c.WorkspaceId, c.WorkspaceName, c.DatasetId, c.DatasetName, c.ReportId, c.ReportName);
}

/// <summary>
/// A prepared export waiting to be downloaded by <c>GET {prefix}/download/{ticket}</c>.
/// Holds the resolved request (and effective identity) so the download itself needs no
/// credentials: the unguessable one-time ticket is the capability.
/// </summary>
internal sealed class DownloadTicket
{
    public required string Id { get; init; }
    public required ExportTarget Target { get; init; }
    public required EffectiveIdentity? Identity { get; init; }
    public required string? UserKey { get; init; }
    public required string? UserName { get; init; }
    public required string FileName { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }

    private int _state = (int)DownloadState.Issued;
    public DownloadState State => (DownloadState)Volatile.Read(ref _state);
    public long Rows;
    public string? Error { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Atomically moves Issued -> Streaming; false if already redeemed.</summary>
    public bool TryRedeem() =>
        Interlocked.CompareExchange(ref _state, (int)DownloadState.Streaming, (int)DownloadState.Issued) == (int)DownloadState.Issued;

    public void Finish(DownloadState state, string? error = null, DateTimeOffset? at = null)
    {
        Error = error;
        FinishedAt = at ?? DateTimeOffset.UtcNow;
        Volatile.Write(ref _state, (int)state);
    }
}

/// <summary>
/// In-process store of download tickets (256-bit random, base64url). A ticket can be redeemed
/// once, within <see cref="DownloadOptions.TicketLifetimeSeconds"/>; its status stays queryable
/// for <see cref="DownloadOptions.StatusRetentionMinutes"/>. In-memory, so behind a load
/// balancer the prepare and download requests must reach the same instance (session affinity).
/// </summary>
internal sealed class DownloadTicketStore
{
    private readonly ConcurrentDictionary<string, DownloadTicket> _tickets = new(StringComparer.Ordinal);
    private readonly DownloadOptions _options;
    private readonly TimeProvider _time;
    private long _lastSweep;

    public DownloadTicketStore(IOptions<PbiVisualExportOptions> options, TimeProvider? time = null)
    {
        _options = options.Value.Downloads;
        _time = time ?? TimeProvider.System;
    }

    public TimeSpan Lifetime => TimeSpan.FromSeconds(Math.Max(1, _options.TicketLifetimeSeconds));

    public DownloadTicket Issue(ExportTarget target, EffectiveIdentity? identity, string? userKey, string? userName, string fileName)
    {
        Sweep();
        var ticket = new DownloadTicket
        {
            Id = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
            Target = target,
            Identity = identity,
            UserKey = userKey,
            UserName = userName,
            FileName = fileName,
            ExpiresAt = _time.GetUtcNow() + Lifetime
        };
        _tickets[ticket.Id] = ticket;
        return ticket;
    }

    /// <summary>Finds a ticket (any state). Unredeemed tickets past their lifetime report <see cref="DownloadState.Expired"/>.</summary>
    public DownloadTicket? Find(string id)
    {
        if (!_tickets.TryGetValue(id, out var t)) return null;
        var now = _time.GetUtcNow();
        if (t.State == DownloadState.Issued && now > t.ExpiresAt && t.TryRedeem())
            t.Finish(DownloadState.Expired, "The download link expired before it was used.", now);
        return t;
    }

    private void Sweep()
    {
        var now = _time.GetUtcNow();
        var last = Interlocked.Read(ref _lastSweep);
        if (now.UtcTicks - last < TimeSpan.TicksPerMinute || Interlocked.CompareExchange(ref _lastSweep, now.UtcTicks, last) != last) return;

        var retention = TimeSpan.FromMinutes(Math.Max(1, _options.StatusRetentionMinutes));
        foreach (var (id, t) in _tickets)
        {
            var done = t.FinishedAt ?? (t.State == DownloadState.Issued ? t.ExpiresAt : (DateTimeOffset?)null);
            if (done is { } d && now - d > retention) _tickets.TryRemove(id, out _);
        }
    }
}

/// <summary>Caps exports running at once, per user and overall (<see cref="ExportOptions"/>).</summary>
internal sealed class ExportConcurrencyLimiter
{
    private readonly ExportOptions _options;
    private readonly Dictionary<string, int> _perUser = new(StringComparer.Ordinal);
    private int _total;

    public ExportConcurrencyLimiter(IOptions<PbiVisualExportOptions> options) => _options = options.Value.Export;

    /// <summary>Takes a slot for <paramref name="userKey"/>; dispose the lease when the export ends. Null = limit reached.</summary>
    public IDisposable? TryAcquire(string userKey)
    {
        lock (_perUser)
        {
            if (!HasRoom(userKey)) return null;
            _perUser[userKey] = _perUser.GetValueOrDefault(userKey) + 1;
            _total++;
        }
        return new Lease(this, userKey);
    }

    /// <summary>Whether <see cref="TryAcquire"/> would succeed right now (pre-flight check).</summary>
    public bool WouldAllow(string userKey)
    {
        lock (_perUser) return HasRoom(userKey);
    }

    private bool HasRoom(string userKey) =>
        (_options.MaxConcurrentExports <= 0 || _total < _options.MaxConcurrentExports) &&
        (_options.MaxConcurrentExportsPerUser <= 0 || _perUser.GetValueOrDefault(userKey) < _options.MaxConcurrentExportsPerUser);

    private void Release(string userKey)
    {
        lock (_perUser)
        {
            _total--;
            var n = _perUser.GetValueOrDefault(userKey) - 1;
            if (n <= 0) _perUser.Remove(userKey);
            else _perUser[userKey] = n;
        }
    }

    private sealed class Lease(ExportConcurrencyLimiter owner, string userKey) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(userKey);
        }
    }
}
