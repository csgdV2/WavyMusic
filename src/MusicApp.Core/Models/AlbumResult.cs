namespace MusicApp.Core.Models;

public sealed class AlbumResult
{

    public required string BrowseId { get; init; }

    public required string Title { get; init; }

    public string? Artist { get; init; }

    public string? Kind { get; init; }

    public int? Year { get; init; }

    public string? ThumbnailUrl { get; init; }

    public string Subtitle
    {
        get
        {
            var lead = string.IsNullOrEmpty(Artist) ? Kind : Artist;
            if (Year is not { } y)
                return lead ?? string.Empty;
            return string.IsNullOrEmpty(lead) ? y.ToString() : $"{lead} • {y}";
        }
    }
}
