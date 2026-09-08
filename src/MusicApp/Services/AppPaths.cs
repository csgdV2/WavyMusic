using System.IO;

namespace MusicApp.Services;

public static class AppPaths
{

    public static string DataRoot { get; } = EnsureDir(Migrate(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavelength"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavy")));

    public static string AudioCache { get; } = EnsureDir(Path.Combine(DataRoot, "cache", "audio"));

    public static string ArtCache { get; } = EnsureDir(Path.Combine(DataRoot, "cache", "art"));

    public static string BundledYtDlp { get; } = Path.Combine(ToolsDir, "yt-dlp.exe");

    public static string DownloadedYtDlp { get; } = Path.Combine(
        EnsureDir(Path.Combine(DataRoot, "tools")), "yt-dlp.exe");

    public static string? Ffmpeg { get; } = ResolveOptionalTool("ffmpeg.exe");

    private static string ToolsDir => Path.Combine(AppContext.BaseDirectory, "Tools");

    private static string? ResolveOptionalTool(string exeName)
    {
        var bundled = Path.Combine(ToolsDir, exeName);
        return File.Exists(bundled) ? bundled : null;
    }

    private static string Migrate(string old, string current)
    {
        try
        {
            if (Directory.Exists(current) || !Directory.Exists(old))
                return current;

            Directory.Move(old, current);
        }
        catch
        {
            try
            {
                CopyTree(old, current);
            }
            catch
            {
            }
        }

        return current;
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);

        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));

        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            if (!File.Exists(target))
                File.Copy(file, target);
        }
    }

    private static string EnsureDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
