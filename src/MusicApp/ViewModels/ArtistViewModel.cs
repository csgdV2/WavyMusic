using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MusicApp.Core.Abstractions;
using MusicApp.Core.Models;

namespace MusicApp.ViewModels;

public sealed partial class ArtistViewModel : ObservableObject
{
    private const int Collapsed = 5;

    private readonly IYtDlpService _ytDlp;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _message;

    [ObservableProperty] private ArtistDetails? _artist;

    [ObservableProperty] private bool _showingAllSongs;

    public ObservableCollection<Track> Songs { get; } = new();

    private List<Track> _allSongs = new();

    public IReadOnlyList<Track> AllSongs => _allSongs;

    public bool IsLoaded => Artist is not null;

    public bool CanShowMoreSongs => !ShowingAllSongs && _allSongs.Count > Collapsed;

    public bool HasSimilar => Artist?.Similar.Count > 0;

    partial void OnArtistChanged(ArtistDetails? value)
    {
        _allSongs = value?.TopSongs.ToList() ?? new List<Track>();
        ShowingAllSongs = false;
        FillSongs();
        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(HasSimilar));
    }

    partial void OnShowingAllSongsChanged(bool value)
    {
        FillSongs();
        OnPropertyChanged(nameof(CanShowMoreSongs));
    }

    public void ShowAllSongs() => ShowingAllSongs = true;

    private void FillSongs()
    {
        Songs.Clear();

        var take = ShowingAllSongs ? _allSongs.Count : Math.Min(Collapsed, _allSongs.Count);
        for (var i = 0; i < take; i++)
            Songs.Add(_allSongs[i]);

        OnPropertyChanged(nameof(CanShowMoreSongs));
    }

    public ArtistViewModel(IYtDlpService ytDlp) => _ytDlp = ytDlp;

    public async Task LoadAsync(string? browseId)
    {
        if (string.IsNullOrWhiteSpace(browseId))
        {
            Message = "No artist to show.";
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsLoading = true;
        Message = null;
        Artist = null;
        try
        {
            var artist = await _ytDlp.GetArtistAsync(browseId, token);
            if (token.IsCancellationRequested)
                return;

            Artist = artist;

            Message = artist is null ? "Couldn't open this artist." : null;

            if (artist?.SongsPlaylistId is { Length: > 0 } listId)
                _ = LoadAllSongsAsync(listId, artist, token);
        }
        catch (OperationCanceledException)
        {

        }
        catch (Exception ex)
        {
            Message = $"Couldn't open this artist: {ex.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested)
                IsLoading = false;
        }
    }

    public void Cancel() => _cts?.Cancel();

    private async Task LoadAllSongsAsync(string listId, ArtistDetails artist, CancellationToken token)
    {
        try
        {
            var details = await _ytDlp.GetPlaylistAsync(listId, token);
            if (token.IsCancellationRequested || !ReferenceEquals(Artist, artist))
                return;

            if (details?.Tracks is not { Count: > 0 } tracks)
                return;

            var merged = new List<Track>(tracks.Count + _allSongs.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var track in _allSongs)
                if (seen.Add(track.Id))
                    merged.Add(track);

            foreach (var track in tracks)
                if (seen.Add(track.Id))
                    merged.Add(track);

            _allSongs = merged;
            FillSongs();
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }
}
