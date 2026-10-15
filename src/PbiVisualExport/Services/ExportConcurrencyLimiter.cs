using Microsoft.Extensions.Options;

namespace PbiVisualExport.Services;

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
