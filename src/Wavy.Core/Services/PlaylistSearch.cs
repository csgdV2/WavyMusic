using Wavy.Core.Models;

namespace Wavy.Core.Services;

public static class PlaylistSearch
{
    public static IEnumerable<Track> Filter(IEnumerable<Track> tracks, string? query)
    {
        var words = (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return tracks.Where(t => words.All(word => $"{t.Title} {t.Artist} {t.Album}".Contains(word, StringComparison.OrdinalIgnoreCase)));
    }
}
