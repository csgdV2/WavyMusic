using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicApp.Core.Abstractions;
using MusicApp.Core.Models;
using MusicApp.Core.Services;
using MusicApp.Services;

namespace MusicApp.ViewModels;

public sealed partial class PlaylistsViewModel : ObservableObject
{
    private readonly PlaylistService _playlists;
    private readonly PlayerViewModel _player;
    private readonly IYtDlpService _ytDlp;
    private readonly SpotifyImportService _spotify;

    private bool _loading;

    [ObservableProperty] private Playlist? _selected;

    public PlaylistsViewModel(PlaylistService playlists, PlayerViewModel player, IYtDlpService ytDlp, SpotifyImportService spotify)
    {
        _playlists = playlists;
        _player = player;
        _ytDlp = ytDlp;
        _spotify = spotify;

        _playlists.PlaylistChanged += (_, playlist) =>
        {
            if (playlist == Selected)
                LoadTracks(playlist);
            OnPropertyChanged(nameof(SelectedSummary));
        };

        Tracks.CollectionChanged += (_, _) =>
        {
            if (!_loading && Selected is { } playlist)
                _playlists.SetTracks(playlist, Tracks);
        };
    }

    public ObservableCollection<Playlist> Playlists => _playlists.Playlists;

    public ObservableCollection<Track> Tracks { get; } = new();

    public bool HasSelection => Selected is not null;

    public bool SelectedIsEmpty => Selected is not null && Tracks.Count == 0;

    public string SelectedSummary => Selected?.SummaryText ?? string.Empty;

    public void Create(string name) => Selected = _playlists.Create(name);

    public void Rename(string name)
    {
        if (Selected is { } playlist)
            _playlists.Rename(playlist, name);
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (Selected is not { } playlist)
            return;

        _playlists.Delete(playlist);
        Selected = Playlists.FirstOrDefault();
    }

    [RelayCommand]
    private async Task PlaySelectedAsync()
    {
        if (Tracks.Count > 0)
            await _player.PlayFromAsync(Tracks.ToList(), 0);
    }

    [RelayCommand]
    private async Task ShuffleSelectedAsync()
    {
        if (Tracks.Count > 0)
            await _player.PlayShuffledAsync(Tracks.ToList());
    }

    public Task PlayFromAsync(Track track)
    {
        var index = Tracks.IndexOf(track);
        return index < 0 ? Task.CompletedTask : _player.PlayFromAsync(Tracks.ToList(), index);
    }

    public void Remove(Track track)
    {
        if (Selected is { } playlist)
            _playlists.Remove(playlist, track);
    }

    public string SuggestedName => _playlists.SuggestName();

    public async Task<string?> ImportAsync(string link, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(link))
            return "Paste a YouTube Music playlist link first.";

        if (SpotifyImportService.LooksLikeSpotify(link))
            return await ImportSpotifyAsync(link, ct).ConfigureAwait(true);

        PlaylistDetails? details;
        try
        {
            details = await _ytDlp.GetPlaylistAsync(link, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        if (details is null)
            return "That link didn't work. It needs to be a YouTube Music or YouTube playlist link, and the playlist has to be public.";

        if (details.Tracks.Count == 0)
            return $"“{details.Title}” has no songs in it.";

        var playlist = _playlists.Create(UniqueName(details.Title));
        foreach (var track in details.Tracks)
            _playlists.TryAdd(playlist, track);

        Selected = playlist;
        return null;
    }

    public async Task<string?> ImportSpotifyAsync(string link, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(link))
            return "Paste a Spotify playlist or album link first.";

        if (!SpotifyImportService.LooksLikeSpotify(link))
            return "That doesn't look like a Spotify playlist or album link.";

        PlaylistDetails? details;
        try
        {
            details = await _spotify.GetPlaylistAsync(link, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception e)
        {
            return $"Spotify import failed: {e.Message}";
        }

        if (details is null)
            return "That link didn't work. The playlist or album has to be public.";

        if (details.Tracks.Count == 0)
            return $"Couldn't match any songs from “{details.Title}”.";

        var playlist = _playlists.Create(UniqueName(details.Title));
        foreach (var track in details.Tracks)
            _playlists.TryAdd(playlist, track);

        Selected = playlist;
        return null;
    }

    private string UniqueName(string name)
    {
        if (Playlists.All(p => p.Name != name))
            return name;

        for (var n = 2; ; n++)
        {
            var candidate = $"{name} ({n})";
            if (Playlists.All(p => p.Name != candidate))
                return candidate;
        }
    }

    partial void OnSelectedChanged(Playlist? value)
    {
        LoadTracks(value);
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedSummary));
    }

    public void Refresh()
    {
        Selected = Playlists.Contains(Selected!) ? Selected : Playlists.FirstOrDefault();
        LoadTracks(Selected);
    }

    private void LoadTracks(Playlist? playlist)
    {
        _loading = true;
        try
        {
            Tracks.Clear();
            if (playlist is not null)
                foreach (var track in playlist.Tracks)
                    Tracks.Add(track);
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(SelectedIsEmpty));
    }
}
