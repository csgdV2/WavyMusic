using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using MusicApp.Core.Abstractions;
using MusicApp.Core.Models;

namespace MusicApp.Core.Services;

public sealed partial class YtDlpService : IYtDlpService
{

    private const string AudioFormat = "bestaudio[ext=m4a]/bestaudio[acodec^=mp4a]/best[ext=mp4]";

    private const string StreamPlayerClient = "android";

    private static readonly TimeSpan StreamSafetyMargin = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan StreamAssumedLifetime = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan SearchCacheLifetime = TimeSpan.FromMinutes(10);
    private const int SearchCacheCapacity = 64;

    private static readonly TimeSpan PageCacheLifetime = TimeSpan.FromMinutes(30);
    private const int PageCacheCapacity = 24;

    private readonly IProcessRunner _runner;
    private readonly IToolLocator _locator;
    private readonly string? _ffmpegPath;
    private readonly YouTubeSearchClient _fastSearch = new();
    private readonly YouTubeMusicSearchClient _musicSearch = new();
    private readonly YouTubeMusicBrowseClient _musicBrowse = new();
    private readonly YouTubeMusicRadioClient _musicRadio = new();
    private readonly YouTubeStreamClient _fastStream = new();
    private readonly DashManifestBuilder _dashManifest = new();

