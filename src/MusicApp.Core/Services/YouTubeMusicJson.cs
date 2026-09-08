using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using MusicApp.Core.Models;

namespace MusicApp.Core.Services;

internal static class YouTubeMusicJson
{
    public const string ClientName = "WEB_REMIX";
    public const string ClientVersion = "1.20240724.00";

    public static readonly string[] Separators = { "•", "·", "|" };

    public const string ContextJson =
        "\"context\":{\"client\":{\"clientName\":\"" + ClientName +
        "\",\"clientVersion\":\"" + ClientVersion + "\",\"hl\":\"en\",\"gl\":\"US\"}}";

    public static HttpClient CreateClient()
    {
        var handler = Ipv4Http.CreateHandler();
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        client.DefaultRequestHeaders.Add("X-YouTube-Client-Name", "67");
        client.DefaultRequestHeaders.Add("X-YouTube-Client-Version", ClientVersion);
        client.DefaultRequestHeaders.Add("Origin", "https://music.youtube.com");
        client.DefaultRequestHeaders.Referrer = new Uri("https://music.youtube.com/");
        return client;
    }

    public static StringContent JsonBody(string json)
    {
        var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return content;
    }

    public static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static string? Text(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.String)
            return node.GetString();
        if (node.ValueKind != JsonValueKind.Object)
            return null;

        if (GetString(node, "simpleText") is { Length: > 0 } simple)
            return simple;

