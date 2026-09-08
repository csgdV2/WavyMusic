using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicApp.Core.Models;
using MusicApp.Services;

namespace MusicApp.ViewModels;

public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly CacheService _cache;

    [ObservableProperty] private bool _isEmpty = true;

    public ObservableCollection<Track> Tracks { get; } = new();

    public LibraryViewModel(CacheService cache) => _cache = cache;

    public void Refresh()
    {
        Tracks.Clear();
        foreach (var track in _cache.GetCachedTracks())
            Tracks.Add(track);
        IsEmpty = Tracks.Count == 0;
    }

    [RelayCommand]
    private void ClearHistory()
    {
        _cache.ClearHistory();
        Refresh();
    }
}