    private readonly ConcurrentDictionary<string, (DateTimeOffset CachedAt, IReadOnlyList<Track> Tracks)> _searchCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTimeOffset CachedAt, object Page)> _pageCache =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (ResolvedStream Stream, DateTimeOffset ResolvedAt)> _streamCache =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, Task<ResolvedStream>> _inFlightResolves =
        new(StringComparer.Ordinal);

    public YtDlpService(IProcessRunner runner, IToolLocator locator, string? ffmpegPath = null)
    {
        _runner = runner;
        _locator = locator;
        _ffmpegPath = ffmpegPath;
    }

    public async Task PrewarmAsync(CancellationToken ct = default)
    {
        try
        {
            var ytDlp = await _locator.GetYtDlpPathAsync(ct).ConfigureAwait(false);
            await _runner.RunAsync(ytDlp, new[] { "--version" }, ct).ConfigureAwait(false);
        }
        catch
        {

        }
    }

    public async Task<IReadOnlyList<Track>> SearchAsync(
        string query, SearchFilter filter = SearchFilter.Songs, int limit = 12, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<Track>();

        var cacheKey = $"{filter}\0{limit}\0{query.Trim()}";
        if (_searchCache.TryGetValue(cacheKey, out var cached)
            && DateTimeOffset.UtcNow - cached.CachedAt < SearchCacheLifetime)
        {
            return cached.Tracks;
        }

        var tracks = await TryAsync(() => _musicSearch.SearchTracksAsync(query, filter, limit, ct), ct)
                     .ConfigureAwait(false)
                     ?? Array.Empty<Track>();

        if (tracks.Count == 0)
            tracks = await TryAsync(() => _fastSearch.SearchAsync(query, limit, ct), ct).ConfigureAwait(false)
                     ?? Array.Empty<Track>();

        if (tracks.Count == 0)
            tracks = await SearchWithYtDlpAsync(query, limit, ct).ConfigureAwait(false);

        if (tracks.Count > 0)
            StoreSearch(cacheKey, tracks);

        return tracks;
    }

    public async Task<IReadOnlyList<Track>> GetSimilarAsync(
        Track track, int limit = 12, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(track?.Id))
            return Array.Empty<Track>();

        var tracks = await TryAsync(() => _musicRadio.GetRadioAsync(track!.Id, limit, ct), ct).ConfigureAwait(false)
                     ?? Array.Empty<Track>();

        if (tracks.Count == 0 && !string.IsNullOrWhiteSpace(track!.Artist))
        {
            var byArtist = await SearchAsync(track.Artist!, SearchFilter.Songs, limit + 4, ct).ConfigureAwait(false);
            tracks = byArtist.Where(t => t.Id != track.Id).Take(limit).ToList();
        }

        return tracks;
    }

    public async Task<IReadOnlyList<Track>> GetTrendingAsync(int limit = 20, CancellationToken ct = default) =>
        await GetPageAsync<IReadOnlyList<Track>>(
            "FEmusic_charts", () => _musicBrowse.GetTrendingAsync(limit, ct), ct).ConfigureAwait(false)
        ?? Array.Empty<Track>();

    public async Task<IReadOnlyList<Track>> GetNewReleasesAsync(int limit = 20, CancellationToken ct = default) =>
        await GetPageAsync<IReadOnlyList<Track>>(
            "FEmusic_new_releases", () => _musicBrowse.GetNewReleasesAsync(limit, ct), ct).ConfigureAwait(false)
        ?? Array.Empty<Track>();

    public async Task<IReadOnlyList<ArtistResult>> SearchArtistsAsync(
        string query, int limit = 12, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(query)
            ? Array.Empty<ArtistResult>()
            : await TryAsync(() => _musicSearch.SearchArtistsAsync(query, limit, ct), ct).ConfigureAwait(false)
              ?? Array.Empty<ArtistResult>();

    public async Task<IReadOnlyList<AlbumResult>> SearchAlbumsAsync(
        string query, int limit = 12, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(query)
            ? Array.Empty<AlbumResult>()
            : await TryAsync(() => _musicSearch.SearchAlbumsAsync(query, limit, ct), ct).ConfigureAwait(false)
              ?? Array.Empty<AlbumResult>();

    public async Task<IReadOnlyList<PlaylistResult>> SearchPlaylistsAsync(
        string query, int limit = 12, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(query)
            ? Array.Empty<PlaylistResult>()
            : await TryAsync(() => _musicSearch.SearchPlaylistsAsync(query, limit, ct), ct).ConfigureAwait(false)
              ?? Array.Empty<PlaylistResult>();

    public Task<ArtistDetails?> GetArtistAsync(string browseId, CancellationToken ct = default) =>        GetPageAsync(browseId, () => _musicBrowse.GetArtistAsync(browseId, ct), ct);

    public Task<AlbumDetails?> GetAlbumAsync(string browseId, CancellationToken ct = default) =>
        GetPageAsync(browseId, () => _musicBrowse.GetAlbumAsync(browseId, ct), ct);

    public Task<PlaylistDetails?> GetPlaylistAsync(string urlOrId, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(urlOrId)
            ? Task.FromResult<PlaylistDetails?>(null)
            : TryPageAsync(() => _musicBrowse.GetPlaylistAsync(urlOrId, ct), ct);

    private async Task<T?> GetPageAsync<T>(string browseId, Func<Task<T?>> fetch, CancellationToken ct)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(browseId))
            return null;

        if (_pageCache.TryGetValue(browseId, out var cached)
            && DateTimeOffset.UtcNow - cached.CachedAt < PageCacheLifetime
            && cached.Page is T fresh)
        {
            return fresh;
        }

        var page = await TryPageAsync(fetch, ct).ConfigureAwait(false);
        if (page is null)
            return null;

        if (_pageCache.Count >= PageCacheCapacity)
        {
            foreach (var stale in _pageCache.OrderBy(e => e.Value.CachedAt).Take(PageCacheCapacity / 2).ToList())
                _pageCache.TryRemove(stale.Key, out _);
        }
        _pageCache[browseId] = (DateTimeOffset.UtcNow, page);
        return page;
    }

    private static async Task<T?> TryAsync<T>(Func<Task<T>> source, CancellationToken ct) where T : class
    {
        try
        {
            return await source().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<T?> TryPageAsync<T>(Func<Task<T?>> source, CancellationToken ct) where T : class
    {
        try
        {
            return await source().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<Track>> SearchWithYtDlpAsync(string query, int limit, CancellationToken ct)
    {
        var args = new List<string>
        {
            $"ytsearch{limit}:{query}",
            "--dump-json",
            "--flat-playlist",
            "--no-warnings",
            "--ignore-config",
            "--force-ipv4",
            "--socket-timeout", "10",
        };

        var ytDlp = await _locator.GetYtDlpPathAsync(ct).ConfigureAwait(false);
        var result = await _runner.RunAsync(ytDlp, args, ct).ConfigureAwait(false);

        var tracks = new List<Track>();
        foreach (var line in result.StdOut.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{')
                continue;
            if (TryParseSearchEntry(trimmed) is { } track)
                tracks.Add(track);
        }

        if (tracks.Count == 0 && !result.Success)
            throw new YtDlpException($"Search failed (exit {result.ExitCode}): {Truncate(result.StdErr)}");

        return tracks;
    }

    private void StoreSearch(string cacheKey, IReadOnlyList<Track> tracks)
    {

        if (_searchCache.Count >= SearchCacheCapacity)
        {
            foreach (var stale in _searchCache.OrderBy(e => e.Value.CachedAt).Take(SearchCacheCapacity / 2).ToList())
                _searchCache.TryRemove(stale.Key, out _);
        }
        _searchCache[cacheKey] = (DateTimeOffset.UtcNow, tracks);
    }

    public async Task<ResolvedStream> ResolveStreamAsync(Track track, CancellationToken ct = default)
    {
        if (TryGetCachedStream(track.Id) is { } cached)
            return cached;

        var resolve = _inFlightResolves.GetOrAdd(track.Id, _ => ResolveAndCacheAsync(track, CancellationToken.None));
        return await resolve.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<ResolvedStream> ResolveAndCacheAsync(Track track, CancellationToken ct)
    {
        try
        {

            string? url = null;
            try
            {
                url = await _fastStream.ResolveAudioUrlAsync(track.Id, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {

            }

            url ??= await ResolveWithYtDlpAsync(track, ct).ConfigureAwait(false);

            return Cache(track.Id, url, await BuildManifestAsync(url, ct).ConfigureAwait(false));
        }
        finally
        {
            _inFlightResolves.TryRemove(track.Id, out _);
        }
    }

    public async Task<ResolvedStream> ReresolveStreamAsync(Track track, CancellationToken ct = default)
    {
        _streamCache.TryRemove(track.Id, out _);
        var url = await ResolveWithYtDlpAsync(track, ct).ConfigureAwait(false);
        return Cache(track.Id, url, await BuildManifestAsync(url, ct).ConfigureAwait(false));
    }

    private async Task<string?> BuildManifestAsync(string url, CancellationToken ct)
    {
        try
        {
            return await _dashManifest.TryBuildAsync(url, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private ResolvedStream Cache(string trackId, string url, string? dashManifest)
    {
        var stream = new ResolvedStream
        {
            Url = url,
            ExpiresAtUtc = ParseExpiry(url),
            DashManifest = dashManifest,
        };
        _streamCache[trackId] = (stream, DateTimeOffset.UtcNow);
        return stream;
    }

    private async Task<string> ResolveWithYtDlpAsync(Track track, CancellationToken ct)
    {
        return await TryResolveAsync(StreamPlayerClient, ct).ConfigureAwait(false)
            ?? await TryResolveAsync(null, ct).ConfigureAwait(false)
            ?? throw new YtDlpException($"yt-dlp returned no stream URL for '{track.Title}'.");

        async Task<string?> TryResolveAsync(string? playerClient, CancellationToken token)
        {
            var args = new List<string>
            {
                "-f", AudioFormat,
                "-g",
                "--no-playlist",
                "--no-warnings",
                "--ignore-config",
                "--force-ipv4",

                "--socket-timeout", "15",
            };

            if (playerClient is not null)
            {
                args.Add("--extractor-args");
                args.Add($"youtube:player_client={playerClient}");
            }

            args.Add(track.SourceUrl);

            var ytDlp = await _locator.GetYtDlpPathAsync(token).ConfigureAwait(false);
            var result = await _runner.RunAsync(ytDlp, args, token).ConfigureAwait(false);

            if (!result.Success)
            {
                if (playerClient is null)
                    throw new YtDlpException($"Could not resolve '{track.Title}' (exit {result.ExitCode}): {Truncate(result.StdErr)}");
                return null;
            }

            return FirstNonEmptyLine(result.StdOut);
        }
    }

    private ResolvedStream? TryGetCachedStream(string trackId)
    {
        if (!_streamCache.TryGetValue(trackId, out var entry))
            return null;

        var stale = entry.Stream.ExpiresAtUtc is not null
            ? entry.Stream.IsExpired(StreamSafetyMargin)
            : DateTimeOffset.UtcNow - entry.ResolvedAt > StreamAssumedLifetime;

        if (!stale)
            return entry.Stream;

        _streamCache.TryRemove(trackId, out _);
        return null;
    }

    public async IAsyncEnumerable<DownloadProgress> DownloadAsync(
        Track track,
        string outputTemplate,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var args = new List<string> { track.SourceUrl };
        if (!string.IsNullOrEmpty(_ffmpegPath))
        {

            args.Add("-f"); args.Add("bestaudio/best");
            args.Add("-x");
            args.Add("--audio-format"); args.Add("m4a");
            args.Add("--audio-quality"); args.Add("0");
            args.Add("--ffmpeg-location"); args.Add(_ffmpegPath);
        }
        else
        {

            args.Add("-f"); args.Add(AudioFormat);
        }
        args.Add("-o"); args.Add(outputTemplate);
        args.Add("--newline");
        args.Add("--no-playlist");
        args.Add("--no-warnings");
        args.Add("--ignore-config");
        args.Add("--force-ipv4");

        args.Add("--extractor-args"); args.Add($"youtube:player_client={StreamPlayerClient}");

        args.Add("--concurrent-fragments"); args.Add("4");

        var ytDlp = await _locator.GetYtDlpPathAsync(ct).ConfigureAwait(false);
        string? destination = null;
        await foreach (var line in _runner.StreamLinesAsync(ytDlp, args, ct).ConfigureAwait(false))
        {
            var destIndex = line.IndexOf("Destination:", StringComparison.Ordinal);
            if (destIndex >= 0)
                destination = line[(destIndex + "Destination:".Length)..].Trim();

            if (TryParsePercent(line) is { } percent)
                yield return new DownloadProgress(percent, destination, line);
        }
    }

    private static Track? TryParseSearchEntry(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var id = GetString(root, "id");
            if (string.IsNullOrEmpty(id))
                return null;

            var title = GetString(root, "title") ?? "(unknown title)";

            var artist = GetString(root, "uploader") ?? GetString(root, "channel");

            if (artist is not null && artist.EndsWith(" - Topic", StringComparison.Ordinal))
                artist = artist[..^" - Topic".Length];

            TimeSpan? duration = null;
            if (root.TryGetProperty("duration", out var durEl)
                && durEl.ValueKind == JsonValueKind.Number
                && durEl.TryGetDouble(out var seconds)
                && seconds > 0)
            {
                duration = TimeSpan.FromSeconds(seconds);
            }

            return new Track
            {
                Id = id,
                Title = title,
                Artist = artist,
                Duration = duration,
                ThumbnailUrl = PickThumbnail(root, id),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string PickThumbnail(JsonElement root, string id)
    {
        if (root.TryGetProperty("thumbnails", out var thumbs) && thumbs.ValueKind == JsonValueKind.Array)
        {
            string? best = null;
            var bestWidth = -1;
            foreach (var thumb in thumbs.EnumerateArray())
            {
                var url = GetString(thumb, "url");
                if (url is null)
                    continue;
                var width = thumb.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number
                    ? w.GetInt32()
                    : 0;
                if (width >= bestWidth)
                {
                    bestWidth = width;
                    best = url;
                }
            }
            if (best is not null)
                return best;
        }

        return GetString(root, "thumbnail")
            ?? $"https://i.ytimg.com/vi/{id}/hqdefault.jpg";
    }

    private static DateTimeOffset? ParseExpiry(string url)
    {
        const string key = "expire=";
        var index = url.IndexOf(key, StringComparison.Ordinal);
        if (index < 0)
            return null;

        var start = index + key.Length;
        var end = start;
        while (end < url.Length && char.IsDigit(url[end]))
            end++;
        if (end == start)
            return null;

        return long.TryParse(url.AsSpan(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : null;
    }

    private static double? TryParsePercent(string line)
    {
        var match = PercentRegex().Match(line);
        return match.Success
            && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)
            ? pct
            : null;
    }

    private static string? FirstNonEmptyLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
                return trimmed;
        }
        return null;
    }

    private static string Truncate(string text, int max = 500)
    {
        text = text.Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?)%")]
    private static partial Regex PercentRegex();
}
