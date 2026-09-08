using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MusicApp.Core.Abstractions;
using MusicApp.Core.Models;
using MusicApp.Services;

namespace MusicApp.ViewModels;

public sealed class HomeViewModel : ObservableObject
{
    private const int RecentLimit = 14;
    private const int SuggestionLimit = 18;
    private const int MixLimit = 14;
    private const int TrendingLimit = 20;
    private const int FreshLimit = 20;

    private static readonly TimeSpan SuggestionDelay = TimeSpan.FromMilliseconds(1200);

    private static readonly char[] ArtistSeparators = { ',', '&', '/', '·', '•', ';' };

    private static readonly string[] FeatureMarkers =
        { " feat. ", " feat ", " ft. ", " ft ", " featuring ", " with " };

    private readonly CacheService _cache;
    private readonly PlaylistService _playlists;
    private readonly FavoritesService _favorites;
    private readonly IYtDlpService _catalogue;
    private readonly PlayerViewModel _player;

    private string _greeting = "Wavy";
    private string? _suggestionsHeading;
    private string _trendingHeading = "Trending right now";
    private string _freshHeading = "New releases";
    private bool _isLoadingSuggestions;
    private string? _seedId;
    private string? _tasteKey;

    public HomeViewModel(
        CacheService cache,
        PlaylistService playlists,
        FavoritesService favorites,
        IYtDlpService catalogue,
        PlayerViewModel player)
    {
        _cache = cache;
        _playlists = playlists;
        _favorites = favorites;
        _catalogue = catalogue;
        _player = player;

        _playlists.Playlists.CollectionChanged += (_, _) => { BuildMix(); SectionsChanged(); };
        _favorites.Albums.CollectionChanged += (_, _) => SectionsChanged();
    }

    public ObservableCollection<Track> Recent { get; } = new();

    public ObservableCollection<Track> Mix { get; } = new();

    public ObservableCollection<Track> Suggestions { get; } = new();

    public ObservableCollection<Track> Trending { get; } = new();

    public ObservableCollection<Track> Fresh { get; } = new();

    public ObservableCollection<Playlist> Playlists => _playlists.Playlists;

    public ObservableCollection<AlbumResult> Albums => _favorites.Albums;

    public string Greeting
    {
        get => _greeting;
        private set { if (_greeting == value) return; _greeting = value; OnPropertyChanged(); }
    }

    public string? SuggestionsHeading
    {
        get => _suggestionsHeading;
        private set { if (_suggestionsHeading == value) return; _suggestionsHeading = value; OnPropertyChanged(); }
    }

    public string TrendingHeading
    {
        get => _trendingHeading;
        private set { if (_trendingHeading == value) return; _trendingHeading = value; OnPropertyChanged(); }
    }

    public string FreshHeading
    {
        get => _freshHeading;
        private set { if (_freshHeading == value) return; _freshHeading = value; OnPropertyChanged(); }
    }

