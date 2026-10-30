using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Wavy.Core.Abstractions;
using Wavy.Core.Models;
using Wavy.Core.Services;
using Wavy.Services;

namespace Wavy.ViewModels;

public sealed class HomeViewModel : ObservableObject
{
    private readonly CacheService _cache;
    private readonly PlaylistService _playlists;
    private readonly FavoritesService _favorites;
    private readonly IYtDlpService _catalogue;
    private readonly PlayerViewModel _player;
    private readonly RadioFeedbackService? _feedback;
    private CancellationTokenSource? _load;
    private string? _loadedKey;
    private DateTimeOffset _loadedAt;
    private string _greeting = "Welcome back";
    private bool _isLoadingSuggestions;
    private string _reason = "A fresh mix from your listening.";
    private string? _message;

    public HomeViewModel(CacheService cache, PlaylistService playlists, FavoritesService favorites,
        IYtDlpService catalogue, PlayerViewModel player, RadioFeedbackService? feedback = null)
    {
        _cache = cache;
        _playlists = playlists;
        _favorites = favorites;
        _catalogue = catalogue;
        _player = player;
        _feedback = feedback;
        if (_feedback is not null) _feedback.Changed += (_, _) =>
        {
            RemoveExcluded(Suggestions, nameof(HasSuggestions));
            RemoveExcluded(Trending, nameof(HasTrending));
            RemoveExcluded(Fresh, nameof(HasFresh));
            _ = RefreshRecommendationsAsync(force: true);
        };
        _playlists.Playlists.CollectionChanged += (_, _) => { BuildMix(); SectionsChanged(); };
        _playlists.PlaylistChanged += (_, _) => { BuildMix(); SectionsChanged(); };
        _favorites.Albums.CollectionChanged += (_, _) => SectionsChanged();
    }

