using System.Text.Json;
using Wavy.Core.Models;

namespace Wavy.Core.Services;

public sealed record RecapSong(Track Track, double Seconds, int Plays);
public sealed record RecapArtist(string Name, double Seconds, int Plays, string? BrowseId = null);
public sealed record ListeningRecap(double Seconds, int Plays, int Songs, int Artists,
    IReadOnlyList<RecapSong> TopSongs, IReadOnlyList<RecapArtist> TopArtists);

public sealed class ListeningHistoryService
{
    private readonly string? _path;
    private readonly List<DayEntry> _days = new();
    private Track? _track;
    private bool _playing;
    private double _sessionSeconds;
    private bool _counted;
    private bool _dirty;
    private double _unsavedSeconds;
    public DateTimeOffset StartedUtc { get; private set; } = DateTimeOffset.UtcNow;

    public ListeningHistoryService(string? path = null)
    {
        _path = path;
        try
        {
            if (path is null || !File.Exists(path)) return;
            var saved = JsonSerializer.Deserialize<SavedHistory>(File.ReadAllText(path));
            if (saved is null) return;
            StartedUtc = saved.StartedUtc;
            _days.AddRange((saved.Days ?? new()).Where(d => d.Track is not null && !string.IsNullOrWhiteSpace(d.Track.Id)
                && double.IsFinite(d.Seconds) && d.Seconds >= 0 && d.Plays >= 0));
        }
        catch { }
    }

    public void Advance(Track? track, bool playing, TimeSpan elapsed, DateTimeOffset now)
    {
        var seconds = elapsed.TotalSeconds;
        if (_playing && _track is not null && seconds > 0 && seconds <= 5)
        {
            var day = DateOnly.FromDateTime(now.LocalDateTime);
            var entry = _days.FirstOrDefault(d => d.Day == day && d.Track.Id == _track.Id);
            if (entry is null) { entry = new DayEntry { Day = day, Track = _track }; _days.Add(entry); }
            entry.Seconds += seconds;
            _sessionSeconds += seconds;
            var threshold = _track.Duration is { TotalSeconds: > 0 } duration ? Math.Min(30, duration.TotalSeconds / 2) : 30;
            if (!_counted && _sessionSeconds >= threshold) { entry.Plays++; _counted = true; }
            _dirty = true;
            _unsavedSeconds += seconds;
        }
        if (track?.Id != _track?.Id) { _sessionSeconds = 0; _counted = false; }
        if ((_playing && !playing) || track?.Id != _track?.Id || _unsavedSeconds >= 30) Flush();
        _track = track;
        _playing = playing;
    }

    public void EndSession()
    {
        _track = null;
        _playing = false;
        _sessionSeconds = 0;
        _counted = false;
        Flush();
    }

    public ListeningRecap Recap(DateOnly from, DateOnly to)
    {
        var entries = _days.Where(d => d.Day >= from && d.Day <= to).ToList();
        var songs = entries.GroupBy(d => d.Track.Id).Select(g => new RecapSong(g.Last().Track, g.Sum(d => d.Seconds), g.Sum(d => d.Plays)))
            .OrderByDescending(d => d.Plays).ThenByDescending(d => d.Seconds).ToList();
        var artists = entries.Where(d => !string.IsNullOrWhiteSpace(d.Track.Artist)).GroupBy(d => d.Track.Artist!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new RecapArtist(g.Key, g.Sum(d => d.Seconds), g.Sum(d => d.Plays),
                g.Select(d => d.Track.ArtistBrowseId).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id))))
            .OrderByDescending(d => d.Plays).ThenByDescending(d => d.Seconds).ToList();
        return new ListeningRecap(entries.Sum(d => d.Seconds), entries.Sum(d => d.Plays), songs.Count, artists.Count, songs.Take(5).ToList(), artists.Take(5).ToList());
    }

    public void Flush()
    {
        if (!_dirty || _path is null) return;
        try
        {
            if (Path.GetDirectoryName(_path) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(new SavedHistory { StartedUtc = StartedUtc, Days = _days }));
            File.Move(_path + ".tmp", _path, overwrite: true);
            _dirty = false;
            _unsavedSeconds = 0;
        }
        catch { }
    }

    public sealed class DayEntry
    {
        public DateOnly Day { get; set; }
        public Track Track { get; set; } = new() { Id = "", Title = "" };
        public double Seconds { get; set; }
        public int Plays { get; set; }
    }
    private sealed class SavedHistory
    {
        public DateTimeOffset StartedUtc { get; set; }
        public List<DayEntry>? Days { get; set; }
    }
}
