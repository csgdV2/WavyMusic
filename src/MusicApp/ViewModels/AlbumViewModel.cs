using CommunityToolkit.Mvvm.ComponentModel;
using MusicApp.Core.Abstractions;
using MusicApp.Core.Models;
using MusicApp.Services;

namespace MusicApp.ViewModels;

public sealed partial class AlbumViewModel : ObservableObject
{
    private readonly IYtDlpService _ytDlp;
    private readonly FavoritesService _favorites;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _message;

    [ObservableProperty] private AlbumDetails? _album;

    [ObservableProperty] private bool _isFavorite;

    public bool IsLoaded => Album is not null;

    public bool CanOpenArtist =>
        Album is { Artist.Length: > 0, ArtistBrowseId.Length: > 0 };

    public string FavoriteGlyph => IsFavorite ? "" : "";

    public string FavoriteLabel => IsFavorite ? "Favourited" : "Favourite";

    partial void OnAlbumChanged(AlbumDetails? value)
    {
        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(CanOpenArtist));
        IsFavorite = _favorites.Contains(value?.BrowseId);
    }

    partial void OnIsFavoriteChanged(bool value)
    {
        OnPropertyChanged(nameof(FavoriteGlyph));
        OnPropertyChanged(nameof(FavoriteLabel));
    }

    public void ToggleFavorite()
    {
        if (Album is not { } album)
            return;

        IsFavorite = _favorites.Toggle(new AlbumResult
        {
            BrowseId = album.BrowseId,
            Title = album.Title,
            Artist = album.Artist,
            Kind = album.Kind,
            Year = album.Year,
            ThumbnailUrl = album.ThumbnailUrl,
        });
    }

    public AlbumViewModel(IYtDlpService ytDlp, FavoritesService favorites)
    {
        _ytDlp = ytDlp;
        _favorites = favorites;
    }

    public async Task LoadAsync(string? browseId)
    {
        if (string.IsNullOrWhiteSpace(browseId))
        {
            Message = "No album to show.";
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsLoading = true;
        Message = null;
        Album = null;
        try
        {
            var album = await _ytDlp.GetAlbumAsync(browseId, token);
            if (token.IsCancellationRequested)
                return;

            Album = album;
            Message = album is null ? "Couldn't open this album." : null;
        }
        catch (OperationCanceledException)
        {

        }
        catch (Exception ex)
        {
            Message = $"Couldn't open this album: {ex.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested)
                IsLoading = false;
        }
    }

    public void Cancel() => _cts?.Cancel();
}
