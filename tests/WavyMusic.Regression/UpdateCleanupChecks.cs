using Wavy.Core.Services;

internal static class UpdateCleanupChecks
{
    public static void Run()
    {
        static void Check(bool pass, string name)
        {
            if (!pass) throw new Exception("FAIL: " + name);
            Console.WriteLine("PASS: " + name);
        }
        var root = Path.Combine(Path.GetTempPath(), "Wavy-update-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var old = Path.Combine(root, "Wavy-1.9.4-setup.exe");
            var current = Path.Combine(root, "Wavy-2.0-setup.exe");
            var future = Path.Combine(root, "Wavy-2.1-setup.exe");
            var other = Path.Combine(root, "another-installer.exe");
            var stale = Path.Combine(root, "Wavy-2.1-setup.exe." + new string('a', 32) + ".partial");
            var fresh = Path.Combine(root, "Wavy-2.2-setup.exe." + new string('b', 32) + ".partial");
            foreach (var path in new[] { old, current, future, other, stale, fresh }) File.WriteAllText(path, "fixture");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
            Check(UpdateCacheCleanup.RemoveObsolete(root, new Version(2, 0, 0, 0)) == 3 && !File.Exists(old) && !File.Exists(current),
                "update cleanup removes older and already-installed installers plus abandoned partial downloads");
            Check(File.Exists(future) && File.Exists(fresh) && File.Exists(other),
                "cleanup preserves newer updates, fresh downloads and unrelated files");
            var locked = Path.Combine(root, "Wavy-1.8-setup.exe");
            File.WriteAllText(locked, "locked");
            using (var stream = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
                Check(UpdateCacheCleanup.RemoveObsolete(root, new Version(2, 0)) == 0 && File.Exists(locked),
                    "a locked installer is left for later rather than interrupting an active process");
            Check(UpdateCacheCleanup.RemoveObsolete(root, new Version(2, 0)) == 1 && !File.Exists(locked),
                "cleanup retries an obsolete installer after its lock is released");
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root)) File.Delete(file);
            Directory.Delete(root);
        }
    }
}
