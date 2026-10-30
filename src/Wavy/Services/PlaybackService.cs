using System.IO;
using System.Diagnostics;
using Wavy.Core.Services;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Wavy.Core.Abstractions;
using Wavy.Core.Models;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Windows.Storage.Streams;

namespace Wavy.Services;

public sealed partial class PlaybackService : ObservableObject, IDisposable
{
    private static readonly TimeSpan HandoffLead = TimeSpan.FromMilliseconds(45);
    private static readonly TimeSpan HandoffWatch = TimeSpan.FromSeconds(2);

    private readonly IYtDlpService _ytDlp;
    private readonly CacheService _cache;
    private readonly SettingsService _settings;
    private readonly LoudnessNormalizationService _normalizer;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _handoff;
    private readonly DispatcherQueueTimer _listeningTimer;
    private readonly ListeningHistoryService? _history;
    private long _lastListeningTick = Stopwatch.GetTimestamp();

    private MediaPlayer _player;
    private MediaPlayer _standby;

    private CancellationTokenSource? _resolveCts;

    private int _generation;
    private MediaSource? _source;

    private MediaSource? _standbySource;
    private Track? _preparedTrack;
    private bool _preparedIsLocal;
    private string? _preparingId;

    private string? _streamingTrackId;
    private string? _recoveredTrackId;

    private bool _sawProgress;
    private (int Generation, TimeSpan Position, bool Playing)? _resume;

    public async Task ApplyAudioSettingsAsync()
    {
        if (CurrentTrack is not { } track) return;
        ClearPreparedNext();
        _resume = (_generation + 1, Position, IsPlaying);
        await PlayAsync(track, _cache.TryGetLocalPath(track.Id));
    }

    [ObservableProperty] private Track? _currentTrack;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isBuffering;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private TimeSpan _position;
    [ObservableProperty] private TimeSpan _duration;
    [ObservableProperty] private string? _statusMessage;

    public PlaybackService(IYtDlpService ytDlp, CacheService cache, SettingsService settings,
        LoudnessNormalizationService normalizer, DispatcherQueue dispatcher, ListeningHistoryService? history = null)
    {
        _ytDlp = ytDlp;
        _cache = cache;
        _settings = settings;
        _normalizer = normalizer;
        _dispatcher = dispatcher;
        _history = history;

        _player = CreatePlayer();
        _standby = CreatePlayer();

        _handoff = dispatcher.CreateTimer();
        _handoff.Interval = TimeSpan.FromMilliseconds(20);
        _handoff.IsRepeating = true;
        _handoff.Tick += (_, _) => TryHandoff();
        _settings.Changed += OnAudioSettingChanged;
        _listeningTimer = dispatcher.CreateTimer();
        _listeningTimer.Interval = TimeSpan.FromSeconds(1);
        _listeningTimer.Tick += (_, _) => TrackListening();
        _listeningTimer.Start();
    }

