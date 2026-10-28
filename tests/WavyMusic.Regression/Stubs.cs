using Wavy.Core.Models;

namespace Wavy.Services;
public sealed class SettingsService
{
    public bool AudioNormalization { get; set; }
    public bool DiscordPresence { get; set; } = true;
    public string DiscordApplicationId => "test-application";
    public event EventHandler<string>? Changed;
    public double[] EqualizerBands { get; set; } = new double[10];
    public List<string> History { get; } = new();
    public void AddSearchHistory(string query) => History.Add(query);
}
public sealed class CacheService
{
    public IReadOnlyList<Track> GetCachedTracks() => new[] { new Track { Id = "recent", Title = "Recent song" } };
}
public sealed class PlaybackService
{
    public Track? CurrentTrack { get; set; }
    public bool IsPlaying { get; set; }
    public TimeSpan Position { get; set; }
    public TimeSpan Duration { get; set; }
}
