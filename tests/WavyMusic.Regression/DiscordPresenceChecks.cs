using System.Buffers.Binary;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Wavy.Core.Models;
using Wavy.Services;

internal static class DiscordPresenceChecks
{
    public static async Task RunAsync()
    {
        var prefix = "wavy-regression-" + Guid.NewGuid().ToString("N") + "-";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var server = new NamedPipeServerStream(prefix + "0", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var player = new PlaybackService
        {
            CurrentTrack = new Track { Id = "first", Title = "First song", Artist = "First artist" },
            IsPlaying = true, Duration = TimeSpan.FromMinutes(3),
        };
        using var presence = new DiscordPresenceService(player, new SettingsService(), prefix);
        var tick = typeof(DiscordPresenceService).GetMethod("TickAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var shown = typeof(DiscordPresenceService).GetField("_shown", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var requests = new List<JsonElement>();
        var peer = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(timeout.Token);
            var handshake = await ReadAsync(server, timeout.Token);
            if (handshake.Opcode != 0) throw new Exception("Missing IPC handshake");
            await WriteAsync(server, 1, "{\"cmd\":\"DISPATCH\",\"evt\":\"READY\",\"data\":{}}", timeout.Token);
            for (var i = 0; i < 4; i++)
            {
                var frame = await ReadAsync(server, timeout.Token);
                using var doc = JsonDocument.Parse(frame.Body);
                requests.Add(doc.RootElement.Clone());
                var nonce = doc.RootElement.GetProperty("nonce").GetString();
                if (i == 0)
                {
                    await WriteAsync(server, 3, "{}", timeout.Token);
                    if ((await ReadAsync(server, timeout.Token)).Opcode != 4) throw new Exception("Client did not answer IPC ping");
                    await WriteAsync(server, 1, "{\"cmd\":\"DISPATCH\",\"evt\":\"OTHER\"}", timeout.Token);
                }
                await WriteAsync(server, 1, JsonSerializer.Serialize(new { cmd = "SET_ACTIVITY", nonce, evt = i == 3 ? "ERROR" : null, data = new { } }), timeout.Token);
            }
        }, timeout.Token);

        await ((Task)tick.Invoke(presence, null)!).WaitAsync(timeout.Token);
        Check(shown.GetValue(presence) is not null, "Discord accepts acknowledged activity after ping and unrelated event");
        player.CurrentTrack = new Track { Id = "second", Title = "Second song", Artist = "A" };
        await ((Task)tick.Invoke(presence, null)!).WaitAsync(timeout.Token);
        player.IsPlaying = false;
        await ((Task)tick.Invoke(presence, null)!).WaitAsync(timeout.Token);
        player.IsPlaying = true;
        await ((Task)tick.Invoke(presence, null)!).WaitAsync(timeout.Token);
        await peer;

        var first = requests[0].GetProperty("args").GetProperty("activity");
        var second = requests[1].GetProperty("args").GetProperty("activity");
        Check(first.GetProperty("type").GetInt32() == 2 && first.GetProperty("name").GetString() == "First artist", "listening activity headline sends the artist instead of the app name");
        Check(second.GetProperty("name").GetString() == "A" && second.GetProperty("state").GetString() == "A", "switching tracks updates both headlines and preserves single-character artists");
        Check(second.GetProperty("details").GetString() == "Second song", "song title remains in the rich presence details");
        Check(requests[2].GetProperty("args").GetProperty("activity").ValueKind == JsonValueKind.Null, "pausing clears the listening activity");
        Check(shown.GetValue(presence) is null, "a rejected Discord update is not cached as successful");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }

    private static async Task<(int Opcode, string Body)> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[8];
        await stream.ReadExactlyAsync(header, ct);
        var body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4))];
        await stream.ReadExactlyAsync(body, ct);
        return (BinaryPrimitives.ReadInt32LittleEndian(header), Encoding.UTF8.GetString(body));
    }

    private static async Task WriteAsync(Stream stream, int opcode, string json, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), body.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }
}
