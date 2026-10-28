using Wavy.Core.Models;

namespace Wavy.Core.Services;

public static class RecommendationSelector
{
    public static IReadOnlyList<Track> Seeds(IEnumerable<Track> tracks, int limit) => tracks
        .Where(t => !string.IsNullOrWhiteSpace(t.Id))
        .DistinctBy(t => ArtistKey(t), StringComparer.OrdinalIgnoreCase)
        .Take(limit).ToList();

    public static IReadOnlyList<Track> Pick(IEnumerable<IEnumerable<Track>> sources, IEnumerable<Track> excluded, int limit)
    {
        var history = excluded.ToList();
        var ids = new HashSet<string>(history.Select(t => t.Id), StringComparer.Ordinal);
        var songs = new HashSet<string>(history.Select(SongKey), StringComparer.OrdinalIgnoreCase);
        var artists = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rows = sources.Select(s => s.ToList()).ToList();
        var result = new List<Track>();
        for (var index = 0; index < rows.Select(r => r.Count).DefaultIfEmpty().Max() && result.Count < limit; index++)
            foreach (var row in rows)
            {
                if (index >= row.Count || result.Count >= limit) continue;
                var track = row[index];
                var artist = ArtistKey(track);
                var song = SongKey(track);
                if (string.IsNullOrWhiteSpace(track.Id) || string.IsNullOrWhiteSpace(track.Title)
                    || ids.Contains(track.Id) || songs.Contains(song)
                    || (artists.TryGetValue(artist, out var count) && count >= 2)) continue;
                ids.Add(track.Id);
                songs.Add(song);
                artists[artist] = artists.GetValueOrDefault(artist) + 1;
                result.Add(track);
            }
        return result;
    }

    private static string ArtistKey(Track track) => string.IsNullOrWhiteSpace(track.Artist) ? track.Id : track.Artist.Trim();
    private static string SongKey(Track track) => ArtistKey(track) + "|" + string.Join(" ", track.Title.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
