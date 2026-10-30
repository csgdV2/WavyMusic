using System.Diagnostics;
using Wavy.Core.Services;

namespace Wavy.Services;

public sealed class UpdateInstallerLauncher
{
    public async Task LaunchAsync(string installer, AppRelease release, CancellationToken ct)
    {
        var bundledHelper = Path.Combine(AppContext.BaseDirectory, "Tools", "wavy-update.ps1");
        if (!File.Exists(bundledHelper)) throw new FileNotFoundException("The update helper is missing. Reinstall Wavy from GitHub.");
        var work = Path.Combine(AppPaths.DataRoot, "updates", "sessions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var helper = Path.Combine(work, "wavy-update.ps1");
        File.Copy(bundledHelper, helper);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = work,
        };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-STA", "-File", helper,
            "-InstallerPath", installer, "-InstallDirectory", AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
            "-ExpectedSha256", release.Sha256, "-ExpectedSize", release.Size.ToString(System.Globalization.CultureInfo.InvariantCulture), "-WorkDirectory", work })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("The updater could not be started.");
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(45))
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(Path.Combine(work, "ready"))) return;
            if (process.HasExited)
            {
                var error = Path.Combine(work, "error.txt");
                throw new IOException(File.Exists(error) ? await File.ReadAllTextAsync(error, ct) : "The update helper stopped before installation started.");
            }
            await Task.Delay(100, ct);
        }
        throw new TimeoutException("The update helper did not start in time.");
    }
}
