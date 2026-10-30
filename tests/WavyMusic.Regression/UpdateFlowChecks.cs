using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Wavy;
using Wavy.Core.Services;
using Wavy.Services;

internal static class UpdateFlowChecks
{
    public static async Task RunAsync()
    {
        static void Check(bool pass, string name)
        {
            if (!pass) throw new Exception("FAIL: " + name);
            Console.WriteLine("PASS: " + name);
        }
        var folder = Path.Combine(Path.GetTempPath(), "Wavy-update-flow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var previousRoot = AppPaths.DataRoot;
        AppPaths.DataRoot = folder;
        try
        {
            var handler = new ReleaseHandler();
            using var http = new HttpClient(handler);
            var settings = new SettingsService { LastUpdateCheck = DateTimeOffset.UtcNow };
            var launcher = new UpdateInstallerLauncher();
            var service = new AppUpdateService(settings, new GitHubUpdateClient(http), launcher);
            await service.CheckAtStartupAsync();
            await service.CheckAtStartupAsync();
            Check(handler.CheckRequests == 2 && service.HasUpdate, "startup checks run every time even when the last check was recent");
            settings.AutomaticUpdateChecks = false;
            await service.CheckAtStartupAsync();
            Check(handler.CheckRequests == 2, "automatic update checks still respect the user's off setting");
            App.MainWindow = new UpdateTestWindow();
            launcher.OnLaunch = async (path, release, ct) =>
            {
                Check(await GitHubUpdateClient.VerifyAsync(path, release, ct) && App.MainWindow.Closed == 0,
                    "the app stays open until the verified installer is handed to the update helper");
            };
            await service.ActAsync();
            Check(handler.DownloadRequests == 1 && launcher.Calls == 1 && App.MainWindow.Closed == 1,
                "one Download update action downloads, verifies, starts installation and closes the app without a second click");

            var corrupt = new ReleaseHandler { Corrupt = true };
            using var badHttp = new HttpClient(corrupt);
            var noInstall = new UpdateInstallerLauncher();
            var rejected = new AppUpdateService(new SettingsService(), new GitHubUpdateClient(badHttp), noInstall);
            App.MainWindow = new UpdateTestWindow();
            await rejected.CheckAtStartupAsync();
            await rejected.ActAsync();
            Check(noInstall.Calls == 0 && App.MainWindow.Closed == 0 && rejected.Status.StartsWith("Couldn't update"),
                "a damaged update never launches an installer or closes the app");

            var pendingHandler = new ReleaseHandler { Pending = new TaskCompletionSource<byte[]>() };
            using var pendingHttp = new HttpClient(pendingHandler);
            var pendingLauncher = new UpdateInstallerLauncher();
            var pendingService = new AppUpdateService(new SettingsService(), new GitHubUpdateClient(pendingHttp), pendingLauncher);
            App.MainWindow = new UpdateTestWindow();
            await pendingService.CheckAtStartupAsync();
            var first = pendingService.ActAsync();
            await pendingService.ActAsync();
            Check(pendingHandler.DownloadRequests == 1 && pendingService.IsBusy, "repeated update clicks cannot start overlapping downloads");
            pendingHandler.Pending.SetResult(pendingHandler.Payload);
            await first;
            Check(pendingLauncher.Calls == 1, "the pending download continues straight into one installation");

            var retryHandler = new ReleaseHandler();
            using var retryHttp = new HttpClient(retryHandler);
            var retryLauncher = new UpdateInstallerLauncher { OnLaunch = (_, _, _) => throw new IOException("helper failed") };
            var retryService = new AppUpdateService(new SettingsService(), new GitHubUpdateClient(retryHttp), retryLauncher);
            App.MainWindow = new UpdateTestWindow();
            await retryService.CheckAtStartupAsync();
            await retryService.ActAsync();
            Check(App.MainWindow.Closed == 0 && retryService.CanAct && retryService.ActionLabel == "Retry update",
                "a failed helper leaves the player open and allows a retry");
            retryLauncher.OnLaunch = null;
            await retryService.ActAsync();
            Check(retryHandler.DownloadRequests == 1 && retryLauncher.Calls == 2 && App.MainWindow.Closed == 1,
                "retrying installation re-verifies the download without downloading it again");
        }
        finally { App.MainWindow = null; AppPaths.DataRoot = previousRoot; Directory.Delete(folder, true); }
    }

    private sealed class ReleaseHandler : HttpMessageHandler
    {
        public byte[] Payload { get; } = "MZ-Wavy-auto-update-fixture"u8.ToArray();
        public bool Corrupt { get; set; }
        public TaskCompletionSource<byte[]>? Pending { get; set; }
        public int CheckRequests { get; private set; }
        public int DownloadRequests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                CheckRequests++;
                var json = JsonSerializer.Serialize(new { tag_name = "3.0", draft = false, prerelease = false,
                    assets = new[] { new { name = "Wavy-3.0-setup.exe", size = Payload.Length,
                        browser_download_url = "https://github.com/csgdV2/WavyMusic/releases/download/3.0/Wavy-3.0-setup.exe",
                        digest = "sha256:" + Convert.ToHexString(SHA256.HashData(Payload)) } } });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            }
            DownloadRequests++;
            var bytes = Pending is null ? Payload.ToArray() : await Pending.Task.WaitAsync(ct);
            if (Corrupt) bytes[^1] ^= 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
    }
}
