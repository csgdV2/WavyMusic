using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using MusicApp.Core.Abstractions;
using MusicApp.Core.Models;
using MusicApp.Core.Services;
using MusicApp.Models;

namespace MusicApp.Services;

public sealed class CacheService
{
    private readonly IYtDlpService _ytDlp;
    private readonly string _cacheDir;
    private readonly string _indexPath;

    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _inFlight = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly HttpClient Http = new(Ipv4Http.CreateHandler())
    {
        Timeout = TimeSpan.FromMinutes(5),
    };

    public CacheService(IYtDlpService ytDlp, string cacheDir)
    {
        _ytDlp = ytDlp;
        _cacheDir = cacheDir;
        _indexPath = Path.Combine(cacheDir, "index.json");
        Load();
    }

    public string? TryGetLocalPath(string trackId)
    {
        lock (_gate)
            return _entries.TryGetValue(trackId, out var e) && File.Exists(e.FilePath) ? e.FilePath : null;
    }

    public void MarkPlayed(Track track)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(track.Id, out var e))
            {
                e = new CacheEntry { Id = track.Id, CachedUtc = DateTimeOffset.UtcNow };
                _entries[track.Id] = e;
            }

            e.Title = track.Title;
            e.Artist = track.Artist;
            e.ThumbnailUrl = track.ThumbnailUrl;
            e.DurationSeconds = track.Duration?.TotalSeconds;
            e.LastPlayedUtc = DateTimeOffset.UtcNow;
            e.HiddenFromHistory = false;
            Save();
        }
    }

    public Task EnsureCachedAsync(Track track, CancellationToken ct = default)
    {
        if (TryGetLocalPath(track.Id) is not null)
            return Task.CompletedTask;

        lock (_gate)
        {
            if (_inFlight.TryGetValue(track.Id, out var existing))
                return existing;
            var task = DownloadAsync(track, ct);
            _inFlight[track.Id] = task;
            return task;
        }
    }

    public IReadOnlyList<Track> GetCachedTracks()
    {
        lock (_gate)
            return _entries.Values
                .Where(e => !e.HiddenFromHistory)
                .OrderByDescending(e => e.LastPlayedUtc)
                .Select(e => e.ToTrack())
                .ToList();
    }

    public int ClearHistory()
    {
        var cleared = 0;
        lock (_gate)
        {
            foreach (var e in _entries.Values)
            {
                if (e.HiddenFromHistory)
                    continue;
                e.HiddenFromHistory = true;
                cleared++;
            }
            if (cleared > 0)
                Save();
        }
        return cleared;
    }

    public (int Count, long Bytes) Usage()
    {
        lock (_gate)
        {
            var count = 0;
            long bytes = 0;
            foreach (var entry in _entries.Values)
            {
                try
                {
                    var file = new FileInfo(entry.FilePath);
                    if (!file.Exists)
                        continue;
                    count++;
                    bytes += file.Length;
                }
                catch
                {

                }
            }
            return (count, bytes);
        }
    }

    public int EvictStale(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        var removed = 0;
        lock (_gate)
        {
            foreach (var e in _entries.Values)
            {
                if (e.LastPlayedUtc >= cutoff || e.FilePath.Length == 0)
                    continue;
                TryDelete(e.FilePath);
                e.FilePath = "";
                removed++;
            }
            if (removed > 0)
                Save();
        }
        return removed;
    }

    public int ClearAll()
    {
        var removed = 0;
        lock (_gate)
        {
            foreach (var e in _entries.Values)
            {
                if (e.FilePath.Length == 0)
                    continue;
                TryDelete(e.FilePath);
                e.FilePath = "";
                removed++;
            }
            Save();
        }
        return removed;
    }

    private async Task DownloadAsync(Track track, CancellationToken ct)
    {
        try
        {

            if (await TryDownloadDirectAsync(track, ct).ConfigureAwait(false) is not null)
                return;

            var template = Path.Combine(_cacheDir, track.Id + ".%(ext)s");
            string? destination = null;
            await foreach (var progress in _ytDlp.DownloadAsync(track, template, ct).ConfigureAwait(false))
            {
                if (progress.Destination is not null)
                    destination = progress.Destination;
            }

            var finalPath = ResolveDownloadedFile(track.Id, destination);
            if (finalPath is null)
                return;

            Register(track, finalPath);
        }
        catch
        {

        }
        finally
        {
            lock (_gate)
                _inFlight.Remove(track.Id);
        }
    }

    private async Task<string?> TryDownloadDirectAsync(Track track, CancellationToken ct)
    {
        var stream = await _ytDlp.ResolveStreamAsync(track, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(stream.Url))
            return null;

        return await FetchAsync(track, stream.Url, ct).ConfigureAwait(false);
    }

    public async Task<string?> FetchAsync(Track track, string url, CancellationToken ct = default)
    {
        if (TryGetLocalPath(track.Id) is { } already)
            return already;

        var temp = Path.Combine(_cacheDir, track.Id + ".partial");
        try
        {
            Directory.CreateDirectory(_cacheDir);

            using var response = await Http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            await using (var file = File.Create(temp))
                await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);

            if (new FileInfo(temp).Length < 1024)
                return null;

            var destination = Path.Combine(_cacheDir, track.Id + ".m4a");
            File.Move(temp, destination, overwrite: true);
            Register(track, destination);
            return destination;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (File.Exists(temp))
                TryDelete(temp);
        }
    }

    private void Register(Track track, string filePath)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(track.Id, out var e))
            {
                e = new CacheEntry { Id = track.Id, LastPlayedUtc = DateTimeOffset.UtcNow };
                _entries[track.Id] = e;
            }

            e.Title = track.Title;
            e.Artist = track.Artist;
            e.ThumbnailUrl = track.ThumbnailUrl;
            e.DurationSeconds = track.Duration?.TotalSeconds;
            e.FilePath = filePath;
            e.CachedUtc = DateTimeOffset.UtcNow;
            Save();
        }
    }

    private string? ResolveDownloadedFile(string id, string? reportedDestination)
    {
        if (reportedDestination is not null && File.Exists(reportedDestination))
            return reportedDestination;

        try
        {
            return Directory.EnumerateFiles(_cacheDir, id + ".*")
                .FirstOrDefault(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_indexPath))
                return;
            var json = File.ReadAllText(_indexPath);
            var entries = JsonSerializer.Deserialize<List<CacheEntry>>(json, JsonOptions);
            if (entries is null)
                return;
            foreach (var e in entries)
                if (!string.IsNullOrEmpty(e.Id))
                    _entries[e.Id] = e;
        }
        catch
        {

        }

        PruneOrphans();
    }

    private void PruneOrphans()
    {
        try
        {
            if (!Directory.Exists(_cacheDir))
                return;

            var known = new HashSet<string>(_entries.Values.Select(e => e.FilePath ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);

            foreach (var path in Directory.EnumerateFiles(_cacheDir))
            {
                if (string.Equals(path, _indexPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!known.Contains(path))
                    TryDelete(path);
            }
        }
        catch
        {

        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_entries.Values.ToList(), JsonOptions);
            File.WriteAllText(_indexPath, json);
        }
        catch
        {

        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {

        }
    }
}
