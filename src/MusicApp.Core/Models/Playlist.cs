using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace MusicApp.Core.Models;

public sealed class Playlist : INotifyPropertyChanged
{
    private string _name = string.Empty;

    public required string Id { get; init; }

    public required string Name
    {
        get => _name;
        set
        {
            if (_name == value)
                return;

            _name = value;
            Raise(nameof(Name));
        }
    }

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public List<Track> Tracks { get; init; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public void NotifyTracksChanged()
    {
        Raise(nameof(Tracks));
        Raise(nameof(SummaryText));
        Raise(nameof(ThumbnailUrl));
        Raise(nameof(CoverUrls));
        Raise(nameof(HasMosaic));
    }

    private void Raise(string property) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    [JsonIgnore]
    public string SummaryText => Tracks.Count switch
    {
        0 => "No songs yet",
        1 => "1 song",
        var n => $"{n} songs",
    };

    [JsonIgnore]
    public string? ThumbnailUrl => Tracks.Count > 0 ? Tracks[0].ThumbnailUrl : null;

    [JsonIgnore]
    public IReadOnlyList<string> CoverUrls =>
        Tracks.Select(t => t.ThumbnailUrl)
              .Where(url => !string.IsNullOrWhiteSpace(url))
              .Select(url => url!)
              .Distinct(StringComparer.Ordinal)
              .Take(4)
              .ToList();

    [JsonIgnore]
    public bool HasMosaic => CoverUrls.Count >= 4;
}
