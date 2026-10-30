using System.IO;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using Wavy.Core.Abstractions;
using Wavy.Core.Models;
using Wavy.Core.Services;
using Wavy.Models;

namespace Wavy.Services;

public sealed class CacheService
{
    private readonly IYtDlpService _ytDlp;
    private readonly string _cacheDir;
    private readonly string _indexPath;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fetchGates = new(StringComparer.Ordinal);
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

    public (double IntegratedLufs, double Peak)? GetAudioAnalysis(string path, string key)
    {
        lock (_gate)
        {
            var entry = _entries.Values.FirstOrDefault(e => string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase));
            return entry?.AudioAnalysisKey == key && entry.IntegratedLufs is { } lufs && entry.AnalyzedPeak is { } peak
                && double.IsFinite(lufs) && double.IsFinite(peak) && peak >= 0
                ? (lufs, peak) : null;
        }
    }

    public void SaveAudioAnalysis(string path, string key, double integratedLufs, double peak)
    {
        if (!double.IsFinite(integratedLufs) || !double.IsFinite(peak) || peak < 0) return;
        lock (_gate)
        {
            var entry = _entries.Values.FirstOrDefault(e => string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return;
            entry.AudioAnalysisKey = key;
            entry.IntegratedLufs = integratedLufs;
            entry.AnalyzedPeak = peak;
            Save();
        }
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
        => EnsureCachedAsync(track, false, ct);

    public Task EnsureCachedFromSourceAsync(Track track, CancellationToken ct = default)
        => EnsureCachedAsync(track, true, ct);

    private Task EnsureCachedAsync(Track track, bool forceYtDlp, CancellationToken ct)
    {
        if (TryGetLocalPath(track.Id) is not null)
            return Task.CompletedTask;

        lock (_gate)
        {
            if (_inFlight.TryGetValue(track.Id, out var existing))
                return existing;
            var task = DownloadAsync(track, ct, forceYtDlp);
            _inFlight[track.Id] = task;
            return task;
        }
    }

    public IReadOnlyList<Track> GetCachedTracks()
    {
        lock (_gate)
            return _entries.Values
                .Where(e => !e.HiddenFromHistory && e.LastPlayedUtc > DateTimeOffset.MinValue)
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
                DeleteProcessed(e.FilePath);
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
                DeleteProcessed(e.FilePath);
                TryDelete(e.FilePath);
                e.FilePath = "";
                removed++;
            }
            Save();
        }
        return removed;
    }

    private async Task DownloadAsync(Track track, CancellationToken ct, bool forceYtDlp)
    {
        try
        {
            if (!forceYtDlp && await TryDownloadDirectAsync(track, ct).ConfigureAwait(false) is not null)
                return;

            var template = Path.Combine(_cacheDir, CacheFileStem(track).Replace("%", "%%") + ".%(ext)s");
            string? destination = null;
            await foreach (var progress in _ytDlp.DownloadAsync(track, template, ct).ConfigureAwait(false))
            {
                if (progress.Destination is not null)
                    destination = progress.Destination;
            }

            var finalPath = ResolveDownloadedFile(CacheFileStem(track), destination);
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
        var gate = _fetchGates.GetOrAdd(track.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (TryGetLocalPath(track.Id) is { } already) return already;
            return await FetchCoreAsync(track, url, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<string?> FetchCoreAsync(Track track, string url, CancellationToken ct)
    {
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

            var destination = Path.Combine(_cacheDir, CacheFileStem(track) + ".m4a");
            File.Move(temp, destination, overwrite: true);
            Register(track, destination);
            return destination;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
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

    private static string CacheFileStem(Track track)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var title = new string(track.Title.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (title.Length == 0) title = "Untitled";
        if (title.Length > 100) title = title[..100].TrimEnd();
        var id = new string(track.Id.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return $"{title} [{id}]";
    }

    private void RenameCachedFiles()
    {
        var changed = false;
        foreach (var entry in _entries.Values)
        {
            try
            {
                if (!File.Exists(entry.FilePath)) continue;
                var old = entry.FilePath;
                var renamed = Path.Combine(_cacheDir, CacheFileStem(entry.ToTrack()) + Path.GetExtension(old));
                if (string.Equals(old, renamed, StringComparison.OrdinalIgnoreCase)) continue;
                File.Move(old, renamed);
                entry.FilePath = renamed;
                DeleteProcessed(old);
                changed = true;
            }
            catch { }
        }
        if (changed) Save();
    }

    private void Register(Track track, string filePath)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(track.Id, out var e))
            {
                e = new CacheEntry { Id = track.Id };
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
                .FirstOrDefault(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                    && !f.Contains(".dsp-", StringComparison.OrdinalIgnoreCase)
                    && !f.Contains(".norm16.", StringComparison.OrdinalIgnoreCase)
                    && !f.EndsWith(".partial", StringComparison.OrdinalIgnoreCase));
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

        RenameCachedFiles();
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

    private static void DeleteProcessed(string source)
    {
        TryDelete(source + ".norm16.m4a");
        try
        {
            if (Path.GetDirectoryName(source) is { } folder && Directory.Exists(folder))
                foreach (var path in Directory.EnumerateFiles(folder, Path.GetFileName(source) + ".dsp-*.wav")) TryDelete(path);
        }
        catch { }
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
