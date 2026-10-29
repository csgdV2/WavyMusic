using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Wavy.Core.Services;

internal static class UpdateChecks
{
    public static async Task RunAsync()
    {
        var bytes = "MZ-Wavy-installer-fixture"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        string Fixture(string tag = "1.9", string? url = null, string? digest = null, bool prerelease = false) => JsonSerializer.Serialize(new
        {
            tag_name = tag, draft = false, prerelease,
            assets = new[] { new { name = $"Wavy-{tag.TrimStart('v')}-setup.exe", size = bytes.Length,
                browser_download_url = url ?? $"https://github.com/csgdV2/WavyMusic/releases/download/{tag}/Wavy-{tag.TrimStart('v')}-setup.exe",
                digest = digest ?? "sha256:" + hash } }
        });
        AppRelease? Parse(string json, Version? current = null)
        {
            using var doc = JsonDocument.Parse(json);
            return GitHubUpdateClient.ParseRelease(doc.RootElement, current ?? new Version(1, 8, 0, 0));
        }
        void Check(bool pass, string name)
        {
            if (!pass) throw new Exception("FAIL: " + name);
            Console.WriteLine("PASS: " + name);
        }
        var release = Parse(Fixture())!;
        Check(Parse(Fixture("1.9.1"), new Version(1, 9, 0)) is not null && Parse(Fixture("1.9.1"), new Version(1, 9, 1, 0)) is null,
            "updater offers newer patch releases without offering the installed patch again");
        Check(release.Version == new Version(1, 9) && Parse(Fixture("v1.9")) is not null, "updater accepts newer stable releases with plain or v-prefixed tags");
        Check(Parse(Fixture("1.8")) is null && Parse(Fixture("1.7")) is null && Parse(Fixture(prerelease: true)) is null,
            "updater ignores equivalent, older and prerelease versions");
        try { Parse(Fixture(url: "https://example.com/Wavy-1.9-setup.exe")); throw new Exception("Untrusted URL accepted"); }
        catch (InvalidDataException) { Console.WriteLine("PASS: updater rejects installers outside the Wavy release"); }
        try { Parse(Fixture(digest: "")); throw new Exception("Missing checksum accepted"); }
        catch (InvalidDataException) { Console.WriteLine("PASS: updater requires a valid SHA-256 digest"); }
        var folder = Path.Combine(Path.GetTempPath(), "Wavy-update-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var http = new HttpClient(new Handler(bytes));
            var client = new GitHubUpdateClient(http);
            var path = await client.DownloadAsync(release, folder);
            Check(await GitHubUpdateClient.VerifyAsync(path, release), "updater downloads and verifies the installer before it is offered for installation");
            await File.WriteAllBytesAsync(path, "tampered installer"u8.ToArray());
            Check(!await GitHubUpdateClient.VerifyAsync(path, release), "updater rejects a modified installer at installation time");
            try { await client.DownloadAsync(release with { Sha256 = new string('0', 64) }, folder); throw new Exception("Checksum mismatch accepted"); }
            catch (InvalidDataException) { Console.WriteLine("PASS: updater rejects corrupted downloads and removes partial files"); }
            Check(!Directory.EnumerateFiles(folder, "*.partial").Any(), "failed update downloads leave no partial installers");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try { await client.DownloadAsync(release, folder, ct: canceled.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { Console.WriteLine("PASS: updater downloads can be canceled"); }
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private sealed class Handler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
