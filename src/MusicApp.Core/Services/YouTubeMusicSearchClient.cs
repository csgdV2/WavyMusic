using System.Net.Http;
using System.Text.Json;
using MusicApp.Core.Models;

namespace MusicApp.Core.Services;

public sealed class YouTubeMusicSearchClient
{
    private const string Endpoint = "https://music.youtube.com/youtubei/v1/search?prettyPrint=false";

    private static readonly Dictionary<SearchFilter, string> FilterParams = new()
    {
        [SearchFilter.Songs] = "EgWKAQIIAWoKEAkQBRAKEAMQBA==",
        [SearchFilter.Videos] = "EgWKAQIQAWoKEAkQChAFEAMQBA==",
        [SearchFilter.Artists] = "EgWKAQIgAWoKEAkQChAFEAMQBA==",
        [SearchFilter.Albums] = "EgWKAQIYAWoKEAkQChAFEAMQBA==",
        [SearchFilter.Playlists] = "EgWKAQIoAWoKEAkQChAFEAMQBA==",
    };

    private static readonly HttpClient Http = YouTubeMusicJson.CreateClient();

    public Task<IReadOnlyList<Track>> SearchTracksAsync(
        string query, SearchFilter filter, int limit, CancellationToken ct = default) =>
        QueryAsync(query, filter, limit, item => ParseTrack(item, filter), ct);

    public Task<IReadOnlyList<ArtistResult>> SearchArtistsAsync(
        string query, int limit, CancellationToken ct = default) =>
        QueryAsync(query, SearchFilter.Artists, limit, ParseArtist, ct);

    public Task<IReadOnlyList<AlbumResult>> SearchAlbumsAsync(
        string query, int limit, CancellationToken ct = default) =>
        QueryAsync(query, SearchFilter.Albums, limit, ParseAlbum, ct);

    public Task<IReadOnlyList<PlaylistResult>> SearchPlaylistsAsync(
        string query, int limit, CancellationToken ct = default) =>
        QueryAsync(query, SearchFilter.Playlists, limit, ParsePlaylist, ct);

    private async Task<IReadOnlyList<T>> QueryAsync<T>(
        string query, SearchFilter filter, int limit, Func<JsonElement, T?> parse, CancellationToken ct)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<T>();

        using var content = YouTubeMusicJson.JsonBody(BuildRequestJson(query, filter));
        using var response = await Http.PostAsync(Endpoint, content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var results = new List<T>(limit);
        Collect(doc.RootElement, results, parse, limit);
        return results;
    }

    private static string BuildRequestJson(string query, SearchFilter filter)
    {

        var escaped = JsonEncodedText.Encode(query).ToString();
        return $"{{{YouTubeMusicJson.ContextJson},\"query\":\"{escaped}\"," +
               $"\"params\":\"{FilterParams[filter]}\"}}";
    }

    private static void Collect<T>(JsonElement element, List<T> results, Func<JsonElement, T?> parse, int limit)
        where T : class
    {
        if (results.Count >= limit)
            return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("musicResponsiveListItemRenderer"))
                    {
                        if (parse(property.Value) is { } parsed)
                            results.Add(parsed);
                        continue;
                    }
                    Collect(property.Value, results, parse, limit);
                    if (results.Count >= limit)
                        return;
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, results, parse, limit);
                    if (results.Count >= limit)
                        return;
                }
                break;
        }
    }

    private static Track? ParseTrack(JsonElement item, SearchFilter filter) =>
        YouTubeMusicJson.ParseListItemTrack(item, includeAlbum: filter == SearchFilter.Songs);

    private static ArtistResult? ParseArtist(JsonElement item)
    {
        if (YouTubeMusicJson.BrowseId(item, "MUSIC_PAGE_TYPE_ARTIST") is not { } browseId)
            return null;

        var name = YouTubeMusicJson.ColumnText(item, 0);
        if (string.IsNullOrEmpty(name))
            return null;

        var parts = YouTubeMusicJson.SubtitleParts(item, 1);
        return new ArtistResult
        {
            BrowseId = browseId,
            Name = name,
            Subtitle = parts.Plain.LastOrDefault(p => !YouTubeMusicJson.IsTypeLabel(p)),
            ThumbnailUrl = YouTubeMusicJson.PickThumbnail(item),
        };
    }

    private static PlaylistResult? ParsePlaylist(JsonElement item)
    {
        if (YouTubeMusicJson.BrowseId(item, "MUSIC_PAGE_TYPE_PLAYLIST") is not { } browseId)
            return null;

        var title = YouTubeMusicJson.ColumnText(item, 0);
        if (string.IsNullOrEmpty(title))
            return null;

        var parts = YouTubeMusicJson.SubtitleParts(item, 1);
        var author = parts.Plain.FirstOrDefault(p =>
            !YouTubeMusicJson.IsTypeLabel(p) && p != parts.Trailing);

        return new PlaylistResult
        {
            BrowseId = browseId,
            Title = title,
            Author = author,
            Detail = parts.Trailing != author ? parts.Trailing : null,
            ThumbnailUrl = YouTubeMusicJson.PickThumbnail(item),
        };
    }

    private static AlbumResult? ParseAlbum(JsonElement item)
    {
        if (YouTubeMusicJson.BrowseId(item, "MUSIC_PAGE_TYPE_ALBUM") is not { } browseId)
            return null;

        var title = YouTubeMusicJson.ColumnText(item, 0);
        if (string.IsNullOrEmpty(title))
            return null;

        var parts = YouTubeMusicJson.SubtitleParts(item, 1);
        return new AlbumResult
        {
            BrowseId = browseId,
            Title = title,
            Artist = parts.Artist
                     ?? parts.Plain.FirstOrDefault(p => !YouTubeMusicJson.IsTypeLabel(p) && p != parts.Trailing),
            Kind = parts.Plain.FirstOrDefault(YouTubeMusicJson.IsTypeLabel),
            Year = YouTubeMusicJson.ParseYear(parts.Trailing),
            ThumbnailUrl = YouTubeMusicJson.PickThumbnail(item),
        };
    }
}
