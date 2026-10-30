using System.Text.RegularExpressions;

namespace Wavy.Core.Services;

public static class UpdateCacheCleanup
{
    private static readonly Regex InstallerName = new(@"^Wavy-(\d+\.\d+(?:\.\d+){0,2})-setup\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PartialName = new(@"^Wavy-\d+\.\d+(?:\.\d+){0,2}-setup\.exe\.[0-9a-f]{32}\.partial$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static int RemoveObsolete(string folder, Version installed, DateTimeOffset? now = null)
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(folder) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return 0;
            var cutoff = (now ?? DateTimeOffset.UtcNow).UtcDateTime.AddDays(-1);
            foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                    var name = Path.GetFileName(path);
                    var match = InstallerName.Match(name);
                    var oldInstaller = match.Success && Version.TryParse(match.Groups[1].Value, out var version)
                        && Normalize(version) <= Normalize(installed);
                    var abandonedPartial = PartialName.IsMatch(name) && File.GetLastWriteTimeUtc(path) < cutoff;
                    if (!oldInstaller && !abandonedPartial) continue;
                    File.Delete(path);
                    removed++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return removed;
    }

    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
}
