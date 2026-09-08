namespace MusicApp.Core.Models;

public sealed class PlaylistResult
{
    public required string BrowseId { get; init; }

    public required string Title { get; init; }

    public string? Author { get; init; }

    public string? Detail { get; init; }

    public string? ThumbnailUrl { get; init; }

    public string Subtitle
    {
        get
        {
            if (string.IsNullOrEmpty(Author))
                return Detail ?? string.Empty;
            return string.IsNullOrEmpty(Detail) ? Author : $"{Author} • {Detail}";
        }
    }
}
