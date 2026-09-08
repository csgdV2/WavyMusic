namespace MusicApp.Models;

public sealed class CacheEntry
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Artist { get; set; }
    public string? ThumbnailUrl { get; set; }
    public double? DurationSeconds { get; set; }
    public string FilePath { get; set; } = "";
    public DateTimeOffset CachedUtc { get; set; }
    public DateTimeOffset LastPlayedUtc { get; set; }

    public bool HiddenFromHistory { get; set; }

    public Core.Models.Track ToTrack() => new()
    {
        Id = Id,
        Title = Title,
        Artist = Artist,
        ThumbnailUrl = ThumbnailUrl,
        Duration = DurationSeconds is { } s ? TimeSpan.FromSeconds(s) : null,
    };
}
