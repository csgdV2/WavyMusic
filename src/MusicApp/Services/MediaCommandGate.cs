namespace MusicApp.Services;

internal static class MediaCommandGate
{
    public const string Next = "next";
    public const string Previous = "previous";
    public const string PlayPause = "playpause";

    private const long WindowMs = 350;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, long> Claims = new(StringComparer.Ordinal);

    public static bool TryClaim(string command)
    {
        lock (Gate)
        {
            var now = Environment.TickCount64;
            if (Claims.TryGetValue(command, out var last) && now - last < WindowMs)
                return false;

            Claims[command] = now;
            return true;
        }
    }
}
