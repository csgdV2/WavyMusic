namespace MusicApp.Core.Models;

public enum LyricsSource
{
    Auto,
    LrcLib,
    LyricsOvh,
    Genius,
}

public sealed class LyricsLine
{
    public required string Text { get; init; }

    public double? StartSeconds { get; init; }
}

public sealed class Lyrics
{
    public required IReadOnlyList<LyricsLine> Lines { get; init; }

    public required string Provider { get; init; }

    public bool IsSynced { get; init; }
}
