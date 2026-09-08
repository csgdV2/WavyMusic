using System.IO;
using System.Net.Http;
using MusicApp.Core.Abstractions;
using MusicApp.Core.Models;
using MusicApp.Core.Services;

namespace MusicApp.Services;

public sealed class ToolProvisioner : IToolLocator
{
    private const string YtDlpDownloadUrl =
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(7);
    private const long MinValidSize = 1_000_000;

    private readonly string _bundledPath;
    private readonly string _downloadedPath;
    private readonly object _gate = new();
    private Task<string>? _resolve;

    public ToolProvisioner(string bundledPath, string downloadedPath)
    {
        _bundledPath = bundledPath;
        _downloadedPath = downloadedPath;
    }

    public Task<string> GetYtDlpPathAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {

            if (_resolve is { IsFaulted: false, IsCanceled: false })
                return _resolve;
            return _resolve = ResolveAsync(ct);
        }
    }

    private async Task<string> ResolveAsync(CancellationToken ct)
    {

        if (File.Exists(_bundledPath))
            return _bundledPath;

        if (File.Exists(_downloadedPath))
        {
            if (IsStale(_downloadedPath))
                _ = TryRefreshAsync();
            return _downloadedPath;
        }

        try
        {
            await DownloadAsync(_downloadedPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new YtDlpException(
                "yt-dlp isn't installed and couldn't be downloaded automatically. Check your " +
                "internet connection, or place yt-dlp.exe in the app's Tools folder. " +
                $"({ex.Message})", ex);
        }
        return _downloadedPath;
    }

    private static bool IsStale(string path)
    {
        try { return DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(path) > RefreshAfter; }
        catch { return false; }
    }

    private async Task TryRefreshAsync()
    {

        try { await DownloadAsync(_downloadedPath, CancellationToken.None).ConfigureAwait(false); }
        catch {  }
    }

    private static async Task DownloadAsync(string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var tempPath = destination + ".download";

        using (var http = new HttpClient(Ipv4Http.CreateHandler()) { Timeout = TimeSpan.FromMinutes(5) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Wavy/1.0");
            using var response = await http
                .GetAsync(YtDlpDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(file, ct).ConfigureAwait(false);
        }

        if (new FileInfo(tempPath).Length < MinValidSize)
        {
            TryDelete(tempPath);
            throw new IOException("The downloaded yt-dlp file looks incomplete.");
        }

        File.Move(tempPath, destination, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch {  }
    }
}
