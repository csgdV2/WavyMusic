using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wavy.Core.Abstractions;
using Wavy.Core.Models;
using Wavy.Core.Services;
using Wavy.Services;

namespace Wavy.ViewModels;

public sealed partial class PlayerViewModel : ObservableObject
{

    private static readonly string PauseGlyph = char.ConvertFromUtf32(0xF8AE);
    private static readonly string PlayGlyph = char.ConvertFromUtf32(0xF5B0);
    private static readonly string RepeatOffGlyph = char.ConvertFromUtf32(0xF5E7);
    private static readonly string RepeatAllGlyph = char.ConvertFromUtf32(0xE8EE);
    private static readonly string RepeatOneGlyph = char.ConvertFromUtf32(0xE8ED);

    public enum RepeatMode { None, All, One }

    private readonly PlaybackService _playback;
    private readonly CacheService _cache;
    private readonly SettingsService _settings;
    private readonly IYtDlpService _catalogue;
    private readonly RadioFeedbackService? _feedback;
    private readonly Random _rng = new();
    private List<Track>? _unshuffled;
    private int _index = -1;
    private double _positionSeconds;
    private bool _isShuffled;
    private RepeatMode _repeat = RepeatMode.None;
    private bool _reordering;
    private bool _isQueueOpen;
    private bool _isLyricsOpen;
    private bool _autoplayEnabled = true;
    private Task? _extension;
    private CancellationTokenSource? _radioCts;
    private int _queueGeneration;

    private int _playGeneration;

    private CancellationTokenSource? _playCts;

    public PlayerViewModel(PlaybackService playback, CacheService cache, SettingsService settings, IYtDlpService catalogue, RadioFeedbackService? feedback = null)
    {
        _playback = playback;
        _cache = cache;
        _settings = settings;
        _catalogue = catalogue;
        _feedback = feedback;
        if (_feedback is not null) _feedback.Changed += OnRadioFeedbackChanged;
        _playback.PropertyChanged += OnPlaybackPropertyChanged;
        _playback.MediaEndedNaturally += (_, _) => _ = OnTrackEndedAsync();
        _playback.AdvancedGaplessly += (_, track) => OnAdvancedGaplessly(track);
        Queue.CollectionChanged += OnQueueChanged;

        _playback.Volume = settings.Volume;
    }

    public ObservableCollection<Track> Queue { get; } = new();

    public bool HasQueue => Queue.Count > 0;

    public int CurrentIndex => _index;

    public bool IsQueueOpen
    {
        get => _isQueueOpen;
        set
        {
            if (_isQueueOpen == value)
                return;
            _isQueueOpen = value;
            if (value)
                IsLyricsOpen = false;
            OnPropertyChanged();
        }
    }

    public bool IsLyricsOpen
    {
        get => _isLyricsOpen;
        set
        {
            if (_isLyricsOpen == value)
                return;
            _isLyricsOpen = value;
            if (value)
                IsQueueOpen = false;
            OnPropertyChanged();
        }
    }

