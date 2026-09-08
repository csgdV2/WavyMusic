using MusicApp.Core.Models;

namespace MusicApp.Core.Abstractions;

public interface IYtDlpService
{

    Task PrewarmAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Track>> SearchAsync(
        string query, SearchFilter filter = SearchFilter.Songs, int limit = 12, CancellationToken ct = default);

    Task<IReadOnlyList<Track>> GetSimilarAsync(Track track, int limit = 12, CancellationToken ct = default);

    Task<IReadOnlyList<Track>> GetTrendingAsync(int limit = 20, CancellationToken ct = default);

    Task<IReadOnlyList<Track>> GetNewReleasesAsync(int limit = 20, CancellationToken ct = default);

    Task<IReadOnlyList<ArtistResult>> SearchArtistsAsync(string query, int limit = 12, CancellationToken ct = default);

    Task<IReadOnlyList<AlbumResult>> SearchAlbumsAsync(string query, int limit = 12, CancellationToken ct = default);

    Task<IReadOnlyList<PlaylistResult>> SearchPlaylistsAsync(string query, int limit = 12, CancellationToken ct = default);

    Task<ArtistDetails?> GetArtistAsync(string browseId, CancellationToken ct = default);

    Task<AlbumDetails?> GetAlbumAsync(string browseId, CancellationToken ct = default);

    Task<PlaylistDetails?> GetPlaylistAsync(string urlOrId, CancellationToken ct = default);

    Task<ResolvedStream> ResolveStreamAsync(Track track, CancellationToken ct = default);

    Task<ResolvedStream> ReresolveStreamAsync(Track track, CancellationToken ct = default);

    IAsyncEnumerable<DownloadProgress> DownloadAsync(Track track, string outputTemplate, CancellationToken ct = default);
}
