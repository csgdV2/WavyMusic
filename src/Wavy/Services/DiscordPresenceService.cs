using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Wavy.Core.Models;

namespace Wavy.Services;

public sealed class DiscordPresenceService : IDisposable
{

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan SeekTolerance = TimeSpan.FromSeconds(2);

    private readonly PlaybackService _player;
    private readonly SettingsService _settings;
    private readonly Timer _timer;
    private readonly string _pipePrefix;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private NamedPipeClientStream? _pipe;
    private DateTime _nextAttempt = DateTime.MinValue;
    private string? _appId;

    private string? _shown;
    private DateTimeOffset _startedAt;
    private bool _disposed;

    public DiscordPresenceService(PlaybackService player, SettingsService settings)
        : this(player, settings, "discord-ipc-") { }

    internal DiscordPresenceService(PlaybackService player, SettingsService settings, string pipePrefix)
    {
        _player = player;
        _settings = settings;
        _pipePrefix = pipePrefix;

        _settings.Changed += OnSettingChanged;

        _timer = new Timer(_ => _ = TickAsync(), null, Interval, Interval);
    }

    private bool Enabled => _settings.DiscordPresence;

    private async Task TickAsync()
    {
        if (_disposed)
            return;

        try
        {
            if (!Enabled)
            {
                await ClearAsync().ConfigureAwait(false);
                return;
            }

            var track = _player.CurrentTrack;
            if (track is null || !_player.IsPlaying)
            {
                await ClearAsync().ConfigureAwait(false);
                return;
            }

            await PublishAsync(track).ConfigureAwait(false);
        }
        catch
        {

            Drop();
        }
    }

    private async Task PublishAsync(Track track)
    {
        var position = _player.Position;
        var duration = _player.Duration;
        var playing = _player.IsPlaying;

        var anchor = DateTimeOffset.UtcNow - position;
        if (_shown is null || (anchor - _startedAt).Duration() > SeekTolerance)
            _startedAt = anchor;

        var activity = new Dictionary<string, object?>
        {

            ["type"] = 2,
            ["name"] = Trim(track.Artist, "Unknown artist"),
            ["status_display_type"] = 1,
            ["details"] = Trim(track.Title, "Unknown song"),
            ["details_url"] = track.MusicUrl,
            ["state"] = Trim(track.Artist, "Unknown artist"),
        };

        if (track.ArtistUrl is { } artistUrl)
            activity["state_url"] = artistUrl;

        if (playing && duration > TimeSpan.Zero)
        {
            activity["timestamps"] = new Dictionary<string, object?>
            {
                ["start"] = _startedAt.ToUnixTimeMilliseconds(),
                ["end"] = (_startedAt + duration).ToUnixTimeMilliseconds(),
            };
        }

        if (!string.IsNullOrWhiteSpace(track.ThumbnailUrl))
        {
            var assets = new Dictionary<string, object?> { ["large_image"] = track.ThumbnailUrl };
            var album = track.Album?.Trim();
            if (!string.IsNullOrWhiteSpace(album)
                && !string.Equals(album, track.Title.Trim(), StringComparison.OrdinalIgnoreCase)
                && !string.Equals(album, track.Artist?.Trim(), StringComparison.OrdinalIgnoreCase))
                assets["large_text"] = Trim(album, "");
            activity["assets"] = assets;
        }

        var payload = JsonSerializer.Serialize(activity);
        if (payload == _shown)
            return;

        if (await SendActivityAsync(activity).ConfigureAwait(false))
            _shown = payload;
    }

    private async Task ClearAsync()
    {
        if (_shown is null || _pipe is null)
        {
            _shown = null;
            return;
        }

        if (await SendActivityAsync(null).ConfigureAwait(false))
            _shown = null;
    }

