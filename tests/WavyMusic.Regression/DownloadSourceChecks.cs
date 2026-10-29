using Wavy.Core.Abstractions;
using Wavy.Core.Models;
using Wavy.Core.Services;

static class DownloadSourceChecks
{
    public static async Task RunAsync()
    {
        foreach (var ffmpeg in new string?[] { null, "ffmpeg.exe" })
        {
            var runner = new CaptureRunner();
            var service = new YtDlpService(runner, new Locator(), ffmpeg);
            await foreach (var progress in service.DownloadAsync(new Track { Id = "test", Title = "Test" }, "test.%(ext)s")) { }
            if (runner.Arguments.Contains("-x") || runner.Arguments.Contains("--audio-format") || runner.Arguments.Contains("--audio-quality"))
                throw new Exception("Download unexpectedly transcodes audio.");
            Console.WriteLine($"PASS: downloads preserve source codec with FFmpeg {(ffmpeg is null ? "absent" : "available")}");
        }
    }

    private sealed class Locator : IToolLocator
    {
        public Task<string> GetYtDlpPathAsync(CancellationToken ct = default) => Task.FromResult("yt-dlp");
    }

    private sealed class CaptureRunner : IProcessRunner
    {
        public IReadOnlyList<string> Arguments { get; private set; } = Array.Empty<string>();
        public Task<ProcessResult> RunAsync(string exePath, IReadOnlyList<string> args, CancellationToken ct = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<string> StreamLinesAsync(string exePath, IReadOnlyList<string> args,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Arguments = args;
            await Task.CompletedTask;
            yield return "Destination: test.m4a";
        }
    }
}