    public bool IsLoadingSuggestions
    {
        get => _isLoadingSuggestions;
        private set
        {
            if (_isLoadingSuggestions == value)
                return;
            _isLoadingSuggestions = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowSuggestions));
        }
    }

    public bool HasRecent => Recent.Count > 0;
    public bool HasMix => Mix.Count > 0;
    public bool HasPlaylists => Playlists.Count > 0;
    public bool HasAlbums => Albums.Count > 0;
    public bool HasSuggestions => Suggestions.Count > 0;
    public bool HasTrending => Trending.Count > 0;
    public bool HasFresh => Fresh.Count > 0;

    public bool ShowSuggestions => HasSuggestions || IsLoadingSuggestions;

    public bool IsEmpty => !HasRecent && !HasMix && !HasPlaylists && !HasAlbums;

    public void Refresh()
    {
        Greeting = GreetingFor(DateTime.Now);

        Recent.Clear();
        foreach (var track in _cache.GetCachedTracks().Take(RecentLimit))
            Recent.Add(track);

        BuildMix();
        SectionsChanged();
        _ = LoadSuggestionsAsync();
        _ = LoadCatalogueRowsAsync();
    }

    private async Task LoadCatalogueRowsAsync()
    {
        var taste = BuildTaste();
        var key = TasteKey(taste);
        if (HasTrending && HasFresh && key == _tasteKey)
            return;

        _tasteKey = key;
        TrendingHeading = taste.Count > 0 ? "Trending in your taste" : "Trending right now";
        FreshHeading = taste.Count > 0 ? "New for you" : "New releases";

        await Task.Delay(SuggestionDelay);

        var charts = await SafeAsync(() => _catalogue.GetTrendingAsync(TrendingLimit));
        var releases = await SafeAsync(() => _catalogue.GetNewReleasesAsync(FreshLimit));
        var radio = taste.Count > 0 ? await TasteRadioAsync(taste) : Array.Empty<Track>();

        Fill(Trending, Compose(charts, radio, taste, TrendingLimit), nameof(HasTrending));
        Fill(Fresh, Compose(releases, radio, taste, FreshLimit), nameof(HasFresh));
    }

    private Dictionary<string, double> BuildTaste()
    {
        var taste = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        void Add(string? artist, double weight)
        {
            foreach (var name in Names(artist))
                taste[name] = taste.TryGetValue(name, out var score) ? score + weight : weight;
        }

        var history = _cache.GetCachedTracks();
        for (var i = 0; i < history.Count; i++)
            Add(history[i].Artist, 1.0 / (1.0 + i * 0.08));

        foreach (var playlist in Playlists)
            foreach (var track in playlist.Tracks)
                Add(track.Artist, 0.6);

        foreach (var album in Albums)
            Add(album.Artist, 0.8);

        return taste;
    }

    private static string TasteKey(Dictionary<string, double> taste) =>
        string.Join('|', taste.OrderByDescending(p => p.Value).Take(6).Select(p => p.Key));

    private async Task<IReadOnlyList<Track>> TasteRadioAsync(Dictionary<string, double> taste)
    {
        var seed = _cache.GetCachedTracks().Concat(Mix)
            .Where(t => t.Id is { Length: > 0 })
            .OrderByDescending(t => Affinity(t, taste))
            .FirstOrDefault();

        return seed is null
            ? Array.Empty<Track>()
            : await SafeAsync(() => _catalogue.GetSimilarAsync(seed, SuggestionLimit));
    }

    private static List<Track> Compose(
        IReadOnlyList<Track> primary,
        IReadOnlyList<Track> extra,
        Dictionary<string, double> taste,
        int limit)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var picked = new List<Track>();

        void Take(IEnumerable<Track> source)
        {
            foreach (var track in source)
            {
                if (picked.Count >= limit)
                    return;
                if (track.Id is { Length: > 0 } && seen.Add(track.Id))
                    picked.Add(track);
            }
        }

        if (taste.Count == 0)
        {
            Take(primary);
            return picked;
        }

        var ranked = primary
            .Select((track, order) => (Track: track, Score: Affinity(track, taste), Order: order))
            .ToList();

        Take(ranked.Where(x => x.Score > 0)
                   .OrderByDescending(x => x.Score).ThenBy(x => x.Order)
                   .Select(x => x.Track));
        Take(extra.OrderByDescending(t => Affinity(t, taste)));
        Take(ranked.Where(x => x.Score <= 0).OrderBy(x => x.Order).Select(x => x.Track));

        return picked;
    }

    private static double Affinity(Track track, Dictionary<string, double> taste)
    {
        var score = 0.0;
        foreach (var name in Names(track.Artist))
            if (taste.TryGetValue(name, out var weight))
                score += weight;
        return score;
    }

    private static IEnumerable<string> Names(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return Array.Empty<string>();

        var flat = artist;
        foreach (var marker in FeatureMarkers)
            flat = flat.Replace(marker, ",", StringComparison.OrdinalIgnoreCase);

        return flat
            .Split(ArtistSeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(name => name.Trim().TrimEnd('.'))
            .Where(name => name.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<Track>> SafeAsync(Func<Task<IReadOnlyList<Track>>> fetch)
    {
        try
        {
            return await fetch();
        }
        catch
        {
            return Array.Empty<Track>();
        }
    }

    private void Fill(ObservableCollection<Track> row, IReadOnlyList<Track> tracks, string flag)
    {
        if (tracks.Count > 0)
        {
            row.Clear();
            foreach (var track in tracks)
                row.Add(track);
        }

        OnPropertyChanged(flag);
    }

    private void BuildMix()
    {
        Mix.Clear();

        var lists = Playlists.Where(p => p.Tracks.Count > 0).ToList();
        if (lists.Count == 0)
            return;

        var seen = new HashSet<string>(Recent.Select(t => t.Id), StringComparer.Ordinal);
        var depth = lists.Max(p => p.Tracks.Count);
        for (var i = 0; i < depth && Mix.Count < MixLimit; i++)
            foreach (var list in lists)
            {
                if (Mix.Count >= MixLimit)
                    break;
                if (i < list.Tracks.Count && seen.Add(list.Tracks[i].Id))
                    Mix.Add(list.Tracks[i]);
            }
    }

    public async Task PlayRecentAsync(Track track) => await PlayFromAsync(Recent, track);

    public async Task PlayMixAsync(Track track) => await PlayFromAsync(Mix, track);

    public async Task PlaySuggestionAsync(Track track) => await PlayFromAsync(Suggestions, track);

    public async Task PlayTrendingAsync(Track track) => await PlayFromAsync(Trending, track);

    public async Task PlayFreshAsync(Track track) => await PlayFromAsync(Fresh, track);

    public async Task PlayPlaylistAsync(Playlist playlist)
    {
        var tracks = playlist.Tracks.ToList();
        if (tracks.Count == 0)
            return;

        await _player.PlayFromAsync(tracks, 0, lockQueue: true);
    }

    public async Task PlayAlbumAsync(AlbumResult album)
    {
        if (string.IsNullOrWhiteSpace(album.BrowseId))
            return;

        var details = await _catalogue.GetAlbumAsync(album.BrowseId);
        var tracks = details?.Tracks.ToList();
        if (tracks is null || tracks.Count == 0)
            return;

        await _player.PlayFromAsync(tracks, 0, lockQueue: true);
    }

    private async Task PlayFromAsync(IReadOnlyList<Track> row, Track track)
    {
        var at = row.ToList().FindIndex(t => t.Id == track.Id);
        if (at < 0)
            return;

        await _player.PlayFromAsync(row.ToList(), at);
    }

    private async Task LoadSuggestionsAsync()
    {
        var seed = Recent.FirstOrDefault() ?? Mix.FirstOrDefault();
        if (seed is null)
        {
            _seedId = null;
            Suggestions.Clear();
            SuggestionsHeading = null;
            OnPropertyChanged(nameof(HasSuggestions));
            OnPropertyChanged(nameof(ShowSuggestions));
            return;
        }

        if (seed.Id == _seedId && HasSuggestions)
            return;

        _seedId = seed.Id;
        SuggestionsHeading = $"More like “{seed.Title}”";
        IsLoadingSuggestions = true;
        try
        {
            await Task.Delay(SuggestionDelay);
            var similar = await _catalogue.GetSimilarAsync(seed, SuggestionLimit);

            var seen = new HashSet<string>(
                Recent.Select(t => t.Id).Concat(Mix.Select(t => t.Id)), StringComparer.Ordinal);
            Suggestions.Clear();
            foreach (var track in similar)
                if (seen.Add(track.Id))
                    Suggestions.Add(track);
        }
        catch
        {

        }
        finally
        {
            IsLoadingSuggestions = false;
            OnPropertyChanged(nameof(HasSuggestions));
            OnPropertyChanged(nameof(ShowSuggestions));
        }
    }

    private void SectionsChanged()
    {
        OnPropertyChanged(nameof(HasRecent));
        OnPropertyChanged(nameof(HasMix));
        OnPropertyChanged(nameof(HasPlaylists));
        OnPropertyChanged(nameof(HasAlbums));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private static string GreetingFor(DateTime now) => now.Hour switch
    {
        < 5 => "Still up",
        < 12 => "Good morning",
        < 18 => "Good afternoon",
        _ => "Good evening",
    };
}
