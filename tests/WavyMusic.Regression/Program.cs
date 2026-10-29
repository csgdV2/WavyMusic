using System.Reflection;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Wavy.Core.Abstractions;
using Wavy.Core.Models;
using Wavy.Core.Services;
using Wavy.Services;
using Wavy.ViewModels;
using NAudio.Wave;

static void Check(bool value, string name)
{
    if (!value) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}

using (var doc = JsonDocument.Parse("""
{"streamingData":{"formats":[{"mimeType":"video/mp4; codecs=\"mp4a\"","bitrate":900000,"url":"video"}],"adaptiveFormats":[{"mimeType":"audio/mp4","bitrate":48000,"url":"low"},{"mimeType":"audio/mp4","bitrate":128000,"url":"balanced"},{"mimeType":"audio/mp4","bitrate":256000,"url":"high"}]}}
"""))
{
    var pick = typeof(YouTubeStreamClient).GetMethod("PickAudioUrl", BindingFlags.NonPublic | BindingFlags.Static)!;
    foreach (var (quality, expected) in new[] { (AudioQuality.Best, "high"), (AudioQuality.Balanced, "balanced"), (AudioQuality.DataSaver, "low") })
        Check((string?)pick.Invoke(null, new object[] { doc.RootElement, quality }) == expected, quality + " selects the expected audio stream");
}

await DiscordPresenceChecks.RunAsync();
await DownloadSourceChecks.RunAsync();
await LyricsProviderChecks.RunAsync();
await UpdateChecks.RunAsync();
await PlaybackQueueChecks.RunAsync();

Track PickTrack(string id, string title, string artist) => new() { Id = id, Title = title, Artist = artist };
var seedTracks = new[] { PickTrack("a", "One", "First"), PickTrack("b", "Two", "First"), PickTrack("c", "Three", "Second"), PickTrack("d", "Four", "Third") };
Check(RecommendationSelector.Seeds(seedTracks, 3).Select(t => t.Id).SequenceEqual(new[] { "a", "c", "d" }), "recommendations use seeds from different artists");
var selected = RecommendationSelector.Pick(new[] {
    new[] { PickTrack("old", "Heard", "Known"), PickTrack("a1", "Alpha", "First"), PickTrack("a2", "Beta", "First"), PickTrack("a3", "Gamma", "First"), PickTrack("a4", "Delta", "Fourth") },
    new[] { PickTrack("b1", "New", "Second"), PickTrack("dupe", "  alpha ", "FIRST"), PickTrack("b2", "Next", "Third") }
}, new[] { PickTrack("old", "Heard", "Known") }, 8);
Check(selected.Select(t => t.Id).SequenceEqual(new[] { "b1", "a1", "a2", "b2", "a4" }), "recommendations interleave radios, omit history and duplicate songs, and cap repeated artists");
Check(RecommendationSelector.Pick(Array.Empty<IEnumerable<Track>>(), Array.Empty<Track>(), 10).Count == 0, "empty recommendation sources remain safe");
Check(RecommendationSelector.Pick(new[] { seedTracks.AsEnumerable() }, Array.Empty<Track>(), 2).Count == 2, "recommendation selection honors its row limit");

var settings = new SettingsService();
var catalogue = DispatchProxy.Create<IYtDlpService, CatalogueProxy>();
var proxy = (CatalogueProxy)(object)catalogue;
var search = new SearchViewModel(catalogue, settings, new CacheService());
Check(search.Recent.Count == 1 && !search.HasQuery, "empty search exposes played songs");
await search.SearchAsync("first");
await search.SearchAsync("first word");
Check(settings.History.Count == 0, "typing searches do not create history entries");
await search.SearchAsync("first word", true);
Check(settings.History.SequenceEqual(new[] { "first word" }), "submitted search records the complete query");
var pending = search.SearchAsync("delayed");
search.Clear();
proxy.Delayed.SetResult(new[] { new Track { Id = "stale", Title = "Late response" } });
await pending;
Check(!search.HasQuery && search.Results.Count == 0 && !search.IsSearching, "clear discards a late search response");
search.SelectedFilter = SearchFilter.Artists;
Check(!search.ShowArtists && search.Recent.Count == 1, "empty search hides the previous result filter");

proxy.ArtistResults = new[] { new ArtistResult { BrowseId = "artist-frrsty", Name = "Frrsty", Subtitle = "Artist", ThumbnailUrl = "portrait" } };
search.SelectedFilter = SearchFilter.Songs;
await search.SearchAsync("Frrsty");
Check(search.ShowFeaturedArtist && search.FeaturedArtist?.BrowseId == "artist-frrsty" && search.Results.Count == 1, "song searches include a matching artist alongside tracks");
await search.SearchAsync("Frr");
Check(search.ShowFeaturedArtist, "artist name prefixes match without requiring the Artists filter");
await search.SearchAsync("Unrelated song");
Check(!search.ShowFeaturedArtist && search.Results.Count == 1, "unrelated artist suggestions do not become featured matches");
proxy.FailArtists = true;
await search.SearchAsync("artist lookup unavailable");
Check(search.Results.Count == 1 && search.Message is null, "artist lookup failure preserves song results");
proxy.FailArtists = false;
var lateArtistSearch = search.SearchAsync("late artist");
search.Clear();
proxy.DelayedArtist.SetResult(new[] { new ArtistResult { BrowseId = "stale-artist", Name = "late artist" } });
await lateArtistSearch;
Check(!search.ShowFeaturedArtist && search.FeaturedArtist is null && search.Results.Count == 0, "clearing search discards a late artist result");

