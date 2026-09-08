using System.Text.Json.Serialization;

namespace MusicApp.Core.Models;

public sealed class Track
{

    public required string Id { get; init; }

    public required string Title { get; init; }

    public string? Artist { get; init; }

    public string? Album { get; init; }

    public TimeSpan? Duration { get; init; }

    public int? ReleaseYear { get; init; }

    public string? ThumbnailUrl { get; init; }

    [JsonIgnore]
    public string DurationText => Duration is { } d
        ? (d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss"))
        : string.Empty;

    [JsonIgnore]
    public string SubtitleText =>
        Album is { Length: > 0 } album
        && !string.Equals(album, Title, StringComparison.OrdinalIgnoreCase)
        && Artist is { Length: > 0 } artist
            ? $"{artist} • {album}"
            : Artist ?? string.Empty;

    [JsonIgnore]
    public string SourceUrl => $"https://www.youtube.com/watch?v={Id}";

    [JsonIgnore]
    public string MusicUrl => $"https://music.youtube.com/watch?v={Id}";
}
