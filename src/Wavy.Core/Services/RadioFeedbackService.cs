using System.Text.Json;
using Wavy.Core.Models;

namespace Wavy.Core.Services;

public enum RadioPreference { None, MoreLikeThis, DontRecommend }

public sealed class RadioFeedbackService
{
    private readonly string? _path;
    private readonly List<Track> _preferred = new();
    private readonly List<Track> _excluded = new();
    public event EventHandler? Changed;
    public long Revision { get; private set; }
    public IReadOnlyList<Track> Excluded => _excluded.ToArray();
    public IReadOnlyList<Track> Preferred => _preferred.ToArray();

    public RadioFeedbackService(string? path = null)
    {
        _path = path;
        try
        {
            if (path is null || !File.Exists(path)) return;
            var saved = JsonSerializer.Deserialize<SavedFeedback>(File.ReadAllText(path));
            foreach (var track in saved?.Preferred ?? new()) AddLoaded(_preferred, track, 30);
            foreach (var track in saved?.Excluded ?? new()) AddLoaded(_excluded, track, 300);
            _preferred.RemoveAll(IsExcluded);
        }
        catch { }
    }

    public RadioPreference GetPreference(Track track) => IsExcluded(track) ? RadioPreference.DontRecommend
        : _preferred.Any(t => SameSong(t, track)) ? RadioPreference.MoreLikeThis : RadioPreference.None;

    public bool IsExcluded(Track track) => _excluded.Any(t => SameSong(t, track));

    public IReadOnlyList<Track> Seeds(IEnumerable<Track> history, int limit) => RecommendationSelector.Seeds(
        _preferred.Take(1).Concat(history).Where(t => !IsExcluded(t)), limit);

    public IReadOnlyList<Track> RadioSeeds(Track current) => new[] { current }.Concat(_preferred.Take(1))
        .DistinctBy(t => t.Id).ToArray();

    public void SetPreference(Track track, RadioPreference preference)
    {
        if (string.IsNullOrWhiteSpace(track.Id) || string.IsNullOrWhiteSpace(track.Title)) return;
        _preferred.RemoveAll(t => SameSong(t, track));
        _excluded.RemoveAll(t => SameSong(t, track));
        if (preference == RadioPreference.MoreLikeThis) _preferred.Insert(0, track);
        if (preference == RadioPreference.DontRecommend) _excluded.Insert(0, track);
        if (_preferred.Count > 30) _preferred.RemoveRange(30, _preferred.Count - 30);
        if (_excluded.Count > 300) _excluded.RemoveRange(300, _excluded.Count - 300);
        Save();
        Revision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        _preferred.Clear();
        _excluded.Clear();
        Save();
        Revision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void AddLoaded(List<Track> tracks, Track? track, int limit)
    {
        if (track is not null && !string.IsNullOrWhiteSpace(track.Id) && !string.IsNullOrWhiteSpace(track.Title)
            && tracks.Count < limit && !tracks.Any(t => SameSong(t, track))) tracks.Add(track);
    }

    private static bool SameSong(Track a, Track b) => a.Id == b.Id ||
        (!string.IsNullOrWhiteSpace(a.Artist) && !string.IsNullOrWhiteSpace(b.Artist)
        && string.Equals(Key(a.Artist), Key(b.Artist), StringComparison.OrdinalIgnoreCase)
        && string.Equals(Key(a.Title), Key(b.Title), StringComparison.OrdinalIgnoreCase));

    private static string Key(string text) => string.Join(" ", text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private void Save()
    {
        if (_path is null) return;
        try
        {
            if (Path.GetDirectoryName(_path) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(new SavedFeedback { Preferred = _preferred, Excluded = _excluded }));
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
        catch { }
    }

    private sealed class SavedFeedback
    {
        public List<Track>? Preferred { get; set; }
        public List<Track>? Excluded { get; set; }
    }
}
