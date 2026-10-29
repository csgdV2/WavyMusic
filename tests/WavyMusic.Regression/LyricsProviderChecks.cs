using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Wavy.Core.Models;
using Wavy.Core.Services;

static class LyricsProviderChecks
{
    public static async Task RunAsync()
    {
        using var exactHandler = new Responses("exact");
        using var exactHttp = new HttpClient(exactHandler);
        var exact = await new LyricsClient(exactHttp).GetAsync("Demo (Official Audio)", "Test Artist", 120, LyricsSource.LrcLib, album: "Demo Album");
        Check(exact is { IsSynced: true } && exactHandler.Requests.Count == 1
            && exactHandler.Requests[0].Contains("album_name=Demo%20Album"), "LRCLIB exact lookup uses cleaned title, album and duration");
        using var searchHttp = new HttpClient(new Responses("search"));
        var search = await new LyricsClient(searchHttp).GetAsync("Demo", "Test Artist", 120, LyricsSource.Auto);
        Check(search is { IsSynced: true } && search.Lines[0].Text == "Right recording", "LRCLIB searches for synced lyrics before returning an exact plain-text match");
        using var broadHandler = new Responses("broad");
        using var broadHttp = new HttpClient(broadHandler);
        var broad = await new LyricsClient(broadHttp).GetAsync("Demo", "Test Artist", 120, LyricsSource.LrcLib);
        Check(broad is { IsSynced: true } && broadHandler.Requests.Any(u => u.Contains("?q=")), "LRCLIB broader search finds records missed by structured lookup");
        using var wrongHttp = new HttpClient(new Responses("wrong"));
        Check(await new LyricsClient(wrongHttp).GetAsync("Demo", "Test Artist", 120, LyricsSource.LrcLib) is null,
            "LRCLIB rejects a wrong artist and mismatched recording duration");
        using var plainHttp = new HttpClient(new Responses("plain"));
        Check(await new LyricsClient(plainHttp).GetAsync("Demo", "Test Artist", 120, LyricsSource.Auto) is { IsSynced: false, Provider: "LRCLIB" },
            "LRCLIB plain lyrics remain a fallback when no timed record matches");
        using var rateHandler = new Responses("rate");
        using var rateHttp = new HttpClient(rateHandler);
        var rateClient = new LyricsClient(rateHttp);
        await rateClient.GetAsync("Demo", "Test Artist", 120, LyricsSource.LrcLib);
        await rateClient.GetAsync("Demo", "Test Artist", 120, LyricsSource.LrcLib);
        Check(rateHandler.Requests.Count == 1, "LRCLIB Retry-After suppresses further requests");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await rateClient.GetAsync("Demo", "Test Artist", 120, LyricsSource.LrcLib, cancelled.Token); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { Console.WriteLine("PASS: lyrics lookup honors cancellation"); }
        Check(Enum.GetValues<LyricsSource>().Length == 4 && (int)LyricsSource.Genius == 3, "only supported key-free sources remain with stable existing values");
    }

    private static void Check(bool success, string message)
    {
        if (!success) throw new Exception("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }

    private sealed class Responses(string mode) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        private static object Record(string artist = "Test Artist", int duration = 120, bool synced = true) =>
            new { trackName = "Demo", artistName = artist, albumName = "Demo Album", duration,
                syncedLyrics = synced ? "[00:01.25]Right recording\n[00:02.25]Next" : null, plainLyrics = "Plain demo" };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var url = request.RequestUri!;
            if (url.Host != "lrclib.net") throw new Exception("Unexpected provider request: " + url.Host);
            Requests.Add(url.AbsoluteUri);
            if (mode == "rate")
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
                return Task.FromResult(limited);
            }
            object payload;
            if (url.AbsolutePath == "/api/get")
            {
                if (mode is "broad" or "wrong") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                payload = Record(synced: mode == "exact");
            }
            else if (mode == "search") payload = new[] { Record(duration: 310), Record(duration: 122) };
            else if (mode == "wrong") payload = new[] { Record(artist: "Someone Else"), Record(duration: 310) };
            else if (mode == "broad" && url.Query.Contains("?q=")) payload = new[] { Record() };
            else payload = Array.Empty<object>();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(payload)) });
        }
    }
}
