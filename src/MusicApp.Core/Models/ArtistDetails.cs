namespace MusicApp.Core.Models;

public sealed class ArtistDetails
{

    public required string BrowseId { get; init; }

    public required string Name { get; init; }

    public string? Subtitle { get; init; }

    public string? Description { get; init; }

    public string? BannerUrl { get; init; }

    public IReadOnlyList<Track> TopSongs { get; init; } = Array.Empty<Track>();

    public IReadOnlyList<AlbumResult> Releases { get; init; } = Array.Empty<AlbumResult>();

    public IReadOnlyList<ArtistResult> Similar { get; init; } = Array.Empty<ArtistResult>();

    public string? SongsPlaylistId { get; init; }

    public bool HasTopSongs => TopSongs.Count > 0;
    public bool HasReleases => Releases.Count > 0;
    public bool HasSimilar => Similar.Count > 0;
}
