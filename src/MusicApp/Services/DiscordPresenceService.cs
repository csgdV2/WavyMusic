using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using MusicApp.Core.Models;

namespace MusicApp.Services;

public sealed class DiscordPresenceService : IDisposable
{

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan SeekTolerance = TimeSpan.FromSeconds(2);

    private readonly PlaybackService _player;
    private readonly SettingsService _settings;
    private readonly Timer _timer;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private NamedPipeClientStream? _pipe;
    private DateTime _nextAttempt = DateTime.MinValue;
    private string? _appId;

    private string? _shown;
    private DateTimeOffset _startedAt;
    private bool _disposed;

    public DiscordPresenceService(PlaybackService player, SettingsService settings)
    {
        _player = player;
        _settings = settings;

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
            ["details"] = Trim(track.Title, "Unknown song"),
            ["state"] = Trim(track.Artist, "Unknown artist"),
        };

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
            activity["assets"] = new Dictionary<string, object?>
            {

                ["large_image"] = track.ThumbnailUrl,
                ["large_text"] = Trim(track.Title, "Unknown song"),
            };
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

            var frame = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["cmd"] = "SET_ACTIVITY",
                ["nonce"] = Guid.NewGuid().ToString(),
                ["args"] = new Dictionary<string, object?>
                {
                    ["pid"] = Environment.ProcessId,
                    ["activity"] = activity,
                },
            });

            await WriteAsync(Opcode.Frame, frame).ConfigureAwait(false);
            return true;
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
                ".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(300).ConfigureAwait(false);
                _pipe = pipe;
                _appId = appId;

                await WriteAsync(Opcode.Handshake, $"{{\"v\":1,\"client_id\":\"{appId}\"}}")
                    .ConfigureAwait(false);

                if (await ReadAsync().ConfigureAwait(false) is null)
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

    private async Task<string?> ReadAsync()
    {
        if (_pipe is not { IsConnected: true } pipe)
            return null;

        var header = new byte[8];
        if (!await FillAsync(pipe, header).ConfigureAwait(false))
            return null;

        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (length is < 0 or > (1 << 20))
            return null;

        var body = new byte[length];
        if (!await FillAsync(pipe, body).ConfigureAwait(false))
            return null;

        return Encoding.UTF8.GetString(body);
    }

    private static async Task<bool> FillAsync(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var got = await stream.ReadAsync(buffer.AsMemory(read)).ConfigureAwait(false);
            if (got <= 0)
                return false;
            read += got;
        }
        return true;
    }

    private void OnSettingChanged(object? sender, string name)
    {
        if (name is not (nameof(SettingsService.DiscordPresence)
            or nameof(SettingsService.DiscordIdentity)))
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
        if (string.IsNullOrEmpty(value) || value.Length < 2)
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
    }
}
