using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using MusicApp.Core.Models;

namespace MusicApp.Core.Services;

public sealed class LyricsClient
{
    private static readonly Regex TimeTag =
        new(@"\[(?<m>\d{1,3}):(?<s>\d{1,2})(?:[.:](?<f>\d{1,3}))?\]", RegexOptions.Compiled);

    private readonly HttpClient _http = new(Ipv4Http.CreateHandler())
    {
        Timeout = TimeSpan.FromSeconds(12),
    };

    public LyricsClient()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Wavy/1.3 (music player)");
    }

    public async Task<Lyrics?> GetAsync(
        string title, string? artist, double durationSeconds,
        LyricsSource source, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var clean = Clean(title);
        var who = string.IsNullOrWhiteSpace(artist) ? null : Clean(artist);

        return source switch
        {
            LyricsSource.LrcLib => await FromLrcLibAsync(clean, who, durationSeconds, ct).ConfigureAwait(false),
            LyricsSource.LyricsOvh => await FromLyricsOvhAsync(clean, who, ct).ConfigureAwait(false),
            LyricsSource.Genius => await FromGeniusAsync(clean, who, ct).ConfigureAwait(false),
            _ => await FromLrcLibAsync(clean, who, durationSeconds, ct).ConfigureAwait(false)
                 ?? await FromGeniusAsync(clean, who, ct).ConfigureAwait(false)
                 ?? await FromLyricsOvhAsync(clean, who, ct).ConfigureAwait(false),
        };
    }

    private async Task<Lyrics?> FromLrcLibAsync(
        string title, string? artist, double duration, CancellationToken ct)
    {
        try
        {
            var url = "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(title);
            if (artist is not null)
                url += "&artist_name=" + Uri.EscapeDataString(artist);

            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var items = await response.Content
                .ReadFromJsonAsync<JsonElement>(cancellationToken: ct).ConfigureAwait(false);
            if (items.ValueKind != JsonValueKind.Array)
                return null;

            JsonElement? best = null;
            var bestScore = double.MaxValue;
            foreach (var item in items.EnumerateArray())
            {
                if (Text(item, "syncedLyrics") is null && Text(item, "plainLyrics") is null)
                    continue;

                var candidate = item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                    ? d.GetDouble()
                    : 0;
                var score = duration > 0 && candidate > 0 ? Math.Abs(candidate - duration) : 500;
                if (Text(item, "syncedLyrics") is not null)
                    score -= 1000;

                if (score >= bestScore)
                    continue;
                bestScore = score;
                best = item;
            }

            if (best is not { } pick)
                return null;

            if (Text(pick, "syncedLyrics") is { } synced && ParseLrc(synced) is { Count: > 0 } timed)
                return new Lyrics { Lines = timed, Provider = "LRCLIB", IsSynced = true };

            if (Text(pick, "plainLyrics") is { } plain && ParsePlain(plain) is { Count: > 0 } lines)
                return new Lyrics { Lines = lines, Provider = "LRCLIB", IsSynced = false };

            return null;
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

    private async Task<Lyrics?> FromLyricsOvhAsync(string title, string? artist, CancellationToken ct)
    {
        if (artist is null)
            return null;

        try
        {
            var url = "https://api.lyrics.ovh/v1/"
                      + Uri.EscapeDataString(artist) + "/" + Uri.EscapeDataString(title);

            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var payload = await response.Content
                .ReadFromJsonAsync<JsonElement>(cancellationToken: ct).ConfigureAwait(false);
            if (Text(payload, "lyrics") is not { } plain)
                return null;

            var lines = ParsePlain(plain);
            return lines.Count == 0
                ? null
                : new Lyrics { Lines = lines, Provider = "lyrics.ovh", IsSynced = false };
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

    private async Task<Lyrics?> FromGeniusAsync(string title, string? artist, CancellationToken ct)
    {
        if (artist is null)
            return null;

        foreach (var slug in GeniusSlugs(title, artist))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://genius.com/" + slug);
                request.Headers.UserAgent.Clear();
                request.Headers.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");

                using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    continue;

                var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var lines = GeniusLines(html);
                if (lines.Count > 0)
                    return new Lyrics { Lines = lines, Provider = "Genius", IsSynced = false };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }
        }

        return null;
    }

    private static IEnumerable<string> GeniusSlugs(string title, string artist)
    {
        var who = artist;
        var cut = who.IndexOfAny(new[] { ',', '&', '·', '/' });
        if (cut > 0)
            who = who[..cut];

        yield return GeniusSlug(who + " " + title);

        if (!ReferenceEquals(who, artist))
            yield return GeniusSlug(artist + " " + title);

        if (who.StartsWith("The ", StringComparison.OrdinalIgnoreCase))
            yield return GeniusSlug(who[4..] + " " + title);
    }

    private static string GeniusSlug(string value)
    {
        var text = value.Trim().Replace("&", " and ").Replace("'", string.Empty).Replace("’", string.Empty);
        text = Regex.Replace(text, @"[^A-Za-z0-9]+", "-").Trim('-').ToLowerInvariant();
        if (text.Length == 0)
            return "-lyrics";

        return char.ToUpperInvariant(text[0]) + text[1..] + "-lyrics";
    }

    private static List<LyricsLine> GeniusLines(string html)
    {
        var chunks = Regex.Split(html, "data-lyrics-container=\"true\"");
        var body = new List<string>();

        for (var i = 1; i < chunks.Length; i++)
        {
            var chunk = chunks[i];
            var open = chunk.IndexOf('>');
            if (open < 0)
                continue;

            var segment = chunk[(open + 1)..];
            var end = Regex.Match(segment, "class=\"(LyricsFooter|RightSidebar)");
            if (end.Success && end.Index > 0)
                segment = segment[..end.Index];

            segment = Regex.Replace(segment, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
            segment = Regex.Replace(segment, "<[^>]+>", string.Empty);
            body.Add(segment);
        }

        var lines = new List<LyricsLine>();
        var seen = string.Empty;

        foreach (var raw in string.Join("\n", body).Split('\n'))
        {
            var text = System.Net.WebUtility.HtmlDecode(raw).Trim();
            if (text.Length == 0)
                continue;
            if (text.Contains("Contributors", StringComparison.Ordinal) ||
                text.EndsWith("Read More", StringComparison.Ordinal))
                continue;
            if (text == seen)
                continue;

            seen = text;
            lines.Add(new LyricsLine { Text = text });
        }

        return lines;
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static List<LyricsLine> ParseLrc(string body)
    {
        var lines = new List<LyricsLine>();

        foreach (var raw in body.Split('\n'))
        {
            var matches = TimeTag.Matches(raw);
            if (matches.Count == 0)
                continue;

            var text = TimeTag.Replace(raw, string.Empty).Trim();
            foreach (Match match in matches)
            {
                var minutes = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
                var seconds = int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
                var fraction = match.Groups["f"].Success ? match.Groups["f"].Value : "0";
                var divisor = Math.Pow(10, fraction.Length);
                var start = minutes * 60 + seconds
                            + int.Parse(fraction, CultureInfo.InvariantCulture) / divisor;

                lines.Add(new LyricsLine { Text = text, StartSeconds = start });
            }
        }

        lines.Sort((a, b) => (a.StartSeconds ?? 0).CompareTo(b.StartSeconds ?? 0));
        return lines;
    }

    private static List<LyricsLine> ParsePlain(string body) =>
        body.Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => new LyricsLine { Text = line.Trim() })
            .ToList();

    private static string Clean(string value)
    {
        var text = Regex.Replace(value, @"\((?:official|lyric|audio|video|hd|4k|visualizer)[^)]*\)",
            string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\[[^\]]*\]", string.Empty);
        text = Regex.Replace(text, @"\b(official (music )?video|lyrics?|audio|visualizer)\b",
            string.Empty, RegexOptions.IgnoreCase);
        text = text.Replace(" - Topic", string.Empty, StringComparison.OrdinalIgnoreCase);
        return Regex.Replace(text, @"\s{2,}", " ").Trim(' ', '-', '|', '·');
    }
}
