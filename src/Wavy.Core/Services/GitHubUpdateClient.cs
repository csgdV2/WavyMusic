using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Wavy.Core.Services;

public sealed record AppRelease(Version Version, string Tag, Uri DownloadUrl, long Size, string Sha256);

public sealed class GitHubUpdateClient
{
    private const string ReleaseRoot = "https://github.com/csgdV2/WavyMusic/releases/download/";
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly HttpClient _http;

    public GitHubUpdateClient(HttpClient? http = null) => _http = http ?? SharedHttp;

    public async Task<AppRelease?> CheckAsync(Version installed, CancellationToken ct = default)
    {
        using var request = Request("https://api.github.com/repos/csgdV2/WavyMusic/releases/latest");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return ParseRelease(doc.RootElement, installed);
    }

    public static AppRelease? ParseRelease(JsonElement root, Version installed)
    {
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()
            || root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
        if (Normalize(version) <= Normalize(installed)) return null;
        var expectedName = $"Wavy-{tag.TrimStart('v', 'V')}-setup.exe";
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), expectedName, StringComparison.OrdinalIgnoreCase)) continue;
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            var expectedUrl = ReleaseRoot + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(expectedName);
            if (!string.Equals(url, expectedUrl, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The release installer link is invalid.");
            var size = asset.GetProperty("size").GetInt64();
            if (size < 2 || size > 512L * 1024 * 1024) throw new InvalidDataException("The release installer size is invalid.");
            var digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() ?? "" : "";
            if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit))
                throw new InvalidDataException("This release has no SHA-256 checksum. Download it from GitHub manually.");
            return new AppRelease(version, tag, new Uri(url), size, digest[7..]);
        }
        throw new InvalidDataException("The latest release has no Windows installer yet.");
    }

    public async Task<string> DownloadAsync(AppRelease release, string folder, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, $"Wavy-{release.Version}-setup.exe");
        var partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using var request = Request(release.DownloadUrl.AbsoluteUri);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != release.Size)
                throw new InvalidDataException("The download size does not match the release.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            var buffer = new byte[81920];
            var signature = new byte[2];
            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, true))
            {
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    for (var i = 0; i < read && total + i < 2; i++) signature[total + i] = buffer[i];
                    total += read;
                    if (total > release.Size) throw new InvalidDataException("The download is larger than expected.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    progress?.Report(total * 100d / release.Size);
                }
            }
            ct.ThrowIfCancellationRequested();
            if (total != release.Size || signature[0] != 'M' || signature[1] != 'Z'
                || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Installer verification failed. Please download the update again.");
            File.Move(partial, destination, true);
            return destination;
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    public static async Task<bool> VerifyAsync(string path, AppRelease release, CancellationToken ct = default)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != release.Size) return false;
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return string.Equals(Convert.ToHexString(hash), release.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpRequestMessage Request(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Wavy/2.1 (+https://github.com/csgdV2/WavyMusic)");
        return request;
    }
}