    public ObservableCollection<Track> Recent { get; } = new();
    public ObservableCollection<Track> Mix { get; } = new();
    public ObservableCollection<Track> Suggestions { get; } = new();
    public ObservableCollection<Track> Trending { get; } = new();
    public ObservableCollection<Track> Fresh { get; } = new();
    public ObservableCollection<Playlist> Playlists => _playlists.Playlists;
    public ObservableCollection<AlbumResult> Albums => _favorites.Albums;
    public string Greeting { get => _greeting; private set => SetProperty(ref _greeting, value); }
    public string SuggestionsHeading => "Picked for your rotation";
    public string TrendingHeading => "Popular right now";
    public string FreshHeading => "New releases";
    public string RecommendationReason { get => _reason; private set => SetProperty(ref _reason, value); }
    public string? RecommendationMessage { get => _message; private set => SetProperty(ref _message, value); }
    public bool IsLoadingSuggestions
    {
        get => _isLoadingSuggestions;
        private set
        {
            SetProperty(ref _isLoadingSuggestions, value);
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
    public bool ShowSuggestions => HasSuggestions || IsLoadingSuggestions || RecommendationMessage is not null;
    public bool IsEmpty => !HasRecent && !HasMix && !HasPlaylists && !HasAlbums;

    public void Refresh()
    {
        Greeting = DateTime.Now.Hour switch
        {
            < 5 => "Still up?",
            < 12 => "Good morning",
            < 18 => "Good afternoon",
            _ => "Good evening",
        };
        Recent.Clear();
        foreach (var track in _cache.GetCachedTracks().Take(6)) Recent.Add(track);
        BuildMix();
        SectionsChanged();
        _ = RefreshRecommendationsAsync();
    }

    public async Task RefreshRecommendationsAsync(bool force = false)
    {
        var history = _cache.GetCachedTracks();
        var seeds = _feedback?.Seeds(history.Take(30).Concat(Mix), 3)
            ?? RecommendationSelector.Seeds(history.Take(30).Concat(Mix), 3);
        var key = string.Join("|", seeds.Select(t => t.Id)) + ":" + _feedback?.Revision;
        if (!force && key == _loadedKey && HasSuggestions
            && DateTimeOffset.UtcNow - _loadedAt < TimeSpan.FromMinutes(30)) return;

        _load?.Cancel();
        var load = new CancellationTokenSource();
        _load = load;
        var ct = load.Token;
        RecommendationReason = seeds.Count > 0
            ? "Inspired by " + string.Join(", ", seeds.Select(t => t.Artist).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct())
            : "Play a few favorites to shape your recommendations.";
        IsLoadingSuggestions = true;
        RecommendationMessage = null;
        try
        {
            await Task.Delay(200, ct);
            var radios = seeds.Select(seed => SafeFetchAsync(() => _catalogue.GetSimilarAsync(seed, 24, ct), ct)).ToArray();
            var chartsTask = SafeFetchAsync(() => _catalogue.GetTrendingAsync(30, ct), ct);
            var freshTask = SafeFetchAsync(() => _catalogue.GetNewReleasesAsync(30, ct), ct);
            await Task.WhenAll(radios.Append(chartsTask).Append(freshTask));
            ct.ThrowIfCancellationRequested();

            var excluded = history.Take(20).Concat(Mix).ToList();
            var radioRows = radios.Select(t => Allowed(t.Result)).ToList();
            if (radioRows.Count == 0) radioRows.Add(Allowed(chartsTask.Result));
            var picks = RecommendationSelector.Pick(radioRows, excluded, 10);
            Fill(Suggestions, picks, nameof(HasSuggestions));
            var trending = RecommendationSelector.Pick(new[] { Allowed(chartsTask.Result) }, excluded.Concat(picks), 8);
            Fill(Trending, trending, nameof(HasTrending));
            Fill(Fresh, RecommendationSelector.Pick(new[] { Allowed(freshTask.Result) },
                excluded.Concat(picks).Concat(trending), 8), nameof(HasFresh));
            if (picks.Count == 0 && trending.Count == 0 && Fresh.Count == 0)
                RecommendationMessage = "No new picks right now. Try refreshing in a moment.";
            else
            {
                _loadedKey = key;
                _loadedAt = DateTimeOffset.UtcNow;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_load, load))
            {
                IsLoadingSuggestions = false;
                _load = null;
            }
            load.Dispose();
            OnPropertyChanged(nameof(ShowSuggestions));
        }
    }

    private IEnumerable<Track> Allowed(IEnumerable<Track> tracks) => tracks.Where(t => _feedback?.IsExcluded(t) != true);

    private void RemoveExcluded(ObservableCollection<Track> tracks, string flag)
    {
        foreach (var track in tracks.Where(t => _feedback?.IsExcluded(t) == true).ToList()) tracks.Remove(track);
        OnPropertyChanged(flag);
    }

    private static async Task<IReadOnlyList<Track>> SafeFetchAsync(Func<Task<IReadOnlyList<Track>>> fetch, CancellationToken ct)
    {
        try { return await fetch(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return Array.Empty<Track>(); }
    }

    private void Fill(ObservableCollection<Track> row, IReadOnlyList<Track> tracks, string flag)
    {
        row.Clear();
        foreach (var track in tracks) row.Add(track);
        OnPropertyChanged(flag);
    }

    private void BuildMix()
    {
        Mix.Clear();
        var lists = Playlists.Where(p => p.Tracks.Count > 0).ToList();
        var seen = new HashSet<string>(Recent.Select(t => t.Id), StringComparer.Ordinal);
        for (var depth = 0; depth < lists.Select(p => p.Tracks.Count).DefaultIfEmpty().Max() && Mix.Count < 8; depth++)
            foreach (var list in lists)
                if (Mix.Count < 8 && depth < list.Tracks.Count && seen.Add(list.Tracks[depth].Id))
                    Mix.Add(list.Tracks[depth]);
    }

    public Task PlayRecentAsync(Track track) => _player.PlayRadioAsync(track);
    public Task PlayMixAsync(Track track) => _player.PlayRadioAsync(track);
    public Task PlaySuggestionAsync(Track track) => _player.PlayRadioAsync(track);
    public Task PlayTrendingAsync(Track track) => _player.PlayRadioAsync(track);
    public Task PlayFreshAsync(Track track) => _player.PlayRadioAsync(track);

    public async Task PlayPlaylistAsync(Playlist playlist)
    {
        var tracks = playlist.Tracks.ToList();
        if (tracks.Count > 0) await _player.PlayFromAsync(tracks, 0, lockQueue: true);
    }

    public async Task PlayAlbumAsync(AlbumResult album)
    {
        if (string.IsNullOrWhiteSpace(album.BrowseId)) return;
        var details = await _catalogue.GetAlbumAsync(album.BrowseId);
        var tracks = details?.Tracks.ToList();
        if (tracks is { Count: > 0 }) await _player.PlayFromAsync(tracks, 0, lockQueue: true);
    }

    private void SectionsChanged()
    {
        foreach (var name in new[] { nameof(HasRecent), nameof(HasMix), nameof(HasPlaylists), nameof(HasAlbums), nameof(IsEmpty) })
            OnPropertyChanged(name);
    }
}
