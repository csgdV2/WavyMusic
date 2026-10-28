using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Wavy.Core.Models;
using Wavy.Services;

namespace Wavy.ViewModels;

public sealed partial class AlbumsViewModel : ObservableObject
{
    private readonly FavoritesService _favorites;

    public AlbumsViewModel(FavoritesService favorites)
    {
        _favorites = favorites;

        _favorites.Albums.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public ObservableCollection<AlbumResult> Albums => _favorites.Albums;

    public bool IsEmpty => Albums.Count == 0;

    public void Remove(AlbumResult album) => _favorites.Remove(album.BrowseId);
}
