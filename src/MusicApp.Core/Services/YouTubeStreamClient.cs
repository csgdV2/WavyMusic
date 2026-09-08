using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MusicApp.Core.Services;

public sealed class YouTubeStreamClient
{
    private const string Endpoint = "https://www.youtube.com/youtubei/v1/player?prettyPrint=false";

    private static readonly ClientProfile[] Profiles =
    {
        new("ANDROID", "20.10.38", 3,
            "com.google.android.youtube/20.10.38 (Linux; U; Android 11; en_US) gzip",
            "\"deviceMake\":\"Google\",\"deviceModel\":\"Pixel 6\",\"osName\":\"Android\",\"osVersion\":\"11\",\"androidSdkVersion\":30"),
        new("IOS", "20.10.4", 5,
            "com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 18_3_2 like Mac OS X; en_US)",
            "\"deviceMake\":\"Apple\",\"deviceModel\":\"iPhone16,2\",\"osName\":\"iPhone\",\"osVersion\":\"18.3.2.22D82\""),
        new("ANDROID_VR", "1.60.19", 28,
            "com.google.android.apps.youtube.vr.oculus/1.60.19 (Linux; U; Android 12; en_US)",
            "\"deviceMake\":\"Oculus\",\"deviceModel\":\"Quest 3\",\"osName\":\"Android\",\"osVersion\":\"12\",\"androidSdkVersion\":32"),
    };

    private static readonly HttpClient Http = CreateClient();

    public async Task<string?> ResolveAudioUrlAsync(string videoId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoId))
            return null;

        foreach (var profile in Profiles)
        {
            ct.ThrowIfCancellationRequested();

            string? url;
            try
            {
                url = await RequestAudioUrlAsync(profile, videoId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                continue;
            }

            if (url is null)
                continue;

            if (await ServesAudioAsync(url, ct).ConfigureAwait(false))
                return url;
        }

        return null;
    }

    private static async Task<string?> RequestAudioUrlAsync(ClientProfile profile, string videoId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(BuildRequestJson(profile, videoId), Encoding.UTF8),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        request.Headers.TryAddWithoutValidation("User-Agent", profile.UserAgent);
        request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", profile.Id.ToString());
        request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", profile.Version);

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return PickAudioUrl(doc.RootElement);
    }

    private static string BuildRequestJson(ClientProfile profile, string videoId)
    {

        var id = JsonEncodedText.Encode(videoId).ToString();
        return $"{{\"videoId\":\"{id}\",\"context\":{{\"client\":{{" +
               $"\"clientName\":\"{profile.Name}\",\"clientVersion\":\"{profile.Version}\"," +
               $"{profile.ExtraContextJson},\"hl\":\"en\",\"gl\":\"US\"}}}}," +
               "\"contentCheckOk\":true,\"racyCheckOk\":true}";
    }

    private static string? PickAudioUrl(JsonElement root)
    {
        if (root.TryGetProperty("playabilityStatus", out var playability)
            && GetString(playability, "status") is { } status
            && !string.Equals(status, "OK", StringComparison.Ordinal))
        {
            return null;
        }

        if (!root.TryGetProperty("streamingData", out var streaming))
            return null;

        return PickByBitrate(streaming, "formats", "video/mp4")
            ?? PickByBitrate(streaming, "adaptiveFormats", "audio/mp4");
    }

    private static string? PickByBitrate(JsonElement streaming, string arrayName, string mimePrefix)
    {
        if (!streaming.TryGetProperty(arrayName, out var formats) || formats.ValueKind != JsonValueKind.Array)
            return null;

        string? best = null;
        var bestBitrate = -1L;
        foreach (var format in formats.EnumerateArray())
        {
            if (GetString(format, "mimeType") is not { } mime
                || !mime.StartsWith(mimePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (mimePrefix == "video/mp4" && !mime.Contains("mp4a", StringComparison.Ordinal))
                continue;

            if (GetString(format, "url") is not { Length: > 0 } url)
                continue;

            var bitrate = format.TryGetProperty("bitrate", out var br) && br.ValueKind == JsonValueKind.Number
                ? br.GetInt64()
                : 0;
            if (bitrate > bestBitrate)
            {
                bestBitrate = bitrate;
                best = url;
            }
        }

        return best;
    }

    private static async Task<bool> ServesAudioAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            using var response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentType?.MediaType is not { } media
                || !(media.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                     || media.StartsWith("video/", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var header = new byte[12];
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var read = 0;
            while (read < header.Length)
            {
                var got = await stream.ReadAsync(header.AsMemory(read), ct).ConfigureAwait(false);
                if (got <= 0)
                    break;
                read += got;
            }

            return read == header.Length
                && header[4] == (byte)'f' && header[5] == (byte)'t'
                && header[6] == (byte)'y' && header[7] == (byte)'p';
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static HttpClient CreateClient()
    {
        var handler = Ipv4Http.CreateHandler();

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        client.DefaultRequestHeaders.Add("Origin", "https://www.youtube.com");
        return client;
    }

    private sealed record ClientProfile(string Name, string Version, int Id, string UserAgent, string ExtraContextJson);
}
