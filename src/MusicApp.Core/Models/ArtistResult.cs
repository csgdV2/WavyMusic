namespace MusicApp.Core.Models;

public sealed class ArtistResult
{

    public required string BrowseId { get; init; }

    public required string Name { get; init; }

    public string? Subtitle { get; init; }

    public string? ThumbnailUrl { get; init; }
}
