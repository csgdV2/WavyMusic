using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using Wavy.Core.Services;
using Wavy.Core.Abstractions;
using Wavy.Core.Models;
using Wavy.Services;

namespace Wavy.ViewModels;

public sealed partial class OnlinePlaylistViewModel : ObservableObject
{
    private readonly IYtDlpService _ytDlp;
    private readonly PlaylistService _playlists;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private string? _saveMessage;

    [ObservableProperty] private PlaylistDetails? _playlist;

    [ObservableProperty] private string _searchQuery = string.Empty;
    public ObservableCollection<Track> VisibleTracks { get; } = new();
    public bool NoSearchMatches => HasTracks && VisibleTracks.Count == 0;
    partial void OnSearchQueryChanged(string value) => FilterTracks();
    private void FilterTracks()
    {
        VisibleTracks.Clear();
        foreach (var track in PlaylistSearch.Filter(Playlist?.Tracks ?? Array.Empty<Track>(), SearchQuery)) VisibleTracks.Add(track);
        OnPropertyChanged(nameof(NoSearchMatches));
    }

    public bool IsLoaded => Playlist is not null;

    public bool HasTracks => Playlist is { Tracks.Count: > 0 };

    public string CountText => Playlist?.Tracks.Count switch
    {
        null or 0 => "No songs",
        1 => "1 song",
        var n => $"{n} songs",
    };

    public OnlinePlaylistViewModel(IYtDlpService ytDlp, PlaylistService playlists)
    {
        _ytDlp = ytDlp;
        _playlists = playlists;
    }

    partial void OnPlaylistChanged(PlaylistDetails? value)
    {
        SearchQuery = string.Empty;
        FilterTracks();
        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(HasTracks));
        OnPropertyChanged(nameof(CountText));
    }

    public async Task LoadAsync(string? browseId)
    {
        if (string.IsNullOrWhiteSpace(browseId))
        {
            Message = "No playlist to show.";
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsLoading = true;
        Message = null;
        SaveMessage = null;
        Playlist = null;
        try
        {
            var playlist = await _ytDlp.GetPlaylistAsync(browseId, token);
            if (token.IsCancellationRequested)
                return;

            Playlist = playlist;
            Message = playlist is null ? "Couldn't open this playlist." : null;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Message = $"Couldn't open this playlist: {ex.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested)
                IsLoading = false;
        }
    }

    public void SaveToLibrary()
    {
        if (Playlist is not { Tracks.Count: > 0 } details)
            return;

        var name = string.IsNullOrWhiteSpace(details.Title) ? _playlists.SuggestName() : details.Title;
        var created = _playlists.Create(name);
        _playlists.SetTracks(created, details.Tracks);

        SaveMessage = $"Saved as \"{created.Name}\" in your playlists.";
    }

    public void Cancel() => _cts?.Cancel();
}
