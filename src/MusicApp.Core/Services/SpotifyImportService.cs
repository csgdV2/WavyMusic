using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MusicApp.Core.Abstractions;
using MusicApp.Core.Models;

namespace MusicApp.Core.Services;

public sealed class SpotifyImportService
{
    private const int MaxTracks = 200;
    private const int Parallelism = 4;

    private static readonly Regex LinkPattern = new(
        @"(?:open\.spotify\.com/(?:intl-[a-z-]+/)?|spotify:)(playlist|album)[/:]([A-Za-z0-9]{16,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NextData = new(
        @"<script\s+id=""__NEXT_DATA__""[^>]*>(.*?)</script>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly IYtDlpService _catalogue;
    private readonly HttpClient _http;

    public SpotifyImportService(IYtDlpService catalogue)
    {
        _catalogue = catalogue;
        _http = new HttpClient(Ipv4Http.CreateHandler())
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    public static bool LooksLikeSpotify(string? link) =>
        !string.IsNullOrWhiteSpace(link) && LinkPattern.IsMatch(link);

    public async Task<PlaylistDetails?> GetPlaylistAsync(string link, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(link))
            return null;

        var match = LinkPattern.Match(link);
        if (!match.Success)
            return null;

        var kind = match.Groups[1].Value.ToLowerInvariant();
        var id = match.Groups[2].Value;

        var entity = await FetchEntityAsync(kind, id, ct).ConfigureAwait(false);
        if (entity is null)
            return null;

        var title = Text(Prop(entity, "name")) ?? Text(Prop(entity, "title")) ?? "Spotify playlist";
        var author = Text(Prop(entity, "subtitle"))
                     ?? Text(Prop(At(Prop(entity, "artists"), 0), "name"));
        var cover = Text(Prop(At(Prop(Prop(entity, "coverArt"), "sources"), 0), "url"))
                    ?? Text(Prop(At(Prop(Prop(entity, "visualIdentity"), "image"), 0), "url"));

        var wanted = ReadEntries(entity, kind == "album" ? author : null);
        if (wanted.Count == 0)
            return new PlaylistDetails { Title = title, Author = author, ThumbnailUrl = cover };

        var resolved = await ResolveAsync(wanted, ct).ConfigureAwait(false);

        return new PlaylistDetails
        {
            Title = title,
            Author = author,
            ThumbnailUrl = cover,
            Tracks = resolved,
        };
    }

    private async Task<JsonNode?> FetchEntityAsync(string kind, string id, CancellationToken ct)
    {
        var html = await GetStringAsync($"https://open.spotify.com/embed/{kind}/{id}", ct).ConfigureAwait(false);
        if (html is null)
            return null;

        var script = NextData.Match(html);
        if (!script.Success)
            return null;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(script.Groups[1].Value);
        }
        catch (JsonException)
        {
            return null;
        }

        var pageProps = Prop(Prop(root, "props"), "pageProps");
        var data = Prop(Prop(pageProps, "state"), "data") ?? Prop(pageProps, "data");

        return Prop(data, "entity") ?? data ?? Prop(pageProps, "entity");
    }

    private static JsonNode? Prop(JsonNode? node, string name) =>
        node is JsonObject obj && obj.TryGetPropertyValue(name, out var value) ? value : null;

    private static JsonNode? At(JsonNode? node, int index) =>
        node is JsonArray array && index >= 0 && index < array.Count ? array[index] : null;

    private async Task<string?> GetStringAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)
                : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    private static List<(string Title, string Artist)> ReadEntries(JsonNode entity, string? fallbackArtist)
    {
        var entries = new List<(string, string)>();

        foreach (var item in Items(entity))
        {
            if (entries.Count >= MaxTracks)
                break;

            var title = Text(Prop(item, "title")) ?? Text(Prop(item, "name"));
            if (title is null)
                continue;

            var artist = Artists(item) ?? fallbackArtist ?? string.Empty;
            entries.Add((title, artist));
        }

        return entries;
    }

    private static IEnumerable<JsonNode> Items(JsonNode entity)
    {
        var trackList = Prop(entity, "trackList");
        var tracks = Prop(entity, "tracks");

        var lists = new[]
        {
            trackList as JsonArray,
            Prop(trackList, "items") as JsonArray,
            Prop(tracks, "items") as JsonArray,
            tracks as JsonArray,
        };

        foreach (var array in lists)
        {
            if (array is null || array.Count == 0)
                continue;

            foreach (var item in array)
            {
                if (item is null)
                    continue;

                yield return Prop(item, "track") ?? item;
            }

            yield break;
        }
    }

    private static string? Artists(JsonNode item)
    {
        if (Prop(item, "artists") is JsonArray artists)
        {
            var names = artists
                .Select(a => Text(Prop(a, "name")))
                .Where(name => name is { Length: > 0 })
                .ToList();

            if (names.Count > 0)
                return string.Join(", ", names);
        }

        return Text(Prop(item, "subtitle"));
    }

    private async Task<List<Track>> ResolveAsync(
        List<(string Title, string Artist)> wanted, CancellationToken ct)
    {
        var found = new Track?[wanted.Count];
        using var gate = new SemaphoreSlim(Parallelism);

        var work = wanted.Select(async (entry, at) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                found[at] = await FindAsync(entry.Title, entry.Artist, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(work).ConfigureAwait(false);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var tracks = new List<Track>(wanted.Count);
        foreach (var track in found)
            if (track is { Id.Length: > 0 } && seen.Add(track.Id))
                tracks.Add(track);

        return tracks;
    }

    private async Task<Track?> FindAsync(string title, string artist, CancellationToken ct)
    {
        var query = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";

        try
        {
            var hits = await _catalogue.SearchAsync(query, SearchFilter.Songs, 5, ct).ConfigureAwait(false);
            if (hits.Count == 0)
                return null;

            return hits.OrderByDescending(t => Score(t, title, artist)).First();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    private static int Score(Track track, string title, string artist)
    {
        var score = 0;

        if (Similar(track.Title, title))
            score += 3;
        else if (Contains(track.Title, title) || Contains(title, track.Title))
            score += 1;

        if (!string.IsNullOrWhiteSpace(artist))
        {
            if (Similar(track.Artist, artist))
                score += 2;
            else if (Contains(track.Artist, artist) || Contains(artist, track.Artist))
                score += 1;
        }

        return score;
    }

    private static bool Similar(string? a, string? b) =>
        string.Equals(Flatten(a), Flatten(b), StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string? a, string? b)
    {
        var left = Flatten(a);
        var right = Flatten(b);
        return left.Length > 0 && right.Length > 0
            && left.Contains(right, StringComparison.OrdinalIgnoreCase);
    }

    private static string Flatten(string? value) =>
        value is null
            ? string.Empty
            : new string(value.Where(char.IsLetterOrDigit).ToArray());

    private static string? Text(JsonNode? node)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
            return null;

        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
