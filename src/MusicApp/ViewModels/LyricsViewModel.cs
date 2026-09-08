using System.Collections.ObjectModel;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using MusicApp.Core.Models;
using MusicApp.Core.Services;
using MusicApp.Services;

namespace MusicApp.ViewModels;

public sealed partial class LyricsLineItem : ObservableObject
{
    public required string Text { get; init; }

    public double? StartSeconds { get; init; }

    [ObservableProperty] private bool _isActive;

    public double LineOpacity => IsActive ? 1.0 : 0.32;

    public Vector3 LineScale => IsActive ? new Vector3(1.045f, 1.045f, 1f) : Vector3.One;

    partial void OnIsActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(LineOpacity));
        OnPropertyChanged(nameof(LineScale));
    }
}

public sealed partial class LyricsViewModel : ObservableObject
{
    private readonly PlayerViewModel _player;
    private readonly SettingsService _settings;
    private readonly DispatcherQueue _dispatcher;
    private readonly LyricsClient _client = new();

    private CancellationTokenSource? _cts;
    private Track? _loadedFor;
    private Track? _awaitingPlayback;
    private int _activeIndex = -1;

    public ObservableCollection<LyricsLineItem> Lines { get; } = new();

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private string? _provider;
    [ObservableProperty] private bool _isSynced;

    public bool ShowUnsyncedNotice => Lines.Count > 0 && !IsSynced;

    partial void OnIsSyncedChanged(bool value) => OnPropertyChanged(nameof(ShowUnsyncedNotice));

    public event EventHandler<LyricsLineItem>? ActiveLineChanged;

    public event EventHandler? LinesReset;

    public LyricsViewModel(PlayerViewModel player, SettingsService settings, DispatcherQueue dispatcher)
    {
        _player = player;
        _settings = settings;
        _dispatcher = dispatcher;

        _player.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(PlayerViewModel.CurrentTrack):
                    _ = ReloadAsync();
                    break;
                case nameof(PlayerViewModel.PositionSeconds):
                    Highlight(_player.PositionSeconds);
                    break;
                case nameof(PlayerViewModel.DurationSeconds):
                    if (_awaitingPlayback is not null && _player.DurationSeconds > 0)
                    {
                        _awaitingPlayback = null;
                        _ = ReloadAsync();
                    }
                    break;
                case nameof(PlayerViewModel.IsLyricsOpen) when _player.IsLyricsOpen:
                    _ = ReloadAsync();
                    break;
            }
        };

        _settings.Changed += (_, name) =>
        {
            if (name == nameof(SettingsService.LyricsSource))
            {
                _loadedFor = null;
                _ = ReloadAsync();
            }
        };
    }

    public async Task ReloadAsync()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => _ = ReloadAsync());
            return;
        }

        if (!_player.IsLyricsOpen)
            return;

        var track = _player.CurrentTrack;
        if (track is null)
        {
            _awaitingPlayback = null;
            Reset("Play a song to see its lyrics.");
            return;
        }

        if (_player.DurationSeconds <= 0)
        {
            _loadedFor = null;
            _awaitingPlayback = track;
            Reset(null);
            IsLoading = true;
            return;
        }

        _awaitingPlayback = null;

        if (ReferenceEquals(track, _loadedFor))
            return;
        _loadedFor = track;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        Reset(null);
        IsLoading = true;
        try
        {
            var found = await _client.GetAsync(
                track.Title, track.Artist, _player.DurationSeconds, _settings.LyricsSource, token);

            if (token.IsCancellationRequested || !ReferenceEquals(track, _loadedFor))
                return;

            if (found is null || found.Lines.Count == 0)
            {
                _loadedFor = null;
                Reset("No lyrics found for this song.");
                return;
            }

            Lines.Clear();
            foreach (var line in found.Lines)
                Lines.Add(new LyricsLineItem { Text = line.Text, StartSeconds = line.StartSeconds });

            Provider = found.Provider;
            IsSynced = found.IsSynced;
            OnPropertyChanged(nameof(ShowUnsyncedNotice));
            _activeIndex = -1;
            Message = null;
            Highlight(_player.PositionSeconds);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _loadedFor = null;
            Reset($"Couldn't load lyrics: {ex.Message}");
        }
        finally
        {
            if (!token.IsCancellationRequested)
                IsLoading = false;
        }
    }

    private void Reset(string? message)
    {
        Lines.Clear();
        _activeIndex = -1;
        Provider = null;
        IsSynced = false;
        IsLoading = false;
        Message = message;
        OnPropertyChanged(nameof(ShowUnsyncedNotice));
        LinesReset?.Invoke(this, EventArgs.Empty);
    }

    private void Highlight(double seconds)
    {
        if (!IsSynced || Lines.Count == 0)
            return;

        var index = -1;
        for (var i = 0; i < Lines.Count; i++)
        {
            if (Lines[i].StartSeconds is { } start && start <= seconds + 0.15)
                index = i;
            else
                break;
        }

        if (index == _activeIndex)
            return;

        if (_activeIndex >= 0 && _activeIndex < Lines.Count)
            Lines[_activeIndex].IsActive = false;

        _activeIndex = index;
        if (index < 0 || index >= Lines.Count)
            return;

        var line = Lines[index];
        line.IsActive = true;
        ActiveLineChanged?.Invoke(this, line);
    }
}