    private async Task<bool> SendActivityAsync(Dictionary<string, object?>? activity)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!await EnsureConnectedAsync().ConfigureAwait(false))
                return false;

            var nonce = Guid.NewGuid().ToString();
            var frame = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["cmd"] = "SET_ACTIVITY",
                ["nonce"] = nonce,
                ["args"] = new Dictionary<string, object?>
                {
                    ["pid"] = Environment.ProcessId,
                    ["activity"] = activity,
                },
            });

            await WriteAsync(Opcode.Frame, frame).ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (await ReadAsync(timeout.Token).ConfigureAwait(false) is { } reply)
            {
                using var response = JsonDocument.Parse(reply);
                if (!response.RootElement.TryGetProperty("nonce", out var replyNonce) || replyNonce.GetString() != nonce)
                    continue;
                if (response.RootElement.TryGetProperty("evt", out var evt) && evt.GetString() == "ERROR")
                    throw new IOException("Discord rejected the presence update.");
                return response.RootElement.TryGetProperty("cmd", out var command) && command.GetString() == "SET_ACTIVITY";
            }
            return false;
        }
        catch
        {
            Drop();
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> EnsureConnectedAsync()
    {
        var appId = _settings.DiscordApplicationId?.Trim();
        if (string.IsNullOrEmpty(appId))
            return false;

        if (_pipe is { IsConnected: true } && appId == _appId)
            return true;

        Drop();
        if (DateTime.UtcNow < _nextAttempt)
            return false;
        _nextAttempt = DateTime.UtcNow + RetryDelay;

        for (var i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(
                ".", $"{_pipePrefix}{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(300).ConfigureAwait(false);
                _pipe = pipe;
                _appId = appId;

                await WriteAsync(Opcode.Handshake, $"{{\"v\":1,\"client_id\":\"{appId}\"}}")
                    .ConfigureAwait(false);

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var ready = await ReadAsync(timeout.Token).ConfigureAwait(false);
                using var handshake = ready is not null ? JsonDocument.Parse(ready) : null;
                if (handshake is null || !handshake.RootElement.TryGetProperty("evt", out var evt) || evt.GetString() != "READY")
                {
                    Drop();
                    continue;
                }

                _nextAttempt = DateTime.MinValue;
                _shown = null;
                return true;
            }
            catch
            {
                pipe.Dispose();
                if (ReferenceEquals(_pipe, pipe))
                    _pipe = null;
            }
        }

        return false;
    }

    private async Task WriteAsync(Opcode opcode, string json)
    {
        if (_pipe is not { IsConnected: true } pipe)
            throw new IOException("Discord pipe is not connected.");

        var body = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), (int)opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), body.Length);
        body.CopyTo(frame.AsSpan(8));

        await pipe.WriteAsync(frame).ConfigureAwait(false);
        await pipe.FlushAsync().ConfigureAwait(false);
    }

    private async Task<string?> ReadAsync(CancellationToken ct)
    {
        if (_pipe is not { IsConnected: true } pipe)
            return null;

        while (true)
        {
            var header = new byte[8];
            if (!await FillAsync(pipe, header, ct).ConfigureAwait(false)) return null;
            var opcode = (Opcode)BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
            var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
            if (length is < 0 or > (1 << 20)) throw new IOException("Invalid Discord frame length.");
            var body = new byte[length];
            if (!await FillAsync(pipe, body, ct).ConfigureAwait(false)) return null;
            var payload = Encoding.UTF8.GetString(body);
            if (opcode == Opcode.Close) throw new IOException("Discord closed the presence connection.");
            if (opcode == Opcode.Ping)
            {
                await WriteAsync(Opcode.Pong, payload).ConfigureAwait(false);
                continue;
            }
            if (opcode == Opcode.Frame) return payload;
        }
    }

    private static async Task<bool> FillAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var got = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (got <= 0)
                return false;
            read += got;
        }
        return true;
    }

    private void OnSettingChanged(object? sender, string name)
    {
        if (name != nameof(SettingsService.DiscordPresence))
        {
            return;
        }

        if (!Enabled)
        {

            _ = Task.Run(async () =>
            {
                try { await ClearAsync().ConfigureAwait(false); } catch { }
                Drop();
            });
            return;
        }

        _nextAttempt = DateTime.MinValue;
        _ = TickAsync();
    }

    private void Drop()
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        _appId = null;
        _shown = null;
    }

    private static string Trim(string? text, string fallback)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
            return fallback;
        return value.Length <= 128 ? value : value[..127] + "…";
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _settings.Changed -= OnSettingChanged;
        _timer.Dispose();
        Drop();
        _gate.Dispose();
    }

    private enum Opcode
    {
        Handshake = 0,
        Frame = 1,
        Close = 2,
        Ping = 3,
        Pong = 4,
    }
}
