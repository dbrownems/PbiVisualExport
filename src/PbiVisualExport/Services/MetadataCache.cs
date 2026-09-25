using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Options;

namespace PbiVisualExport.Services;

/// <summary>
/// Caches downloaded Fabric item definitions — semantic-model TMSL (kind <c>tmsl</c>)
/// and report definitions (kind <c>report</c>) — keyed by (kind, item id) so repeat
/// export requests avoid the expensive getDefinition download.
///
/// Three cooperating layers, all governed by <see cref="MetadataCacheOptions"/>:
///   1. In-process — the parsed value (e.g. a TOM model) is held per item.
///   2. Single-flight — a per-item lock ensures concurrent misses download once.
///   3. On disk — the raw downloaded text is persisted so a restart reuses it
///      (<c>{id:N}.{kind}.json</c>).
///
/// The download and parse are supplied by the caller; this class only decides whether
/// to call them. Cached values are shared across requests and must be treated as
/// read-only by consumers.
/// </summary>
internal sealed class MetadataCache
{
    private readonly MetadataCacheOptions _options;
    private readonly ILogger<MetadataCache> _log;
    private readonly string _diskDirectory;

    private readonly ConcurrentDictionary<(string Kind, Guid Id), CacheEntry> _memory = new();
    private readonly ConcurrentDictionary<(string Kind, Guid Id), SemaphoreSlim> _locks = new();

    public MetadataCache(IOptions<PbiVisualExportOptions> options, ILogger<MetadataCache> log)
    {
        _options = options.Value.MetadataCache;
        _log = log;
        _diskDirectory = string.IsNullOrWhiteSpace(_options.DiskCacheDirectory)
            ? Path.Combine(Path.GetTempPath(), "pbivisualexport-metadata-cache")
            : _options.DiskCacheDirectory;
    }

    /// <summary>
    /// Returns the cached value for (<paramref name="kind"/>, <paramref name="id"/>) if fresh,
    /// otherwise loads it (from disk when possible, else via <paramref name="download"/>),
    /// parses it and caches it.
    /// </summary>
    /// <param name="maxAge">
    /// Oldest acceptable cached copy. null = the configured TTL; <see cref="TimeSpan.Zero"/>
    /// = always re-download.
    /// </param>
    public async Task<T> GetOrLoadAsync<T>(
        string kind,
        Guid id,
        TimeSpan? maxAge,
        Func<Task<string>> download,
        Func<string, T> parse,
        CancellationToken ct) where T : class
    {
        if (!_options.Enabled)
        {
            _log.LogDebug("Metadata cache disabled; downloading {Kind} {Id}", kind, id);
            return parse(await download());
        }

        var key = (kind, id);
        if (TryGetFresh(key, maxAge, out T? cached))
        {
            _log.LogDebug("Metadata cache hit (memory) for {Kind} {Id}", kind, id);
            return cached!;
        }

        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Re-check after acquiring the lock: another request may have just loaded it.
            if (TryGetFresh(key, maxAge, out cached))
            {
                _log.LogDebug("Metadata cache hit (memory, post-lock) for {Kind} {Id}", kind, id);
                return cached!;
            }

            if (TryLoadFromDisk(key, maxAge, out var diskText, out var downloadedUtc))
            {
                _log.LogInformation("Metadata cache hit (disk) for {Kind} {Id}", kind, id);
                return Store(key, diskText!, downloadedUtc, parse, persist: false);
            }

            var text = await download();
            return Store(key, text, DateTimeOffset.UtcNow, parse, persist: _options.PersistToDisk);
        }
        finally
        {
            gate.Release();
        }
    }

    private bool TryGetFresh<T>((string Kind, Guid Id) key, TimeSpan? maxAge, out T? value) where T : class
    {
        if (_memory.TryGetValue(key, out var entry) && IsFresh(entry.DownloadedUtc, maxAge) && entry.Value is T v)
        {
            value = v;
            return true;
        }
        value = null;
        return false;
    }

    /// <param name="downloadedUtc">When the text was downloaded (for a disk hit, the file's write time) - ages count from it.</param>
    private T Store<T>((string Kind, Guid Id) key, string text, DateTimeOffset downloadedUtc, Func<string, T> parse, bool persist) where T : class
    {
        var value = parse(text);
        _memory[key] = new CacheEntry(downloadedUtc, value);
        _log.LogInformation("Cached {Kind} {Id} ({Chars:N0} chars)", key.Kind, key.Id, text.Length);
        if (persist) TrySaveToDisk(key, text);
        return value;
    }

    private bool IsFresh(DateTimeOffset downloadedUtc, TimeSpan? maxAge)
    {
        var age = DateTimeOffset.UtcNow - downloadedUtc;
        if (maxAge is { } m) return age < m;
        return _options.TtlMinutes <= 0 || age < TimeSpan.FromMinutes(_options.TtlMinutes);
    }

    // ---- Disk persistence --------------------------------------------------

    private string DiskPath((string Kind, Guid Id) key) => Path.Combine(_diskDirectory, $"{key.Id:N}.{key.Kind}.json");

    private bool TryLoadFromDisk((string Kind, Guid Id) key, TimeSpan? maxAge, out string? text, out DateTimeOffset downloadedUtc)
    {
        text = null;
        downloadedUtc = default;
        if (!_options.PersistToDisk) return false;
        try
        {
            var path = DiskPath(key);
            if (!File.Exists(path)) return false;
            downloadedUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            if (!IsFresh(downloadedUtc, maxAge)) return false;
            text = File.ReadAllText(path, Encoding.UTF8);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed reading metadata disk cache for {Kind} {Id}; will re-download", key.Kind, key.Id);
            return false;
        }
    }

    private void TrySaveToDisk((string Kind, Guid Id) key, string text)
    {
        var finalPath = DiskPath(key);
        var tempPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(_diskDirectory);
            // Write to a temp sibling then atomically swap into place so a crash
            // mid-write (or a concurrent reader in another process) never sees a
            // truncated file — the durability this disk layer exists for.
            File.WriteAllText(tempPath, text, Encoding.UTF8);
            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed writing metadata disk cache for {Kind} {Id}", key.Kind, key.Id);
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best-effort cleanup */ }
        }
    }

    private readonly record struct CacheEntry(DateTimeOffset DownloadedUtc, object Value);
}