var folder = Path.Combine(Path.GetTempPath(), "Wavy-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    var source = Path.Combine(folder, "tone.wav");
    using (var writer = new WaveFileWriter(source, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)))
    {
        var samples = new float[48000 * 2 * 2];
        for (var frame = 0; frame < samples.Length / 2; frame++)
        {
            var value = (float)((frame < 48000 ? 0.1 : 0.3) * Math.Sin(2 * Math.PI * 1000 * frame / 48000));
            samples[frame * 2] = samples[frame * 2 + 1] = value;
        }
        writer.WriteSamples(samples, 0, samples.Length);
    }
    var processor = new LoudnessNormalizationService(settings);
    Check(await processor.NormalizeAsync(source) == source, "flat EQ with normalization off preserves the original file");
    settings.AudioNormalization = true;
    var normalized = await processor.NormalizeAsync(source);
    var originalSamples = Read(source);
    var processedSamples = Read(normalized);
    var gain = processedSamples[24002] / originalSamples[24002];
    Check(processedSamples.Length == originalSamples.Length && processedSamples.Zip(originalSamples).All(pair => Math.Abs(pair.First - pair.Second * gain) < 0.00001), "normalization applies constant gain without compression");
    Check(processedSamples.Max(x => Math.Abs(x)) <= Math.Pow(10, -2.0 / 20) + 0.00001, "normalization leaves peak headroom");
    using (var reader = new WaveFileReader(normalized)) Check(reader.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat, "processed audio stays lossless float PCM");
    settings.AudioNormalization = false;
    settings.EqualizerBands[5] = -12;
    var equalized = Read(await processor.NormalizeAsync(source));
    Check(Rms(equalized) < Rms(originalSamples) * 0.35, "1 kHz EQ cut changes actual output audio");
    settings.EqualizerBands = new double[10];
    settings.AudioNormalization = true;
    foreach (var sampleRate in new[] { 44100, 48000, 96000 })
    {
        var brightPath = Path.Combine(folder, $"bright-{sampleRate}.wav");
        using (var writer = new WaveFileWriter(brightPath, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2)))
        {
            var samples = new float[sampleRate * 2];
            for (var frame = 0; frame < sampleRate; frame++)
            {
                samples[frame * 2] = (float)(0.08 * Math.Sin(2 * Math.PI * 100 * frame / sampleRate) + 0.04 * Math.Sin(2 * Math.PI * 12000 * frame / sampleRate));
                samples[frame * 2 + 1] = (float)(0.06 * Math.Sin(2 * Math.PI * 18000 * frame / sampleRate));
            }
            writer.WriteSamples(samples, 0, samples.Length);
        }
        var before = Read(brightPath);
        var brightOutput = await processor.NormalizeAsync(brightPath);
        var after = Read(brightOutput);
        var scale = before.Zip(after).Sum(p => (double)p.First * p.Second) / before.Sum(x => (double)x * x);
        using var outputReader = new WaveFileReader(brightOutput);
        Check(outputReader.WaveFormat.SampleRate == sampleRate && outputReader.WaveFormat.Channels == 2
            && before.Length == after.Length && before.Zip(after).All(p => Math.Abs(p.Second - p.First * scale) < 0.000001),
            $"normalization preserves bass, 12/18 kHz detail, stereo and sample rate at {sampleRate} Hz");
    }
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    try { await processor.NormalizeAsync(source, cancelled.Token); throw new Exception("Cancellation was ignored"); }
    catch (OperationCanceledException) { Console.WriteLine("PASS: audio processing honors cancellation"); }
    Check(!Directory.EnumerateFiles(folder, "*.partial.wav").Any(), "processing leaves no partial files");
}
finally { Directory.Delete(folder, true); }
Console.WriteLine("All regression checks passed.");

static float[] Read(string path)
{
    using var reader = new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });
    var provider = reader.ToSampleProvider();
    var result = new List<float>(); var buffer = new float[8192]; int count;
    while ((count = provider.Read(buffer, 0, buffer.Length)) > 0) result.AddRange(buffer.Take(count));
    return result.ToArray();
}
static double Rms(float[] samples) => Math.Sqrt(samples.Average(x => (double)x * x));

public class CatalogueProxy : DispatchProxy
{
    public TaskCompletionSource<IReadOnlyList<Track>> Delayed { get; } = new();
    public IReadOnlyList<ArtistResult> ArtistResults { get; set; } = Array.Empty<ArtistResult>();
    public bool FailArtists { get; set; }
    public TaskCompletionSource<IReadOnlyList<ArtistResult>> DelayedArtist { get; } = new();
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name == nameof(IYtDlpService.SearchAsync))
            return args?[0] as string == "delayed" ? Delayed.Task : Task.FromResult<IReadOnlyList<Track>>(new[] { new Track { Id = "result", Title = "Result" } });
        if (method?.Name == nameof(IYtDlpService.SearchArtistsAsync))
        {
            if (FailArtists) throw new IOException("Artist catalogue unavailable");
            return args?[0] as string == "late artist" ? DelayedArtist.Task : Task.FromResult(ArtistResults);
        }
        throw new NotSupportedException(method?.Name);
    }
}
