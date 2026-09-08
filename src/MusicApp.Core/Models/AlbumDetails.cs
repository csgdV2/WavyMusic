namespace MusicApp.Core.Models;

public sealed class AlbumDetails
{

    public required string BrowseId { get; init; }

    public required string Title { get; init; }

    public string? Artist { get; init; }

    public string? ArtistBrowseId { get; init; }

    public string? Kind { get; init; }

    public int? Year { get; init; }

    public string? ThumbnailUrl { get; init; }

    public string? Details { get; init; }

    public IReadOnlyList<Track> Tracks { get; init; } = Array.Empty<Track>();

    public string Subtitle
    {
        get
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrEmpty(Kind))
                parts.Add(Kind);
            if (Year is { } year)
                parts.Add(year.ToString());
            if (!string.IsNullOrEmpty(Details))
                parts.Add(Details);
            return string.Join(" • ", parts);
        }
    }

    public bool HasTracks => Tracks.Count > 0;
}
