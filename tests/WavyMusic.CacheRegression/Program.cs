using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Wavy.Core.Abstractions;
using Wavy.Core.Models;
using Wavy.Services;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS: " + message);
}

var folder = Path.Combine(Path.GetTempPath(), "Wavy-cache-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
var socket = new TcpListener(IPAddress.Loopback, 0);
socket.Start();
var port = ((IPEndPoint)socket.LocalEndpoint).Port;
socket.Stop();
using var listener = new HttpListener();
var address = $"http://127.0.0.1:{port}/";
listener.Prefixes.Add(address);
listener.Start();
var payload = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
var requests = 0;
var serving = Task.Run(async () =>
{
    try
    {
        while (listener.IsListening)
        {
            var context = await listener.GetContextAsync();
            Interlocked.Increment(ref requests);
            await Task.Delay(200);
            context.Response.ContentLength64 = payload.Length;
            await context.Response.OutputStream.WriteAsync(payload);
            context.Response.Close();
        }
    }
    catch (Exception) when (!listener.IsListening) { }
});
try
{
    var catalogue = DispatchProxy.Create<IYtDlpService, UnusedCatalogue>();
    var cache = new CacheService(catalogue, folder);
    var track = new Track { Id = "first", Title = "Song: Title?" };
    var first = cache.FetchAsync(track, address);
    var second = cache.FetchAsync(track, address);
    using var cancel = new CancellationTokenSource(30);
    try { await cache.FetchAsync(track, address, cancel.Token); throw new Exception("Cancellation was ignored"); }
    catch (OperationCanceledException) { Check(true, "cancelling a waiting request preserves the active download"); }
    var paths = await Task.WhenAll(first, second);
    Check(requests == 1 && paths[0] is not null && paths[0] == paths[1], "overlapping cache requests download a song only once");
    var path = paths[0]!;
    Check(File.ReadAllBytes(path).SequenceEqual(payload), "cached downloads preserve the original bytes");
    Check(Path.GetFileName(path) == "Song_ Title_ [first].m4a", "cached names preserve song titles with safe Windows characters");
    cache.SaveAudioAnalysis(path, "analysis-key", -12.5, 0.8);
    var reloaded = new CacheService(catalogue, folder);
    Check(reloaded.GetAudioAnalysis(path, "analysis-key") is { IntegratedLufs: -12.5, Peak: 0.8 }, "loudness metadata survives reloading the cache index");
    Check(reloaded.GetAudioAnalysis(path, "different-key") is null, "stale loudness metadata is rejected");
    Check(Directory.GetFiles(folder).Length == 2, "cache keeps only the original song and existing index");
    Check(!Directory.EnumerateFiles(folder, "*.partial").Any(), "shared downloads leave no partial files");
}
finally
{
    listener.Stop();
    await serving;
    Directory.Delete(folder, true);
}
Console.WriteLine("All cache regression checks passed.");

public class UnusedCatalogue : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => throw new NotSupportedException();
}
