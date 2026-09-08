using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security;
using System.Text;

namespace MusicApp.Core.Services;

public sealed class DashManifestBuilder
{

    private const int HeaderBytes = 16 * 1024;

    private static readonly HttpClient Http = CreateClient();

    public async Task<string?> TryBuildAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        byte[] header;
        long? totalBytes;
        try
        {
            (header, totalBytes) = await ReadHeaderAsync(url, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }

        return Parse(header) is { } layout
            ? BuildManifest(url, layout, totalBytes)
            : null;
    }

    private static async Task<(byte[] Header, long? TotalBytes)> ReadHeaderAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, HeaderBytes - 1);

        using var response = await Http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return (bytes, response.Content.Headers.ContentRange?.Length);
    }

    private static Layout? Parse(byte[] b)
    {
        long moovEnd = -1, timescale = 0, duration = 0;
        var fragmented = false;

        var offset = 0;
        while (offset + 8 <= b.Length)
        {
            var size = (long)ReadUInt32(b, offset);
            var type = Encoding.ASCII.GetString(b, offset + 4, 4);
            if (size == 1)
                size = (long)ReadUInt64(b, offset + 8);
            if (size < 8)
                return null;

            switch (type)
            {
                case "moov":
                    moovEnd = offset + size;
                    ReadMovieHeader(b, offset + 8, (int)Math.Min(b.Length, moovEnd), ref timescale, ref duration, ref fragmented);
                    break;

                case "sidx" when moovEnd == offset:
                    return fragmented && timescale > 0 && duration > 0
                        ? new Layout(moovEnd - 1, offset, offset + size - 1, (double)duration / timescale)
                        : null;
            }

            offset += (int)size;
        }

        return null;
    }

    private static void ReadMovieHeader(byte[] b, int offset, int end, ref long timescale, ref long duration, ref bool fragmented)
    {
        while (offset + 8 <= end)
        {
            var size = (long)ReadUInt32(b, offset);
            var type = Encoding.ASCII.GetString(b, offset + 4, 4);
            if (size < 8)
                return;

            if (type == "mvex")
            {
                fragmented = true;
            }
            else if (type == "mvhd" && offset + 32 <= end)
            {

                if (b[offset + 8] == 0)
                {
                    timescale = ReadUInt32(b, offset + 20);
                    duration = ReadUInt32(b, offset + 24);
                }
                else if (offset + 40 <= end)
                {
                    timescale = ReadUInt32(b, offset + 28);
                    duration = (long)ReadUInt64(b, offset + 32);
                }
            }

            offset += (int)size;
        }
    }

    private static string BuildManifest(string url, Layout layout, long? totalBytes)
    {

        var bandwidth = totalBytes is > 0 && layout.DurationSeconds > 0
            ? (long)(totalBytes.Value * 8 / layout.DurationSeconds)
            : 131_072;

        var duration = layout.DurationSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" profiles="urn:mpeg:dash:profile:isoff-on-demand:2011" type="static" mediaPresentationDuration="PT{duration}S" minBufferTime="PT1.5S">
              <Period>
                <AdaptationSet mimeType="audio/mp4" subsegmentAlignment="true" subsegmentStartsWithSAP="1">
                  <Representation id="audio" bandwidth="{bandwidth}">
                    <BaseURL>{SecurityElement.Escape(url)}</BaseURL>
                    <SegmentBase indexRange="{layout.IndexStart}-{layout.IndexEnd}">
                      <Initialization range="0-{layout.InitEnd}"/>
                    </SegmentBase>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;
    }

    private static uint ReadUInt32(byte[] b, int o) =>
        (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);

    private static ulong ReadUInt64(byte[] b, int o)
    {
        ulong value = 0;
        for (var i = 0; i < 8; i++)
            value = (value << 8) | b[o + i];
        return value;
    }

    private static HttpClient CreateClient()
    {
        var handler = Ipv4Http.CreateHandler();

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    }

    private sealed record Layout(long InitEnd, long IndexStart, long IndexEnd, double DurationSeconds);
}