        var builder = new StringBuilder();
        foreach (var run in Runs(node))
            if (GetString(run, "text") is { } part)
                builder.Append(part);
        return builder.Length > 0 ? builder.ToString() : null;
    }

    public static string? Text(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var node)
            ? Text(node)
            : null;

    public static IEnumerable<JsonElement> Runs(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty("runs", out var runs)
            && runs.ValueKind == JsonValueKind.Array)
        {
            foreach (var run in runs.EnumerateArray())
                yield return run;
        }
    }

    public static JsonElement? Find(JsonElement element, string name, int maxDepth = 12, int depth = 0)
    {
        if (depth > maxDepth)
            return null;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals(name))
                        return property.Value;
                    if (Find(property.Value, name, maxDepth, depth + 1) is { } found)
                        return found;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (Find(item, name, maxDepth, depth + 1) is { } found)
                        return found;
                break;
        }
        return null;
    }

    public static string? FindFirstString(JsonElement element, string name, int maxDepth = 6) =>
        Find(element, name, maxDepth) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    public static void FindAll(JsonElement element, string name, List<JsonElement> into, int maxDepth = 24, int depth = 0)
    {
        if (depth > maxDepth)
            return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals(name))
                        into.Add(property.Value);
                    else
                        FindAll(property.Value, name, into, maxDepth, depth + 1);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    FindAll(item, name, into, maxDepth, depth + 1);
                break;
        }
    }

    public static string? PickThumbnail(JsonElement item)
    {
        foreach (var key in new[] { "thumbnail", "thumbnailRenderer" })
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(key, out var wrapper))
                continue;
            if (Largest(wrapper) is { } url)
                return url;
        }
        return null;
    }

    public static string? Largest(JsonElement wrapper)
    {
        if (Find(wrapper, "thumbnails", maxDepth: 5) is not { ValueKind: JsonValueKind.Array } thumbs)
            return null;

        string? best = null;
        var bestWidth = -1;
        foreach (var thumb in thumbs.EnumerateArray())
        {
            if (GetString(thumb, "url") is not { } url)
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
        return best;
    }

    public static string? PageType(JsonElement browseEndpoint) =>
        browseEndpoint.ValueKind == JsonValueKind.Object
        && browseEndpoint.TryGetProperty("browseEndpointContextSupportedConfigs", out var configs)
        && configs.TryGetProperty("browseEndpointContextMusicConfig", out var music)
            ? GetString(music, "pageType")
            : null;

    public static string? BrowseId(JsonElement node, string expectedPageType)
    {
        if (node.ValueKind != JsonValueKind.Object
            || !node.TryGetProperty("navigationEndpoint", out var nav)
            || !nav.TryGetProperty("browseEndpoint", out var browse)
            || GetString(browse, "browseId") is not { Length: > 0 } id)
        {
            return null;
        }

        return PageType(browse) == expectedPageType ? id : null;
    }

    public static string? ColumnText(JsonElement item, int index) =>
        Column(item, index) is { } column ? Text(column, "text") : null;

    public static JsonElement? Column(JsonElement item, int index) =>
        NthRenderer(item, "flexColumns", "musicResponsiveListItemFlexColumnRenderer", index);

    public static string? FixedColumnText(JsonElement item, int index) =>
        NthRenderer(item, "fixedColumns", "musicResponsiveListItemFixedColumnRenderer", index) is { } column
            ? Text(column, "text")
            : null;

    private static JsonElement? NthRenderer(JsonElement item, string arrayName, string rendererName, int index)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty(arrayName, out var columns)
            || columns.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var i = 0;
        foreach (var column in columns.EnumerateArray())
        {
            if (i++ != index)
                continue;
            return column.TryGetProperty(rendererName, out var renderer) ? renderer : null;
        }
        return null;
    }

    public static string? GetVideoId(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty("playlistItemData", out var data)
            && GetString(data, "videoId") is { Length: > 0 } fromData)
        {
            return fromData;
        }

        return FindFirstString(item, "videoId");
    }

    public static (string? Artist, string? Album, string? Trailing, List<string> Plain) SubtitleParts(
        JsonElement item, int columnIndex)
    {
        string? artist = null, album = null;
        var plain = new List<string>();

        if (Column(item, columnIndex) is { } column && column.TryGetProperty("text", out var text))
        {
            foreach (var run in Runs(text))
            {
                if (GetString(run, "text") is not { } value)
                    continue;

                var trimmed = value.Trim();
                if (trimmed.Length == 0 || Separators.Contains(trimmed))
                    continue;

                plain.Add(trimmed);

                if (!run.TryGetProperty("navigationEndpoint", out var nav)
                    || !nav.TryGetProperty("browseEndpoint", out var browse))
                {
                    continue;
                }

                switch (PageType(browse))
                {
                    case "MUSIC_PAGE_TYPE_ARTIST":
                        artist ??= trimmed;
                        break;
                    case "MUSIC_PAGE_TYPE_ALBUM":
                        album ??= trimmed;
                        break;
                }
            }
        }

        return (artist, album, plain.Count > 0 ? plain[^1] : null, plain);
    }

    public static Track? ParseListItemTrack(JsonElement item, bool includeAlbum)
    {
        if (GetVideoId(item) is not { } id)
            return null;

        var title = ColumnText(item, 0);
        if (string.IsNullOrEmpty(title))
            return null;

        var parts = SubtitleParts(item, 1);
        return new Track
        {
            Id = id,
            Title = title,
            Artist = parts.Artist ?? parts.Plain.FirstOrDefault(),
            Album = includeAlbum ? parts.Album : null,

            Duration = ParseDuration(FixedColumnText(item, 0)) ?? ParseDuration(parts.Trailing),
            ThumbnailUrl = PickThumbnail(item),
        };
    }

    public static TimeSpan? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var parts = text.Split(':');
        if (parts.Length is < 2 or > 3)
            return null;

        var total = 0L;
        foreach (var part in parts)
        {
            if (!int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 0)
                return null;
            total = total * 60 + value;
        }
        return total > 0 ? TimeSpan.FromSeconds(total) : null;
    }

    public static int? ParseYear(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
        && year is > 1900 and < 2200
            ? year
            : null;

    public static bool IsTypeLabel(string text) =>
        text is "Album" or "Single" or "EP" or "Artist" or "Song" or "Video" or "Playlist";
}
