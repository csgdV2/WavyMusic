using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MusicApp.Core.Models;

namespace MusicApp.Core.Services;

public sealed class YouTubeSearchClient
{

    private const string Endpoint = "https://www.youtube.com/youtubei/v1/search?key=AIzaSyAO_FJ2SlqU8Q4STEHLGCilw_Y9_11qcW8&prettyPrint=false";
    private const string ClientName = "WEB";
    private const string ClientVersion = "2.20240726.00";

    private const string VideosOnlyParams = "EgIQAQ%3D%3D";

    private static readonly HttpClient Http = CreateClient();

    public async Task<IReadOnlyList<Track>> SearchAsync(string query, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<Track>();

        var payload = BuildRequestJson(query);
        using var content = new StringContent(payload, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await Http.PostAsync(Endpoint, content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var tracks = new List<Track>(limit);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Collect(doc.RootElement, tracks, seen, limit);
        return tracks;
    }

    private static string BuildRequestJson(string query)
    {

        var escaped = JsonEncodedText.Encode(query).ToString();
        return $"{{\"context\":{{\"client\":{{\"clientName\":\"{ClientName}\",\"clientVersion\":\"{ClientVersion}\"," +
               $"\"hl\":\"en\",\"gl\":\"US\"}}}},\"query\":\"{escaped}\",\"params\":\"{VideosOnlyParams}\"}}";
    }

    private static void Collect(JsonElement element, List<Track> tracks, HashSet<string> seen, int limit)
    {
        if (tracks.Count >= limit)
            return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("videoRenderer"))
                    {
                        if (TryParseVideoRenderer(property.Value) is { } track && seen.Add(track.Id))
                        {
                            tracks.Add(track);
                            if (tracks.Count >= limit)
                                return;
                        }
                        continue;
                    }
                    Collect(property.Value, tracks, seen, limit);
                    if (tracks.Count >= limit)
                        return;
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, tracks, seen, limit);
                    if (tracks.Count >= limit)
                        return;
                }
                break;
        }
    }

    private static Track? TryParseVideoRenderer(JsonElement renderer)
    {
        var id = GetString(renderer, "videoId");
        if (string.IsNullOrEmpty(id))
            return null;

        var title = GetText(renderer, "title");
        if (string.IsNullOrEmpty(title))
            return null;

        var artist = GetText(renderer, "ownerText") ?? GetText(renderer, "longBylineText");

        if (artist is not null && artist.EndsWith(" - Topic", StringComparison.Ordinal))
            artist = artist[..^" - Topic".Length];

        return new Track
        {
            Id = id,
            Title = title,
            Artist = artist,
            Duration = ParseDuration(GetText(renderer, "lengthText")),
            ThumbnailUrl = PickThumbnail(renderer, id),
        };
    }

    private static string? GetText(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var node) || node.ValueKind != JsonValueKind.Object)
            return null;

        if (GetString(node, "simpleText") is { Length: > 0 } simple)
            return simple;

        if (node.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
        {
            var text = new StringBuilder();
            foreach (var run in runs.EnumerateArray())
                if (GetString(run, "text") is { } part)
                    text.Append(part);
            if (text.Length > 0)
                return text.ToString();
        }

        return null;
    }

    private static TimeSpan? ParseDuration(string? lengthText)
    {
        if (string.IsNullOrWhiteSpace(lengthText))
            return null;

        var parts = lengthText.Split(':');
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

    private static string PickThumbnail(JsonElement renderer, string id)
    {
        if (renderer.TryGetProperty("thumbnail", out var thumbnail)
            && thumbnail.TryGetProperty("thumbnails", out var thumbs)
            && thumbs.ValueKind == JsonValueKind.Array)
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

        return $"https://i.ytimg.com/vi/{id}/hqdefault.jpg";
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static HttpClient CreateClient()
    {
        var handler = Ipv4Http.CreateHandler();
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        client.DefaultRequestHeaders.Add("X-YouTube-Client-Name", "1");
        client.DefaultRequestHeaders.Add("X-YouTube-Client-Version", ClientVersion);
        client.DefaultRequestHeaders.Add("Origin", "https://www.youtube.com");
        return client;
    }
}
