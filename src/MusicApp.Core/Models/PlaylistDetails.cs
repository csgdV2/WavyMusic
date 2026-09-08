namespace MusicApp.Core.Models;

public sealed class PlaylistDetails
{
    public required string Title { get; init; }

    public string? Author { get; init; }

    public string? ThumbnailUrl { get; init; }

    public IReadOnlyList<Track> Tracks { get; init; } = Array.Empty<Track>();
}
