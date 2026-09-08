using System.Net.Http;
using System.Text.Json;
using MusicApp.Core.Models;

namespace MusicApp.Core.Services;

public sealed class YouTubeMusicBrowseClient
{
    private const string Endpoint = "https://music.youtube.com/youtubei/v1/browse?prettyPrint=false";

    private static readonly HttpClient Http = YouTubeMusicJson.CreateClient();

    public async Task<ArtistDetails?> GetArtistAsync(string browseId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(browseId))
            return null;

        using var doc = await BrowseAsync(browseId, ct).ConfigureAwait(false);
        var root = doc.RootElement;

        if (YouTubeMusicJson.Find(root, "musicImmersiveHeaderRenderer", maxDepth: 10) is not { } header)
            return null;

        var name = YouTubeMusicJson.Text(header, "title");
        if (string.IsNullOrEmpty(name))
            return null;

        return new ArtistDetails
        {
            BrowseId = browseId,
            Name = name,

            Subtitle = YouTubeMusicJson.Text(header, "monthlyListenerCount")
                       ?? SubscriberText(header),
            Description = YouTubeMusicJson.Text(header, "description"),
            BannerUrl = header.TryGetProperty("thumbnail", out var art)
                ? YouTubeMusicJson.Largest(art)
                : null,

            TopSongs = ListRows(root, includeAlbum: true),
            Releases = Releases(root),
            Similar = SimilarArtists(root, browseId),
            SongsPlaylistId = SongsPlaylistId(root),
        };
    }

    public async Task<AlbumDetails?> GetAlbumAsync(string browseId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(browseId))
            return null;

        using var doc = await BrowseAsync(browseId, ct).ConfigureAwait(false);
        var root = doc.RootElement;

        if (YouTubeMusicJson.Find(root, "musicResponsiveHeaderRenderer", maxDepth: 10) is not { } header)
            return null;

        var title = YouTubeMusicJson.Text(header, "title");
        if (string.IsNullOrEmpty(title))
            return null;

        var subtitle = Segments(header, "subtitle");
        var kind = subtitle.FirstOrDefault(YouTubeMusicJson.IsTypeLabel);
        var year = YouTubeMusicJson.ParseYear(subtitle.LastOrDefault());

        var creditRun = header.TryGetProperty("straplineTextOne", out var strapline)
            ? YouTubeMusicJson.Runs(strapline).FirstOrDefault()
            : default;

        var cover = header.TryGetProperty("thumbnail", out var art) ? YouTubeMusicJson.Largest(art) : null;
        var artist = YouTubeMusicJson.Text(header, "straplineTextOne");

        var tracks = ListRows(root, includeAlbum: false)
            .Select(t => new Track
            {
                Id = t.Id,
                Title = t.Title,
                Artist = t.Artist ?? artist,
                Album = title,
                Duration = t.Duration,
                ReleaseYear = year,
                ThumbnailUrl = t.ThumbnailUrl ?? cover,
            })
            .ToList();

        return new AlbumDetails
        {
            BrowseId = browseId,
            Title = title,
            Artist = artist,
            ArtistBrowseId = creditRun.ValueKind == JsonValueKind.Object
                ? YouTubeMusicJson.BrowseId(creditRun, "MUSIC_PAGE_TYPE_ARTIST")
                : null,
            Kind = kind,
            Year = year,
            ThumbnailUrl = cover,
            Details = YouTubeMusicJson.Text(header, "secondSubtitle"),
            Tracks = tracks,
        };
    }

    public async Task<PlaylistDetails?> GetPlaylistAsync(string urlOrId, CancellationToken ct = default)
    {
        if (PlaylistId(urlOrId) is not { } id)
            return null;

        using var doc = await BrowseAsync(id.StartsWith("VL", StringComparison.Ordinal) ? id : "VL" + id, ct)
            .ConfigureAwait(false);
        var root = doc.RootElement;

        var header = YouTubeMusicJson.Find(root, "musicResponsiveHeaderRenderer", maxDepth: 12)
                     ?? YouTubeMusicJson.Find(root, "musicDetailHeaderRenderer", maxDepth: 12)
                     ?? YouTubeMusicJson.Find(root, "musicEditablePlaylistDetailHeaderRenderer", maxDepth: 12);

        var title = header is { } h ? YouTubeMusicJson.Text(h, "title") : null;
        title ??= YouTubeMusicJson.Find(root, "microformat", maxDepth: 4) is { } micro
            ? YouTubeMusicJson.FindFirstString(micro, "title", maxDepth: 4)
            : null;

        var tracks = ListRows(root, includeAlbum: true);

        if (string.IsNullOrEmpty(title) && tracks.Count == 0)
            return null;

        const int MaxTracks = 2000;
        var seen = new HashSet<string>(tracks.Select(t => t.Id), StringComparer.Ordinal);
        var token = NextContinuation(root);
        while (tracks.Count < MaxTracks && token is { Length: > 0 })
        {
            JsonDocument next;
            try
            {
                next = await ContinueAsync(token, ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {

                break;
            }

            using (next)
            {
                var added = 0;
                foreach (var track in ListRows(next.RootElement, includeAlbum: true))
                    if (seen.Add(track.Id))
                    {
                        tracks.Add(track);
                        added++;
                    }

                if (added == 0)
                    break;

                token = NextContinuation(next.RootElement);
            }
        }

        return new PlaylistDetails
        {
            Title = string.IsNullOrEmpty(title) ? "Imported playlist" : title,
            Author = header is { } a ? YouTubeMusicJson.Text(a, "straplineTextOne") : null,
            ThumbnailUrl = header is { } t2 ? YouTubeMusicJson.PickThumbnail(t2) : tracks.FirstOrDefault()?.ThumbnailUrl,
            Tracks = tracks,
        };
    }

    public async Task<IReadOnlyList<Track>> GetTrendingAsync(int limit = 20, CancellationToken ct = default)
    {
        string? chart;
        using (var charts = await BrowseAsync("FEmusic_charts", ct).ConfigureAwait(false))
            chart = FirstChartPlaylist(charts.RootElement);

        if (chart is null)
            return Array.Empty<Track>();

        using var doc = await BrowseAsync(chart, ct).ConfigureAwait(false);
        return ListRows(doc.RootElement, includeAlbum: true).Take(limit).ToList();
    }

    public async Task<IReadOnlyList<Track>> GetNewReleasesAsync(int limit = 20, CancellationToken ct = default)
    {
        using var doc = await BrowseAsync("FEmusic_new_releases", ct).ConfigureAwait(false);
        return TileTracks(doc.RootElement).Take(limit).ToList();
    }

    private static string? FirstChartPlaylist(JsonElement root)
    {
        var tiles = new List<JsonElement>();
        YouTubeMusicJson.FindAll(root, "musicTwoRowItemRenderer", tiles);

        foreach (var tile in tiles)
        {
            if (tile.TryGetProperty("navigationEndpoint", out var nav)
                && nav.TryGetProperty("browseEndpoint", out var browse)
                && YouTubeMusicJson.GetString(browse, "browseId") is { Length: > 2 } id
                && id.StartsWith("VL", StringComparison.Ordinal))
            {
                return id;
            }
        }
        return null;
    }

    private static List<Track> TileTracks(JsonElement root)
    {
        var tiles = new List<JsonElement>();
        YouTubeMusicJson.FindAll(root, "musicTwoRowItemRenderer", tiles);

        var tracks = new List<Track>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tile in tiles)
        {
            if (!tile.TryGetProperty("navigationEndpoint", out var nav)
                || !nav.TryGetProperty("watchEndpoint", out var watch)
                || YouTubeMusicJson.GetString(watch, "videoId") is not { Length: > 0 } id
                || !seen.Add(id))
            {
                continue;
            }

            var title = YouTubeMusicJson.Text(tile, "title");
            if (string.IsNullOrEmpty(title))
                continue;

            tracks.Add(new Track
            {
                Id = id,
                Title = title,
                Artist = TileArtist(tile),
                ThumbnailUrl = YouTubeMusicJson.PickThumbnail(tile),
            });
        }
        return tracks;
    }

    private static string? TileArtist(JsonElement tile)
    {
        if (!tile.TryGetProperty("subtitle", out var subtitle))
            return null;

        string? first = null;
        foreach (var run in YouTubeMusicJson.Runs(subtitle))
        {
            if (YouTubeMusicJson.GetString(run, "text") is not { } text || text.Trim().Length == 0)
                continue;
            var trimmed = text.Trim();
            if (YouTubeMusicJson.Separators.Contains(trimmed))
                continue;

            first ??= trimmed;
            if (run.TryGetProperty("navigationEndpoint", out var nav)
                && nav.TryGetProperty("browseEndpoint", out var browse)
                && YouTubeMusicJson.PageType(browse) == "MUSIC_PAGE_TYPE_ARTIST")
            {
                return trimmed;
            }
        }
        return first;
    }

    internal static string? PlaylistId(string urlOrId)
    {
        var text = urlOrId?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        var listIndex = text.IndexOf("list=", StringComparison.OrdinalIgnoreCase);
        if (listIndex >= 0)
        {
            var value = text[(listIndex + 5)..];
            var end = value.IndexOfAny(new[] { '&', '#', '?', '/' });
            if (end >= 0)
                value = value[..end];
            text = value;
        }

        return text.Length >= 10 && text.All(c => char.IsLetterOrDigit(c) || c is '_' or '-')
            ? text
            : null;
    }

    private static async Task<JsonDocument> BrowseAsync(string browseId, CancellationToken ct)
    {
        var escaped = JsonEncodedText.Encode(browseId).ToString();
        return await PostAsync(
            $"{{{YouTubeMusicJson.ContextJson},\"browseId\":\"{escaped}\"}}", ct).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> PostAsync(string body, CancellationToken ct)
    {
        using var content = YouTubeMusicJson.JsonBody(body);

        using var response = await Http.PostAsync(Endpoint, content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ContinueAsync(string token, CancellationToken ct)
    {
        var escaped = JsonEncodedText.Encode(token).ToString();
        return await PostAsync(
            $"{{{YouTubeMusicJson.ContextJson},\"continuation\":\"{escaped}\"}}", ct).ConfigureAwait(false);
    }

    private static string? NextContinuation(JsonElement root)
    {
        if (YouTubeMusicJson.Find(root, "continuationItemRenderer", maxDepth: 24) is { } item
            && YouTubeMusicJson.Find(item, "continuationCommand", maxDepth: 6) is { } command
            && YouTubeMusicJson.GetString(command, "token") is { Length: > 0 } token)
        {
            return token;
        }

        return YouTubeMusicJson.Find(root, "nextContinuationData", maxDepth: 24) is { } legacy
            ? YouTubeMusicJson.GetString(legacy, "continuation")
            : null;
    }

    private static List<Track> ListRows(JsonElement root, bool includeAlbum)
    {
        var rows = new List<JsonElement>();
        YouTubeMusicJson.FindAll(root, "musicResponsiveListItemRenderer", rows);

        var tracks = new List<Track>(rows.Count);
        foreach (var row in rows)
            if (YouTubeMusicJson.ParseListItemTrack(row, includeAlbum) is { } track)
                tracks.Add(track);
        return tracks;
    }

    private static List<AlbumResult> Releases(JsonElement root)
    {
        var tiles = new List<JsonElement>();
        YouTubeMusicJson.FindAll(root, "musicTwoRowItemRenderer", tiles);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var releases = new List<AlbumResult>();
        foreach (var tile in tiles)
        {
            if (YouTubeMusicJson.BrowseId(tile, "MUSIC_PAGE_TYPE_ALBUM") is not { } browseId
                || !seen.Add(browseId))
            {
                continue;
            }

            var title = YouTubeMusicJson.Text(tile, "title");
            if (string.IsNullOrEmpty(title))
                continue;

            var parts = Segments(tile, "subtitle");
            releases.Add(new AlbumResult
            {
                BrowseId = browseId,
                Title = title,

                Artist = null,
                Kind = parts.FirstOrDefault(YouTubeMusicJson.IsTypeLabel),
                Year = YouTubeMusicJson.ParseYear(parts.LastOrDefault()),
                ThumbnailUrl = YouTubeMusicJson.PickThumbnail(tile),
            });
        }
        return releases;
    }

    private static string? SongsPlaylistId(JsonElement root)
    {
        var shelves = new List<JsonElement>();
        YouTubeMusicJson.FindAll(root, "musicShelfRenderer", shelves);

        foreach (var shelf in shelves)
        {
            if (!shelf.TryGetProperty("bottomEndpoint", out var bottom))
                continue;

            if (YouTubeMusicJson.Find(bottom, "browseEndpoint", maxDepth: 4) is not { } endpoint
                || YouTubeMusicJson.GetString(endpoint, "browseId") is not { Length: > 2 } id)
            {
                continue;
            }

            if (id.StartsWith("VL", StringComparison.Ordinal) || id.StartsWith("PL", StringComparison.Ordinal)
                || id.StartsWith("OLAK", StringComparison.Ordinal))
            {
                return id;
            }
        }

        return null;
    }

    private static List<ArtistResult> SimilarArtists(JsonElement root, string selfBrowseId)
    {
        var tiles = new List<JsonElement>();
        YouTubeMusicJson.FindAll(root, "musicTwoRowItemRenderer", tiles);

        var seen = new HashSet<string>(StringComparer.Ordinal) { selfBrowseId };
        var artists = new List<ArtistResult>();
        foreach (var tile in tiles)
        {
            if (YouTubeMusicJson.BrowseId(tile, "MUSIC_PAGE_TYPE_ARTIST") is not { } browseId
                || !seen.Add(browseId))
            {
                continue;
            }

            var name = YouTubeMusicJson.Text(tile, "title");
            if (string.IsNullOrEmpty(name))
                continue;

            artists.Add(new ArtistResult
            {
                BrowseId = browseId,
                Name = name,
                Subtitle = Segments(tile, "subtitle").FirstOrDefault(),
                ThumbnailUrl = YouTubeMusicJson.PickThumbnail(tile),
            });

            if (artists.Count >= 12)
                break;
        }
        return artists;
    }

    private static List<string> Segments(JsonElement parent, string property)
    {
        var segments = new List<string>();
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(property, out var node))
            return segments;

        foreach (var run in YouTubeMusicJson.Runs(node))
        {
            if (YouTubeMusicJson.GetString(run, "text") is not { } text)
                continue;
            var trimmed = text.Trim();
            if (trimmed.Length > 0 && !YouTubeMusicJson.Separators.Contains(trimmed))
                segments.Add(trimmed);
        }
        return segments;
    }

    private static string? SubscriberText(JsonElement header) =>
        header.TryGetProperty("subscriptionButton", out var button)
        && button.TryGetProperty("subscribeButtonRenderer", out var renderer)
        && YouTubeMusicJson.Text(renderer, "subscriberCountText") is { Length: > 0 } count
            ? $"{count} subscribers"
            : null;
}
