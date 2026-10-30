using Wavy.Core.Models;

namespace Wavy.Services;
public sealed class SettingsService
{
    public bool AutomaticUpdateChecks { get; set; } = true;
    public DateTimeOffset LastUpdateCheck { get; set; }
    public bool AudioNormalization { get; set; }
    public double Volume { get; set; } = 0.7;
    public bool PrefetchNext { get; set; } = true;
    public bool DiscordPresence { get; set; } = true;
    public string DiscordApplicationId => "test-application";
    public event EventHandler<string>? Changed;
    public double[] EqualizerBands { get; set; } = new double[10];
    public List<string> History { get; } = new();
    public void AddSearchHistory(string query) => History.Add(query);
}
public sealed class CacheService
{
    public Dictionary<string, (string Key, double Lufs, double Peak)> AudioAnalysis { get; } = new();
    public int SavedAnalysisReads { get; private set; }
    public (double IntegratedLufs, double Peak)? GetAudioAnalysis(string path, string key)
    {
        if (!AudioAnalysis.TryGetValue(path, out var saved) || saved.Key != key) return null;
        SavedAnalysisReads++;
        return (saved.Lufs, saved.Peak);
    }
    public void SaveAudioAnalysis(string path, string key, double lufs, double peak) => AudioAnalysis[path] = (key, lufs, peak);
    public string? TryGetLocalPath(string id) => null;
    public void MarkPlayed(Track track) { }
    public Task EnsureCachedAsync(Track track, CancellationToken ct = default) => Task.CompletedTask;
    public IReadOnlyList<Track> GetCachedTracks() => new[] { new Track { Id = "recent", Title = "Recent song" } };
}
public sealed class PlaybackService : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? MediaEndedNaturally;
    public event EventHandler<Track>? AdvancedGaplessly;
    public bool IsBusy { get; set; }
    public bool IsBuffering { get; set; }
    public bool IsLooping { get; set; }
    public string? StatusMessage { get; set; }
    public double Volume { get; set; }
    public Track? PreparedNext { get; private set; }
    public Task PlayAsync(Track track, string? localPath = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        CurrentTrack = track;
        IsPlaying = true;
        PropertyChanged?.Invoke(this, new(nameof(CurrentTrack)));
        return Task.CompletedTask;
    }
    public void Seek(TimeSpan position) => Position = position;
    public void TogglePlayPause() => IsPlaying = !IsPlaying;
    public void Restart() => Position = TimeSpan.Zero;
    public void ClearStatus() => StatusMessage = null;
    public void ClearPreparedNext() => PreparedNext = null;
    public Task PrepareNextAsync(Track track) { PreparedNext = track; return Task.CompletedTask; }

    public Track? CurrentTrack { get; set; }
    public bool IsPlaying { get; set; }
    public TimeSpan Position { get; set; }
    public TimeSpan Duration { get; set; }
}

public sealed class PlaylistService
{
    public System.Collections.ObjectModel.ObservableCollection<Playlist> Playlists { get; } = new();
    public event EventHandler<Playlist>? PlaylistChanged;
    public Playlist Create(string name) { var p = new Playlist { Id = Guid.NewGuid().ToString(), Name = name }; Playlists.Add(p); return p; }
    public void Rename(Playlist p, string name) => p.Name = name;
    public void Delete(Playlist p) => Playlists.Remove(p);
    public string SuggestName() => "Playlist";
    public void SetTracks(Playlist p, IEnumerable<Track> tracks)
    {
        var copy = tracks.ToList();
        if (p.Tracks.SequenceEqual(copy)) return;
        p.Tracks.Clear(); p.Tracks.AddRange(copy); PlaylistChanged?.Invoke(this, p);
    }
    public void TryAdd(Playlist p, Track t) { if (!p.Tracks.Any(x => x.Id == t.Id)) { p.Tracks.Add(t); PlaylistChanged?.Invoke(this, p); } }
    public void Remove(Playlist p, Track t) { p.Tracks.Remove(t); PlaylistChanged?.Invoke(this, p); }
}

public sealed class FavoritesService
{
    public System.Collections.ObjectModel.ObservableCollection<AlbumResult> Albums { get; } = new();
}
