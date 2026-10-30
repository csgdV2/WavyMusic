using Wavy.Core.Services;

namespace Microsoft.UI.Xaml { public enum Visibility { Visible, Collapsed } }
namespace Wavy
{
    public static class App { public static UpdateTestWindow? MainWindow { get; set; } }
    public sealed class UpdateTestWindow { public int Closed { get; private set; } public void Close() => Closed++; }
}
namespace Wavy.Services
{
    public static class AppPaths { public static string DataRoot { get; set; } = Path.GetTempPath(); }
    public sealed class UpdateInstallerLauncher
    {
        public int Calls { get; private set; }
        public Func<string, AppRelease, CancellationToken, Task>? OnLaunch { get; set; }
        public Task LaunchAsync(string path, AppRelease release, CancellationToken ct)
        {
            Calls++;
            return OnLaunch?.Invoke(path, release, ct) ?? Task.CompletedTask;
        }
    }
}
