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
    Check(ReadAudio(await processor.OpenAudioAsync(source)).SequenceEqual(Read(source)), "flat EQ with normalization off preserves the original samples");
    settings.AudioNormalization = true;
    var normalized = await processor.OpenAudioAsync(source);
    var originalSamples = Read(source);
    var processedSamples = ReadAudio(normalized);
    var gain = processedSamples[24002] / originalSamples[24002];
    Check(processedSamples.Length == originalSamples.Length && processedSamples.Zip(originalSamples).All(pair => Math.Abs(pair.First - pair.Second * gain) < 0.00001), "normalization applies constant gain without compression");
    Check(processedSamples.Max(x => Math.Abs(x)) <= Math.Pow(10, -2.0 / 20) + 0.00001, "normalization leaves peak headroom");
    Check(normalized.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat, "playback audio stays lossless float PCM");
    var quieterPath = Path.Combine(folder, "quieter.wav");
    using (var writer = new WaveFileWriter(quieterPath, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)))
    {
        var quieter = originalSamples.Select(value => value * 0.25f).ToArray();
        writer.WriteSamples(quieter, 0, quieter.Length);
    }
    var quieterOutput = ReadAudio(await processor.OpenAudioAsync(quieterPath));
    Check(quieterOutput.Length == processedSamples.Length && quieterOutput.Zip(processedSamples).All(p => Math.Abs(p.First - p.Second) < 0.00001),
        "quiet and loud recordings of the same song normalize to the same playback level");
    var quietPassage = Rms(processedSamples.Take(96000).ToArray());
    var loudPassage = Rms(processedSamples.Skip(96000).ToArray());
    Check(Math.Abs(loudPassage / quietPassage - 3) < 0.001,
        "normalization preserves the original loud-to-quiet passage ratio throughout the song");
    using (var seekCheck = await processor.OpenAudioAsync(source))
    {
        seekCheck.Seek(TimeSpan.FromSeconds(1));
        var afterSeek = new float[96000];
        var count = seekCheck.Read(afterSeek, 0, afterSeek.Length);
        Check(count == afterSeek.Length && afterSeek.Zip(processedSamples.Skip(96000)).All(p => Math.Abs(p.First - p.Second) < 0.00001),
            "seeking retains the same fixed normalization gain");
    }
    settings.AudioNormalization = false;
    settings.EqualizerBands[5] = -12;
    var equalized = ReadAudio(await processor.OpenAudioAsync(source));
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
        var brightOutput = await processor.OpenAudioAsync(brightPath);
        var after = ReadAudio(brightOutput);
        var scale = before.Zip(after).Sum(p => (double)p.First * p.Second) / before.Sum(x => (double)x * x);
        Check(brightOutput.WaveFormat.SampleRate == sampleRate && brightOutput.WaveFormat.Channels == 2
            && before.Length == after.Length && before.Zip(after).All(p => Math.Abs(p.Second - p.First * scale) < 0.000001),
            $"normalization preserves bass, 12/18 kHz detail, stereo and sample rate at {sampleRate} Hz");
    }
    using (var liveSource = await processor.CreateSourceAsync(source))
    using (var player = new Windows.Media.Playback.MediaPlayer { IsMuted = true })
    {
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaOpened += (_, _) => opened.TrySetResult();
        player.MediaEnded += (_, _) => ended.TrySetResult();
        player.MediaFailed += (_, e) =>
        {
            opened.TrySetException(new Exception(e.ErrorMessage));
            ended.TrySetException(new Exception(e.ErrorMessage));
        };
        player.Source = liveSource;
        player.Play();
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Check(player.PlaybackSession.CanSeek && Math.Abs(player.PlaybackSession.NaturalDuration.TotalSeconds - 2) < 0.05,
            "Windows playback opens the in-memory processed audio with duration and seeking");
        player.PlaybackSession.Position = TimeSpan.FromSeconds(1.5);
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(true, "Windows playback seeks and reaches the end of processed audio");
        player.Source = null;
    }
    var analysisCache = new CacheService();
    var persistedProcessor = new LoudnessNormalizationService(settings, analysisCache);
    typeof(LoudnessNormalizationService).GetField("Measurements", BindingFlags.NonPublic | BindingFlags.Static)!
        .GetValue(null)!.GetType().GetMethod("Clear")!.Invoke(
            typeof(LoudnessNormalizationService).GetField("Measurements", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null), null);
    using (var analyzed = await persistedProcessor.OpenAudioAsync(source)) { }
    Check(analysisCache.AudioAnalysis.ContainsKey(source), "loudness results are saved through the existing cache index");
    typeof(LoudnessNormalizationService).GetField("Measurements", BindingFlags.NonPublic | BindingFlags.Static)!
        .GetValue(null)!.GetType().GetMethod("Clear")!.Invoke(
            typeof(LoudnessNormalizationService).GetField("Measurements", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null), null);
    using (var reused = await new LoudnessNormalizationService(settings, analysisCache).OpenAudioAsync(source)) { }
    Check(analysisCache.SavedAnalysisReads == 1, "a new playback session reuses saved loudness analysis");
    settings.EqualizerBands[5] = -6;
    using (var changedEq = await persistedProcessor.OpenAudioAsync(source)) { }
    Check(analysisCache.SavedAnalysisReads == 1, "changing EQ invalidates saved analysis");
    settings.EqualizerBands = new double[10];
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    try { await processor.OpenAudioAsync(source, cancelled.Token); throw new Exception("Cancellation was ignored"); }
    catch (OperationCanceledException) { Console.WriteLine("PASS: audio processing honors cancellation"); }
    Check(Directory.GetFiles(folder).Length == 5, "normalization and EQ create no additional audio files");
    using var seekable = await processor.OpenAudioAsync(source);
    seekable.Seek(TimeSpan.FromSeconds(1));
    var seekBuffer = new float[960];
    Check(seekable.Read(seekBuffer, 0, seekBuffer.Length) == seekBuffer.Length && Math.Abs(seekable.Position.TotalSeconds - 1.01) < 0.001,
        "processed playback seeks with frame-accurate timestamps");
}
finally { Directory.Delete(folder, true); }
Console.WriteLine("All regression checks passed.");

static float[] ReadAudio(LoudnessNormalizationService.PlaybackAudio audio)
{
    using (audio)
    {
        var result = new List<float>(); var buffer = new float[8192]; int count;
        while ((count = audio.Read(buffer, 0, buffer.Length)) > 0) result.AddRange(buffer.Take(count));
        return result.ToArray();
    }
}

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
