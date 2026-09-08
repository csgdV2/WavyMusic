using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using MusicApp.Core.Abstractions;
using MusicApp.Core.Models;

namespace MusicApp.ViewModels;

public sealed partial class SearchViewModel : ObservableObject
{
    private const string IdlePrompt = "Search for a song to get started.";

    private readonly IYtDlpService _ytDlp;
    private CancellationTokenSource? _cts;

    private string? _query;
    private (SearchFilter Filter, string Query)? _loaded;

    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string? _message = IdlePrompt;
    [ObservableProperty] private SearchFilter _selectedFilter = SearchFilter.Songs;

    public ObservableCollection<Track> Results { get; } = new();
    public ObservableCollection<ArtistResult> Artists { get; } = new();
    public ObservableCollection<AlbumResult> Albums { get; } = new();
    public ObservableCollection<PlaylistResult> Playlists { get; } = new();

    public SearchViewModel(IYtDlpService ytDlp)
    {
        _ytDlp = ytDlp;

        void OnChanged(object? _, NotifyCollectionChangedEventArgs __) =>
            OnPropertyChanged(nameof(ShowBusyIndicator));

        Results.CollectionChanged += OnChanged;
        Artists.CollectionChanged += OnChanged;
        Albums.CollectionChanged += OnChanged;
        Playlists.CollectionChanged += OnChanged;
    }

    public bool HasQuery => !string.IsNullOrEmpty(_query);

    public string? Query => _query;

    public bool ShowTracks => SelectedFilter is SearchFilter.Songs or SearchFilter.Videos;
    public bool ShowArtists => SelectedFilter == SearchFilter.Artists;
    public bool ShowAlbums => SelectedFilter == SearchFilter.Albums;
    public bool ShowPlaylists => SelectedFilter == SearchFilter.Playlists;

    public bool ShowBusyIndicator => IsSearching && VisibleCount == 0;

    private int VisibleCount => SelectedFilter switch
    {
        SearchFilter.Artists => Artists.Count,
        SearchFilter.Albums => Albums.Count,
        SearchFilter.Playlists => Playlists.Count,
        _ => Results.Count,
    };

    partial void OnIsSearchingChanged(bool value) => OnPropertyChanged(nameof(ShowBusyIndicator));

    partial void OnSelectedFilterChanged(SearchFilter value)
    {
        OnPropertyChanged(nameof(ShowTracks));
        OnPropertyChanged(nameof(ShowArtists));
        OnPropertyChanged(nameof(ShowAlbums));
        OnPropertyChanged(nameof(ShowPlaylists));
        OnPropertyChanged(nameof(ShowBusyIndicator));

        if (!string.IsNullOrEmpty(_query))
            _ = RunAsync(_query);
        else
            Message = IdlePrompt;
    }

    public void Clear()
    {

        _cts?.Cancel();
        _cts = null;
        _query = null;
        _loaded = null;
        Results.Clear();
        Artists.Clear();
        Albums.Clear();
        Playlists.Clear();
        IsSearching = false;
        Message = IdlePrompt;
        OnPropertyChanged(nameof(HasQuery));
    }

    public Task SearchAsync(string? query)
    {
        query = query?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            Clear();
            return Task.CompletedTask;
        }

        var isNewQuery = !string.Equals(query, _query, StringComparison.OrdinalIgnoreCase);
        _query = query;
        if (isNewQuery)
            OnPropertyChanged(nameof(HasQuery));

        return RunAsync(query);
    }

    private async Task RunAsync(string query)
    {
        var filter = SelectedFilter;

        if (_loaded == (filter, query) && VisibleCount > 0)
            return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsSearching = true;
        Message = null;
        try
        {

            int count;
            switch (filter)
            {
                case SearchFilter.Artists:
                    var artists = await _ytDlp.SearchArtistsAsync(query, 12, token);
                    if (token.IsCancellationRequested)
                        return;
                    count = Fill(Artists, artists);
                    break;

                case SearchFilter.Albums:
                    var albums = await _ytDlp.SearchAlbumsAsync(query, 12, token);
                    if (token.IsCancellationRequested)
                        return;
                    count = Fill(Albums, albums);
                    break;

                case SearchFilter.Playlists:
                    var playlists = await _ytDlp.SearchPlaylistsAsync(query, 12, token);
                    if (token.IsCancellationRequested)
                        return;
                    count = Fill(Playlists, playlists);
                    break;

                default:
                    var tracks = await _ytDlp.SearchAsync(query, filter, 12, token);
                    if (token.IsCancellationRequested)
                        return;
                    count = Fill(Results, tracks);
                    break;
            }

            _loaded = (filter, query);
            Message = count == 0 ? "No results found." : null;
        }
        catch (OperationCanceledException)
        {

        }
        catch (Exception ex)
        {
            Message = $"Search failed: {ex.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested)
                IsSearching = false;
        }
    }

    private static int Fill<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        target.Clear();
        foreach (var item in items)
            target.Add(item);
        return items.Count;
    }
}