    public bool AutoplayEnabled
    {
        get => _autoplayEnabled;
        set
        {
            if (_autoplayEnabled == value)
                return;
            _autoplayEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AutoplayIsOn));
            OnPropertyChanged(nameof(AutoplayOpacity));
        }
    }

    public bool AutoplayIsOn => AutoplayEnabled && Repeat == RepeatMode.None && !_queueLocked;

    private bool _queueLocked;

    private bool CanExtendQueue => AutoplayIsOn && !_queueLocked;

    public double AutoplayOpacity => AutoplayIsOn ? 1.0 : 0.55;

    [RelayCommand]
    private void ToggleQueue() => IsQueueOpen = !IsQueueOpen;

    [RelayCommand]
    private void ToggleLyrics() => IsLyricsOpen = !IsLyricsOpen;

    [RelayCommand]
    private void ToggleAutoplay() => AutoplayEnabled = !AutoplayEnabled;

    [RelayCommand]
    private void ClearQueue()
    {
        _reordering = true;
        try
        {
            for (var i = Queue.Count - 1; i >= 0; i--)
                if (i != _index)
                    Queue.RemoveAt(i);
            _index = Queue.Count == 0 ? -1 : Queue.IndexOf(_playback.CurrentTrack!);
        }
        finally
        {
            _reordering = false;
        }
        OnPropertyChanged(nameof(HasQueue));
        OnPropertyChanged(nameof(CurrentIndex));
    }

    public async Task PlayNextAsync(Track track)
    {
        if (_index < 0 || Queue.Count == 0)
        {
            await PlayRadioAsync(track);
            return;
        }

        Insert(Math.Min(_index + 1, Queue.Count), track);
    }

    public async Task EnqueueAsync(Track track)
    {
        if (_index < 0 || Queue.Count == 0)
        {
            await PlayRadioAsync(track);
            return;
        }

        Insert(Queue.Count, track);
    }

    public void RemoveFromQueue(Track track)
    {
        var at = Queue.IndexOf(track);
        if (at < 0)
            return;

        _reordering = true;
        try
        {
            Queue.RemoveAt(at);
            if (at < _index)
                _index--;
            else if (at == _index)
                _index = at - 1;
        }
        finally
        {
            _reordering = false;
        }

        OnPropertyChanged(nameof(HasQueue));
        OnPropertyChanged(nameof(CurrentIndex));
    }

    public async Task PlayQueuedAsync(Track track)
    {
        var at = Queue.IndexOf(track);
        if (at < 0)
            return;

        _index = at;
        OnPropertyChanged(nameof(CurrentIndex));
        await PlayCurrentAsync(skipIfCurrent: true);
    }

    private void Insert(int at, Track track)
    {
        _reordering = true;
        try
        {
            Queue.Insert(at, track);
            if (at <= _index)
                _index++;
        }
        finally
        {
            _reordering = false;
        }

        OnPropertyChanged(nameof(HasQueue));
        OnPropertyChanged(nameof(CurrentIndex));
    }

    private void OnQueueChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasQueue));

        if (_reordering)
            return;

        if (_playback.CurrentTrack is { } current)
        {
            var at = Queue.IndexOf(current);
            if (at >= 0)
                _index = at;
        }
        OnPropertyChanged(nameof(CurrentIndex));
        PrefetchNext();
    }

    public Track? CurrentTrack => _playback.CurrentTrack;
    public bool HasTrack => _playback.CurrentTrack is not null;
    public bool IsPlaying => _playback.IsPlaying;
    public bool IsLoading => _playback.IsBusy || _playback.IsBuffering;

    public string? StatusMessage => _playback.StatusMessage;
    public bool HasStatusMessage => !string.IsNullOrEmpty(_playback.StatusMessage);

    public string PlayPauseGlyph => IsPlaying ? PauseGlyph : PlayGlyph;

    public bool IsShuffled
    {
        get => _isShuffled;
        private set { _isShuffled = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShuffleOpacity)); }
    }

    public void SetShuffle(bool on)
    {
        if (_isShuffled == on)
            return;

        IsShuffled = on;
        ReorderForShuffle();
    }

    private void ReorderForShuffle()
    {
        if (Queue.Count == 0)
        {
            _unshuffled = null;
            return;
        }

        var current = _index >= 0 && _index < Queue.Count ? Queue[_index] : null;
        List<Track> order;

        if (IsShuffled)
        {
            _unshuffled = Queue.ToList();

            order = Queue.Where(t => !ReferenceEquals(t, current)).ToList();
            for (var i = order.Count - 1; i > 0; i--)
            {
                var j = _rng.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }
            if (current is not null)
                order.Insert(0, current);
        }
        else
        {
            var live = new HashSet<Track>(Queue);
            order = _unshuffled?.Where(live.Contains).ToList() ?? new List<Track>();
            foreach (var track in Queue)
                if (!order.Contains(track))
                    order.Add(track);
            _unshuffled = null;
        }

        _reordering = true;
        try
        {
            Queue.Clear();
            foreach (var track in order)
                Queue.Add(track);
            _index = current is null ? -1 : Queue.IndexOf(current);
        }
        finally
        {
            _reordering = false;
        }

        OnPropertyChanged(nameof(HasQueue));
        OnPropertyChanged(nameof(CurrentIndex));
    }

    public RepeatMode Repeat
    {
        get => _repeat;
        private set
        {
            _repeat = value;

            _playback.IsLooping = value == RepeatMode.One;
            PrefetchNext();
            OnPropertyChanged(); OnPropertyChanged(nameof(RepeatGlyph)); OnPropertyChanged(nameof(RepeatOpacity));
            OnPropertyChanged(nameof(RepeatIsOn));
            OnPropertyChanged(nameof(AutoplayIsOn));
            OnPropertyChanged(nameof(AutoplayOpacity));
        }
    }

    public double ShuffleOpacity => IsShuffled ? 1.0 : 0.55;

    public bool RepeatIsOn => Repeat != RepeatMode.None;

    public string RepeatGlyph => Repeat switch
    {
        RepeatMode.All => RepeatAllGlyph,
        RepeatMode.One => RepeatOneGlyph,
        _ => RepeatOffGlyph,
    };

    public double RepeatOpacity => RepeatIsOn ? 1.0 : 0.55;
    public string PositionText => Format(_playback.Position);
    public string DurationText => Format(_playback.Duration);

    public string RemainingText
    {
        get
        {
            var left = _playback.Duration - _playback.Position;
            return "-" + Format(left < TimeSpan.Zero ? TimeSpan.Zero : left);
        }
    }

    public double DurationSeconds => _playback.Duration.TotalSeconds;

    public double PositionSeconds
    {
        get => _positionSeconds;
        set
        {
            if (Math.Abs(value - _positionSeconds) < 0.001)
                return;
            _positionSeconds = value;
            OnPropertyChanged();
            if (Math.Abs(value - _playback.Position.TotalSeconds) > 1.0)
                _playback.Seek(TimeSpan.FromSeconds(value));
        }
    }

    public double Volume
    {
        get => _playback.Volume;
        set
        {
            _playback.Volume = value;

            _settings.Volume = _playback.Volume;
            OnPropertyChanged();
        }
    }

    public async Task PlayRadioAsync(Track track)
    {
        var playback = PlayFromAsync(new[] { track }, 0, lockQueue: false);
        var generation = _queueGeneration;
        await playback;
        if (generation == _queueGeneration) await (_extension ?? ExtendWithSimilarAsync());
    }

    public async Task PlayFromAsync(IReadOnlyList<Track> tracks, int startIndex, bool lockQueue = true)
    {
        _queueGeneration++;
        _radioCts?.Cancel();
        _radioCts?.Dispose();
        _radioCts = new CancellationTokenSource();
        _extension = null;
        _queueLocked = lockQueue;
        _playback.ClearPreparedNext();
        OnPropertyChanged(nameof(AutoplayIsOn));
        OnPropertyChanged(nameof(AutoplayOpacity));
        _reordering = true;
        try
        {
            Queue.Clear();
            foreach (var track in tracks)
                Queue.Add(track);
            _index = startIndex;
        }
        finally
        {
            _reordering = false;
        }

        _unshuffled = null;
        if (IsShuffled)
            ReorderForShuffle();

        OnPropertyChanged(nameof(HasQueue));
        OnPropertyChanged(nameof(CurrentIndex));
        await PlayCurrentAsync(skipIfCurrent: true);
    }

    public async Task PlayShuffledAsync(IReadOnlyList<Track> tracks, bool lockQueue = true)
    {
        if (tracks.Count == 0)
            return;

        IsShuffled = true;
        _unshuffled = null;
        await PlayFromAsync(tracks, _rng.Next(tracks.Count), lockQueue);
    }

    public void SeekTo(double seconds)
    {
        _positionSeconds = seconds;
        OnPropertyChanged(nameof(PositionSeconds));
        _playback.Seek(TimeSpan.FromSeconds(seconds));
    }

    public void SeekBy(double seconds)
    {
        if (DurationSeconds <= 0)
            return;
        SeekTo(Math.Clamp(PositionSeconds + seconds, 0, DurationSeconds));
    }

    public void AdjustVolume(double delta) => Volume = Math.Clamp(Volume + delta, 0, 1);

    [RelayCommand]
    private void PlayPause() => _playback.TogglePlayPause();

    [RelayCommand]
    private void ToggleShuffle() => SetShuffle(!IsShuffled);

    [RelayCommand]
    private void CycleRepeat() =>
        Repeat = Repeat switch
        {
            RepeatMode.None => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.None,
        };

    public void ClearStatus() => _playback.ClearStatus();

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task NextAsync() => await NextAsync(wrapAtEnd: false);

    private async Task NextAsync(bool wrapAtEnd)
    {
        if (Queue.Count == 0)
            return;

        var generation = _queueGeneration;
        if (_index + 1 >= Queue.Count && CanExtendQueue)
            await ExtendWithSimilarAsync();
        if (generation != _queueGeneration) return;
        var next = _index + 1;
        if (next >= Queue.Count)
        {
            if (!wrapAtEnd)
                return;
            next = 0;
        }

        _index = next;
        OnPropertyChanged(nameof(CurrentIndex));
        await PlayCurrentAsync();
    }

    private async Task OnTrackEndedAsync()
    {

        if (Repeat == RepeatMode.One)
        {
            _playback.Restart();
            return;
        }

        await NextAsync(wrapAtEnd: Repeat == RepeatMode.All);
    }

    private Task ExtendWithSimilarAsync()
    {
        if (!CanExtendQueue || _index < 0 || _index >= Queue.Count) return Task.CompletedTask;
        if (_extension is { IsCompleted: false }) return _extension;
        return _extension = LoadSimilarAsync(Queue[_index], _queueGeneration, _radioCts?.Token ?? CancellationToken.None);
    }

    private async Task LoadSimilarAsync(Track seed, int generation, CancellationToken token)
    {
        try
        {
            var seeds = _feedback?.RadioSeeds(seed) ?? new[] { seed };
            var rows = await Task.WhenAll(seeds.Select(t => _catalogue.GetSimilarAsync(t, 12, token)));
            var similar = _feedback is null ? rows.SelectMany(t => t)
                : RecommendationSelector.Pick(rows.Select(row => row.Where(t => !_feedback.IsExcluded(t))), Queue, 12);
            if (token.IsCancellationRequested || generation != _queueGeneration || !CanExtendQueue) return;
            var known = new HashSet<string>(Queue.Select(t => t.Id), StringComparer.Ordinal);
            _reordering = true;
            try
            {
                foreach (var track in similar)
                    if (_feedback?.IsExcluded(track) != true && known.Add(track.Id)) Queue.Add(track);
            }
            finally { _reordering = false; }
            OnPropertyChanged(nameof(HasQueue));
            PrefetchNext();
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void OnRadioFeedbackChanged(object? sender, EventArgs args)
    {
        if (_queueLocked) return;
        _reordering = true;
        try
        {
            for (var i = Queue.Count - 1; i > _index; i--)
                if (_feedback?.IsExcluded(Queue[i]) == true) Queue.RemoveAt(i);
        }
        finally { _reordering = false; }
        _playback.ClearPreparedNext();
        OnPropertyChanged(nameof(HasQueue));
        PrefetchNext();
        if (CanExtendQueue && _index >= Queue.Count - 3) _ = ExtendWithSimilarAsync();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task PreviousAsync()
    {

        if (_index <= 0)
        {
            _playback.Seek(TimeSpan.Zero);
            return;
        }

        _index--;
        OnPropertyChanged(nameof(CurrentIndex));
        await PlayCurrentAsync();
    }

    private async Task PlayCurrentAsync(bool skipIfCurrent = false)
    {
        if (_index < 0 || _index >= Queue.Count)
            return;

        var track = Queue[_index];

        if (skipIfCurrent && _playback.IsPlaying && _playback.CurrentTrack?.Id == track.Id)
        {
            PrefetchNext();
            if (CanExtendQueue && _index >= Queue.Count - 3) _ = ExtendWithSimilarAsync();
            return;
        }

        var localPath = _cache.TryGetLocalPath(track.Id);

        var generation = ++_playGeneration;
        _playCts?.Cancel();
        _playCts = new CancellationTokenSource();
        var token = _playCts.Token;

        await _playback.PlayAsync(track, localPath, token);

        if (generation != _playGeneration)
            return;

        _cache.MarkPlayed(track);
        if (localPath is null)
            _ = _cache.EnsureCachedAsync(track, token);

        PrefetchNext();

        if (CanExtendQueue && _index >= Queue.Count - 3)
            _ = ExtendWithSimilarAsync();
    }

    private void PrefetchNext()
    {
        if (Repeat == RepeatMode.One || !_settings.PrefetchNext)
        {
            _playback.ClearPreparedNext();
            return;
        }

        var next = _index + 1;
        if (next >= Queue.Count)
        {
            if (Repeat != RepeatMode.All || Queue.Count == 0)
            {
                _playback.ClearPreparedNext();
                return;
            }
            next = 0;
        }
        if (next == _index)
        {
            _playback.ClearPreparedNext();
            return;
        }

        _ = _playback.PrepareNextAsync(Queue[next]);
    }

    private void OnAdvancedGaplessly(Track track)
    {
        var next = _index + 1;
        if (next >= Queue.Count)
            next = Repeat == RepeatMode.All && Queue.Count > 0 ? 0 : -1;

        if (next < 0 || Queue[next].Id != track.Id)
            next = Queue.IndexOf(track);

        if (next < 0)
            return;

        _index = next;
        OnPropertyChanged(nameof(CurrentIndex));

        _cache.MarkPlayed(track);
        PrefetchNext();

        if (CanExtendQueue && _index >= Queue.Count - 3)
            _ = ExtendWithSimilarAsync();
    }

    private void OnPlaybackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlaybackService.CurrentTrack):
                OnPropertyChanged(nameof(CurrentTrack));
                OnPropertyChanged(nameof(HasTrack));
                break;
            case nameof(PlaybackService.IsPlaying):
                OnPropertyChanged(nameof(IsPlaying));
                OnPropertyChanged(nameof(PlayPauseGlyph));
                break;
            case nameof(PlaybackService.IsBusy):
            case nameof(PlaybackService.IsBuffering):
                OnPropertyChanged(nameof(IsLoading));
                break;
            case nameof(PlaybackService.StatusMessage):
                OnPropertyChanged(nameof(StatusMessage));
                OnPropertyChanged(nameof(HasStatusMessage));
                break;
            case nameof(PlaybackService.Position):
                _positionSeconds = _playback.Position.TotalSeconds;
                OnPropertyChanged(nameof(PositionSeconds));
                OnPropertyChanged(nameof(PositionText));
                OnPropertyChanged(nameof(RemainingText));
                break;
            case nameof(PlaybackService.Duration):
                OnPropertyChanged(nameof(DurationSeconds));
                OnPropertyChanged(nameof(DurationText));
                OnPropertyChanged(nameof(RemainingText));
                break;
        }
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
}
