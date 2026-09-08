using System.Net.Http;
using System.Text.Json;
using MusicApp.Core.Models;

namespace MusicApp.Core.Services;

public sealed class YouTubeMusicRadioClient
{
    private const string Endpoint = "https://music.youtube.com/youtubei/v1/next?prettyPrint=false";

    private static readonly HttpClient Http = YouTubeMusicJson.CreateClient();

    public async Task<IReadOnlyList<Track>> GetRadioAsync(string videoId, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoId) || limit <= 0)
            return Array.Empty<Track>();

        var escaped = JsonEncodedText.Encode(videoId).ToString();
        var body = $"{{{YouTubeMusicJson.ContextJson},\"videoId\":\"{escaped}\"," +
                   $"\"playlistId\":\"RDAMVM{escaped}\",\"isAudioOnly\":true," +
                   "\"enablePersistentPlaylistPanel\":true,\"tunerSettingValue\":\"AUTOMIX_SETTING_NORMAL\"}";

        using var content = YouTubeMusicJson.JsonBody(body);
        using var response = await Http.PostAsync(Endpoint, content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var renderers = new List<JsonElement>();
        YouTubeMusicJson.FindAll(doc.RootElement, "playlistPanelVideoRenderer", renderers);

        var tracks = new List<Track>(limit);
        var seen = new HashSet<string>(StringComparer.Ordinal) { videoId };
        foreach (var renderer in renderers)
        {
            if (Parse(renderer) is not { } track || !seen.Add(track.Id))
                continue;

            tracks.Add(track);
            if (tracks.Count >= limit)
                break;
        }
        return tracks;
    }

    private static Track? Parse(JsonElement renderer)
    {
        if (YouTubeMusicJson.GetString(renderer, "videoId") is not { Length: > 0 } id)
            return null;

        var title = YouTubeMusicJson.Text(renderer, "title");
        if (string.IsNullOrEmpty(title))
            return null;

        var (artist, album) = Byline(renderer);
        return new Track
        {
            Id = id,
            Title = title,
            Artist = artist,
            Album = album,
            Duration = YouTubeMusicJson.ParseDuration(YouTubeMusicJson.Text(renderer, "lengthText")),
            ThumbnailUrl = YouTubeMusicJson.PickThumbnail(renderer),
        };
    }

    private static (string? Artist, string? Album) Byline(JsonElement renderer)
    {
        string? artist = null, album = null, first = null;

        foreach (var property in new[] { "longBylineText", "shortBylineText" })
        {
            if (renderer.ValueKind != JsonValueKind.Object || !renderer.TryGetProperty(property, out var byline))
                continue;

            foreach (var run in YouTubeMusicJson.Runs(byline))
            {
                if (YouTubeMusicJson.GetString(run, "text")?.Trim() is not { Length: > 0 } text)
                    continue;
                if (YouTubeMusicJson.Separators.Contains(text))
                    continue;

                first ??= text;

                if (!run.TryGetProperty("navigationEndpoint", out var nav)
                    || !nav.TryGetProperty("browseEndpoint", out var browse))
                {
                    continue;
                }

                switch (YouTubeMusicJson.PageType(browse))
                {
                    case "MUSIC_PAGE_TYPE_ARTIST":
                        artist ??= text;
                        break;
                    case "MUSIC_PAGE_TYPE_ALBUM":
                        album ??= text;
                        break;
                }
            }

            if (artist is not null)
                break;
        }

        return (artist ?? first, album);
    }
}