    private void TrackListening()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_lastListeningTick, now);
        _lastListeningTick = now;
        _history?.Advance(CurrentTrack, _source is not null && _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing,
            elapsed, DateTimeOffset.Now);
    }

    public event EventHandler? MediaEndedNaturally;

    private void OnAudioSettingChanged(object? sender, string name)
    {
        if (name is nameof(SettingsService.AudioNormalization) or nameof(SettingsService.EqualizerBands) or nameof(SettingsService.AudioQuality))
            ClearPreparedNext();
    }

    public event EventHandler<Track>? AdvancedGaplessly;

    private MediaPlayer CreatePlayer()
    {
        var player = new MediaPlayer
        {
            AudioCategory = MediaPlayerAudioCategory.Media,
            AutoPlay = false,
        };

        player.CommandManager.IsEnabled = false;
        player.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
        player.PlaybackSession.PositionChanged += OnPositionChanged;
        player.MediaOpened += OnMediaOpened;
        player.MediaFailed += OnMediaFailed;
        player.MediaEnded += OnMediaEnded;
        return player;
    }

    public double Volume
    {
        get => _player.Volume;
        set
        {
            var volume = Math.Clamp(value, 0.0, 1.0);
            _player.Volume = volume;
            _standby.Volume = volume;
        }
    }

    public bool IsLooping
    {
        get => _player.IsLoopingEnabled;
        set
        {
            _player.IsLoopingEnabled = value;
            _standby.IsLoopingEnabled = value;
        }
    }

    public void Restart() =>
        RunOnUi(() =>
        {
            if (_player.PlaybackSession.CanSeek)
                _player.PlaybackSession.Position = TimeSpan.Zero;
            _player.Play();
        });

    public async Task PlayAsync(Track track, string? localPath = null, CancellationToken ct = default)
    {
        if (Promote(track, notify: false))
            return;

        _resolveCts?.Cancel();
        _resolveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _resolveCts.Token;
        var generation = ++_generation;

        TrackListening();
        CurrentTrack = track;
        Duration = TimeSpan.Zero;
        Position = TimeSpan.Zero;
        StatusMessage = null;
        _streamingTrackId = null;
        _sawProgress = false;

        RunOnUi(() => { ClearPrepared(); ClearSource(); TrackListening(); });

        if (!string.IsNullOrEmpty(localPath) && File.Exists(localPath))
        {
            if (_normalizer.IsEnabled)
            {
                IsBusy = true;
                try
                {
                    var normalized = await _normalizer.CreateSourceAsync(localPath, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    Play(generation, normalized);
                    return;
                }
                catch (OperationCanceledException) { return; }
                catch {   }
                finally { RunOnUi(() => { if (generation == _generation) IsBusy = false; }); }
            }
            Play(generation, MediaSource.CreateFromUri(new Uri(localPath)));
            return;
        }

        IsBusy = true;
        try
        {
            var stream = await _ytDlp.ResolveStreamAsync(track, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            if (!_normalizer.IsEnabled)
            {
                var immediate = await CreateSourceAsync(stream).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                _streamingTrackId = track.Id;
                Play(generation, immediate);
                return;
            }

            if (stream.DashManifest is { Length: > 0 })
            {
                if (await _cache.FetchAsync(track, stream.Url, token).ConfigureAwait(false) is null)
                    await _cache.EnsureCachedFromSourceAsync(track, token).ConfigureAwait(false);
                if (_cache.TryGetLocalPath(track.Id) is { } downloaded)
                {
                    try
                    {
                        var normalized = await _normalizer.CreateSourceAsync(downloaded, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        Play(generation, normalized);
                        return;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch {   }
                }
            }

            if (stream.DashManifest is null or { Length: 0 }
                && await _cache.FetchAsync(track, stream.Url, token).ConfigureAwait(false) is { } file)
            {
                token.ThrowIfCancellationRequested();
                if (_normalizer.IsEnabled)
                {
                    try
                    {
                        var normalized = await _normalizer.CreateSourceAsync(file, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        Play(generation, normalized);
                        return;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch {   }
                }
                Play(generation, MediaSource.CreateFromUri(new Uri(file)));
                return;
            }

            var source = await CreateSourceAsync(stream).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _streamingTrackId = track.Id;
            Play(generation, source);
        }
        catch (OperationCanceledException)
        {

        }
        catch (Exception ex)
        {
            RunOnUi(() => StatusMessage = $"Couldn't play “{track.Title}”: {ex.Message}");
        }
        finally
        {

            RunOnUi(() => { if (generation == _generation) IsBusy = false; });
        }
    }

    private static async Task<MediaSource> CreateSourceAsync(ResolvedStream stream)
    {
        if (stream.DashManifest is { Length: > 0 } manifest)
        {
            try
            {
                var buffer = new InMemoryRandomAccessStream();
                await buffer.WriteAsync(Encoding.UTF8.GetBytes(manifest).AsBuffer());
                buffer.Seek(0);

                var result = await AdaptiveMediaSource.CreateFromStreamAsync(
                    buffer, new Uri(stream.Url), "application/dash+xml");
                if (result.Status == AdaptiveMediaSourceCreationStatus.Success)
                    return MediaSource.CreateFromAdaptiveMediaSource(result.MediaSource);
            }
            catch
            {

            }
        }

        return MediaSource.CreateFromUri(new Uri(stream.Url));
    }

    private void Play(int generation, MediaSource source) =>
        RunOnUi(() =>
        {
            if (generation != _generation)
            {
                source.Dispose();
                return;
            }

            ClearSource();
            _sawProgress = false;
            _source = source;
            _player.Source = source;
            _player.Play();
        });

    private void ClearSource()
    {
        _handoff.Stop();

        try
        {
            _player.Pause();
            _player.Source = null;
        }
        catch
        {
        }

        var previous = _source;
        _source = null;
        DisposeLater(previous);
    }

    public void Stop()
    {
        _generation++;
        RunOnUi(() => { ClearPrepared(); ClearSource(); TrackListening(); });
    }

    public void TogglePlayPause()
    {

        if (_player.Source is null)
            return;

        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
            _player.Pause();
        else
            _player.Play();
    }

    public async Task PrepareNextAsync(Track track, CancellationToken ct = default)
    {
        if (_preparedTrack?.Id == track.Id || CurrentTrack?.Id == track.Id || _preparingId == track.Id)
            return;

        _preparingId = track.Id;
        try
        {
            var local = _cache.TryGetLocalPath(track.Id);
            if (local is null)
            {
                var stream = await _ytDlp.ResolveStreamAsync(track, ct).ConfigureAwait(false);
                if (_normalizer.IsEnabled && stream.DashManifest is { Length: > 0 })
                {
                    local = await _cache.FetchAsync(track, stream.Url, ct).ConfigureAwait(false);
                    if (local is null)
                    {
                        await _cache.EnsureCachedFromSourceAsync(track, ct).ConfigureAwait(false);
                        local = _cache.TryGetLocalPath(track.Id);
                    }
                    if (local is null)
                    {
                        Arm(track, await CreateSourceAsync(stream).ConfigureAwait(false), local: false);
                        return;
                    }
                }
                else
                {
                    local = await _cache.FetchAsync(track, stream.Url, ct).ConfigureAwait(false);
                }

                if (local is null)
                {
                    Arm(track, await CreateSourceAsync(stream).ConfigureAwait(false), local: false);
                    return;
                }
            }

            MediaSource prepared;
            try
            {
                prepared = _normalizer.IsEnabled && File.Exists(local)
                    ? await _normalizer.CreateSourceAsync(local, ct).ConfigureAwait(false)
                    : MediaSource.CreateFromUri(new Uri(local));
            }
            catch (OperationCanceledException) { throw; }
            catch { prepared = MediaSource.CreateFromUri(new Uri(local)); }
            Arm(track, prepared, local: true);
        }
        catch
        {

        }
        finally
        {
            if (_preparingId == track.Id)
                _preparingId = null;
        }
    }

    public void ClearPreparedNext() => RunOnUi(ClearPrepared);

    private void Arm(Track track, MediaSource source, bool local) =>
        RunOnUi(() =>
        {
            if (_preparedTrack?.Id == track.Id || CurrentTrack?.Id == track.Id)
            {
                DisposeLater(source);
                return;
            }

            ClearPrepared();

            _preparedTrack = track;
            _preparedIsLocal = local;
            _standbySource = source;
            _standby.Volume = _player.Volume;
            _standby.IsLoopingEnabled = false;
            _standby.Source = source;
        });

    private void ClearPrepared()
    {
        _handoff.Stop();
        _preparedTrack = null;
        _preparedIsLocal = false;

        var pending = _standbySource;
        _standbySource = null;
        if (pending is null)
            return;

        try
        {
            _standby.Pause();
            _standby.Source = null;
        }
        catch
        {
        }

        DisposeLater(pending);
    }

    private bool Promote(Track? expected, bool notify)
    {
        if (_preparedTrack is not { } track || _standbySource is not { } source)
            return false;

        if (expected is not null && expected.Id != track.Id)
            return false;

        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => Promote(expected, notify));
            return true;
        }

        _handoff.Stop();
        _resolveCts?.Cancel();
        _generation++;

        var previousSource = _source;
        var incoming = _standby;
        _standby = _player;
        _player = incoming;

        _source = source;
        _standbySource = null;
        _preparedTrack = null;

        try
        {
            _standby.Pause();
            _standby.Source = null;
        }
        catch
        {
        }

        DisposeLater(previousSource);

        TrackListening();
        CurrentTrack = track;
        Position = TimeSpan.Zero;
        Duration = _player.PlaybackSession.NaturalDuration;
        StatusMessage = null;
        IsBusy = false;
        _sawProgress = false;
        _streamingTrackId = _preparedIsLocal ? null : track.Id;
        _preparedIsLocal = false;

        _player.Play();

        if (notify)
            AdvancedGaplessly?.Invoke(this, track);

        return true;
    }

    private void TryHandoff()
    {
        if (_standbySource is null || _preparedTrack is null)
        {
            _handoff.Stop();
            return;
        }

        var session = _player.PlaybackSession;
        if (session.PlaybackState != MediaPlaybackState.Playing)
            return;

        var total = session.NaturalDuration;
        if (total <= TimeSpan.Zero || total - session.Position > HandoffLead)
            return;

        Promote(null, notify: true);
    }

    private static void DisposeLater(MediaSource? source)
    {
        if (source is null)
            return;

        _ = Task.Run(() =>
        {
            try
            {
                source.Dispose();
            }
            catch
            {
            }
        });
    }

    public void Seek(TimeSpan position)
    {
        if (!_player.PlaybackSession.CanSeek)
            return;

        _sawProgress = true;
        _player.PlaybackSession.Position = position;
    }

    public void ClearStatus() => StatusMessage = null;

    private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        if (sender != _player.PlaybackSession)
            return;

        RunOnUi(() =>
        {
            TrackListening();
            IsPlaying = sender.PlaybackState == MediaPlaybackState.Playing;
            IsBuffering = sender.PlaybackState is MediaPlaybackState.Buffering or MediaPlaybackState.Opening;
        });
    }

    private void OnPositionChanged(MediaPlaybackSession sender, object args)
    {
        if (sender != _player.PlaybackSession)
            return;

        if (sender.Position > TimeSpan.FromSeconds(1))
            _sawProgress = true;

        var total = sender.NaturalDuration;
        var armed = _preparedIsLocal
                    && _standbySource is not null
                    && total > TimeSpan.Zero
                    && total - sender.Position <= HandoffWatch;

        RunOnUi(() =>
        {
            Position = sender.Position;
            if (armed && !_handoff.IsRunning)
                _handoff.Start();
        });
    }

    private void OnMediaOpened(MediaPlayer sender, object args)
    {
        if (sender != _player)
            return;

        RunOnUi(() =>
        {
            Duration = sender.PlaybackSession.NaturalDuration;
            if (_resume is { } resume && resume.Generation == _generation)
            {
                _resume = null;
                if (sender.PlaybackSession.CanSeek) sender.PlaybackSession.Position = resume.Position < Duration ? resume.Position : TimeSpan.Zero;
                if (!resume.Playing) sender.Pause();
            }
        });
    }

    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        if (sender != _player || _source is null)
            return;

        if (!_sawProgress)
        {
            RunOnUi(() => RecoverOrReport("this track wouldn't stream"));
            return;
        }

        RunOnUi(() => { TrackListening(); _history?.EndSession(); MediaEndedNaturally?.Invoke(this, EventArgs.Empty); });
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        if (sender != _player)
        {
            RunOnUi(ClearPrepared);
            return;
        }

        if (_source is null)
            return;

        RunOnUi(() => RecoverOrReport(args.ErrorMessage));
    }

    private void RecoverOrReport(string? reason)
    {
        var track = CurrentTrack;
        if (track is not null && _streamingTrackId == track.Id && _recoveredTrackId != track.Id)
        {
            _recoveredTrackId = track.Id;
            _ = RecoverAsync(track);
            return;
        }

        StatusMessage = $"Playback error: {reason}";
    }

    private async Task RecoverAsync(Track track)
    {
        var token = _resolveCts?.Token ?? CancellationToken.None;
        var generation = _generation;
        RunOnUi(() => IsBusy = true);
        try
        {
            var stream = await _ytDlp.ReresolveStreamAsync(track, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            if (CurrentTrack?.Id != track.Id)
                return;

            var source = await CreateSourceAsync(stream).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Play(generation, source);
        }
        catch (OperationCanceledException)
        {

        }
        catch (Exception ex)
        {
            RunOnUi(() => StatusMessage = $"Couldn't play “{track.Title}”: {ex.Message}");
        }
        finally
        {
            RunOnUi(() => IsBusy = false);
        }
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher.HasThreadAccess)
            action();
        else
            _dispatcher.TryEnqueue(() => action());
    }

    public void Dispose()
    {
        TrackListening();
        _history?.EndSession();
        _listeningTimer.Stop();
        _settings.Changed -= OnAudioSettingChanged;
        _handoff.Stop();
        _player.Dispose();
        _standby.Dispose();
    }
}
