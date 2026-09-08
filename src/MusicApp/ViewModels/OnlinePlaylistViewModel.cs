using CommunityToolkit.Mvvm.ComponentModel;
using MusicApp.Core.Abstractions;
using MusicApp.Core.Models;
using MusicApp.Services;

namespace MusicApp.ViewModels;

public sealed partial class OnlinePlaylistViewModel : ObservableObject
{
    private readonly IYtDlpService _ytDlp;
    private readonly PlaylistService _playlists;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private string? _saveMessage;

    [ObservableProperty] private PlaylistDetails? _playlist;

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
