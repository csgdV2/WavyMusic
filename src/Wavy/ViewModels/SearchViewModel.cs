using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using Wavy.Core.Abstractions;
using Wavy.Core.Models;
using Wavy.Services;

namespace Wavy.ViewModels;

public sealed partial class SearchViewModel : ObservableObject
{
    private const string IdlePrompt = "Search for a song to get started.";

    private readonly IYtDlpService _ytDlp;
    private readonly SettingsService _settings;
    private readonly CacheService _cache;
    private CancellationTokenSource? _cts;

    private string? _query;
    private (SearchFilter Filter, string Query)? _loaded;

    [ObservableProperty] private ArtistResult? _featuredArtist;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private SearchFilter _selectedFilter = SearchFilter.Songs;

    public ObservableCollection<Track> Results { get; } = new();
    public ObservableCollection<ArtistResult> Artists { get; } = new();
    public ObservableCollection<AlbumResult> Albums { get; } = new();
    public ObservableCollection<PlaylistResult> Playlists { get; } = new();

    public SearchViewModel(IYtDlpService ytDlp, SettingsService settings, CacheService cache)
    {
        _ytDlp = ytDlp;
        _settings = settings;
        _cache = cache;
        RefreshRecent();
        Message = Recent.Count == 0 ? IdlePrompt : null;

        void OnChanged(object? _, NotifyCollectionChangedEventArgs __) =>
            OnPropertyChanged(nameof(ShowBusyIndicator));

        Results.CollectionChanged += OnChanged;
        Artists.CollectionChanged += OnChanged;
        Albums.CollectionChanged += OnChanged;
        Playlists.CollectionChanged += OnChanged;
    }

    public bool HasQuery => !string.IsNullOrEmpty(_query);

    public string? Query => _query;
    public ObservableCollection<Track> Recent { get; } = new();
    public void RefreshRecent()
    {
        Recent.Clear();
        foreach (var track in _cache.GetCachedTracks().Take(20)) Recent.Add(track);
    }
    public void CancelPendingSearch()
    {
        _cts?.Cancel();
        IsSearching = false;
    }

    public bool ShowTracks => HasQuery && (SelectedFilter is SearchFilter.Songs or SearchFilter.Videos);
    public bool ShowArtists => HasQuery && SelectedFilter == SearchFilter.Artists;
    public bool ShowAlbums => HasQuery && SelectedFilter == SearchFilter.Albums;
    public bool ShowPlaylists => HasQuery && SelectedFilter == SearchFilter.Playlists;
    public bool ShowFeaturedArtist => HasQuery && SelectedFilter == SearchFilter.Songs && FeaturedArtist is not null;

    partial void OnFeaturedArtistChanged(ArtistResult? value) => OnPropertyChanged(nameof(ShowFeaturedArtist));

    private void QueryVisibilityChanged()
    {
        OnPropertyChanged(nameof(ShowFeaturedArtist));
        OnPropertyChanged(nameof(HasQuery)); OnPropertyChanged(nameof(Query));
        OnPropertyChanged(nameof(ShowTracks)); OnPropertyChanged(nameof(ShowArtists));
        OnPropertyChanged(nameof(ShowAlbums)); OnPropertyChanged(nameof(ShowPlaylists));
    }

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
        FeaturedArtist = null;
        OnPropertyChanged(nameof(ShowFeaturedArtist));
        OnPropertyChanged(nameof(ShowTracks));
        OnPropertyChanged(nameof(ShowArtists));
        OnPropertyChanged(nameof(ShowAlbums));
        OnPropertyChanged(nameof(ShowPlaylists));
        OnPropertyChanged(nameof(ShowBusyIndicator));

        if (!string.IsNullOrEmpty(_query))
            _ = RunAsync(_query);
        else
            Message = Recent.Count == 0 ? IdlePrompt : null;
    }

    public void Clear()
    {

        _cts?.Cancel();
        _cts = null;
        _query = null;
        _loaded = null;
        FeaturedArtist = null;
        Results.Clear();
        Artists.Clear();
        Albums.Clear();
        Playlists.Clear();
        IsSearching = false;
        RefreshRecent();
        Message = Recent.Count == 0 ? IdlePrompt : null;
        QueryVisibilityChanged();
    }

    public Task SearchAsync(string? query, bool recordInHistory = false)
    {
        query = query?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            Clear();
            return Task.CompletedTask;
        }

        if (recordInHistory)
            _settings.AddSearchHistory(query);

        var isNewQuery = !string.Equals(query, _query, StringComparison.OrdinalIgnoreCase);
        _query = query;
        if (isNewQuery)
        {
            FeaturedArtist = null;
            Results.Clear(); Artists.Clear(); Albums.Clear(); Playlists.Clear();
            QueryVisibilityChanged();
        }

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
                    var artistMatch = filter == SearchFilter.Songs ? FindArtistAsync(query, token) : Task.CompletedTask;
                    var tracks = await _ytDlp.SearchAsync(query, filter, 12, token);
                    if (token.IsCancellationRequested)
                        return;
                    count = Fill(Results, tracks);
                    await artistMatch;
                    if (token.IsCancellationRequested) return;
                    if (FeaturedArtist is not null) count++;
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
            if (!token.IsCancellationRequested) Message = $"Search failed: {ex.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested)
                IsSearching = false;
        }
    }

    private async Task FindArtistAsync(string query, CancellationToken token)
    {
        try
        {
            var artists = await _ytDlp.SearchArtistsAsync(query, 5, token);
            if (token.IsCancellationRequested) return;
            var normalized = ArtistName(query);
            FeaturedArtist = artists.FirstOrDefault(a => ArtistName(a.Name) == normalized)
                ?? (normalized.Length >= 3 ? artists.FirstOrDefault(a => ArtistName(a.Name).StartsWith(normalized, StringComparison.Ordinal)) : null);
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private static string ArtistName(string value)
    {
        value = value.Trim();
        if (value.StartsWith("the ", StringComparison.OrdinalIgnoreCase)) value = value[4..];
        return new string(value.Normalize(System.Text.NormalizationForm.FormD)
            .Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private static int Fill<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        target.Clear();
        foreach (var item in items)
            target.Add(item);
        return items.Count;
    }
}
