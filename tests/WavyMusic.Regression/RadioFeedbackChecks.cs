using System.Reflection;
using Wavy.Core.Abstractions;
using Wavy.Core.Models;
using Wavy.Core.Services;
using Wavy.Services;
using Wavy.ViewModels;

internal static class RadioFeedbackChecks
{
    public static async Task RunAsync()
    {
        static Track Song(string id, string artist = "Artist", string? title = null) => new() { Id = id, Title = title ?? id, Artist = artist };
        static void Check(bool pass, string name)
        {
            if (!pass) throw new Exception("FAIL: " + name);
            Console.WriteLine("PASS: " + name);
        }
        var folder = Path.Combine(Path.GetTempPath(), "Wavy-feedback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "radio-feedback.json");
            var liked = Song("liked", "Favorite Artist");
            var blocked = Song("blocked", "Avoided Artist", "Avoided Song");
            var feedback = new RadioFeedbackService(path);
            feedback.SetPreference(liked, RadioPreference.MoreLikeThis);
            feedback.SetPreference(blocked, RadioPreference.DontRecommend);
            var reloaded = new RadioFeedbackService(path);
            Check(reloaded.GetPreference(liked) == RadioPreference.MoreLikeThis && reloaded.IsExcluded(blocked),
                "radio feedback persists across app restarts");
            var duplicate = Song("different-upload", "  avoided ARTIST ", "Avoided   Song");
            Check(reloaded.IsExcluded(duplicate), "hidden songs also exclude matching title and artist under a different ID");
            Check(reloaded.Seeds(new[] { blocked, Song("history") }, 3).Select(t => t.Id).SequenceEqual(new[] { "liked", "history" }),
                "Home prioritizes a preferred seed and excludes hidden seeds");
            reloaded.SetPreference(duplicate, RadioPreference.MoreLikeThis);
            Check(!reloaded.IsExcluded(blocked) && reloaded.GetPreference(blocked) == RadioPreference.MoreLikeThis,
                "positive feedback replaces negative feedback for the same song");
            reloaded.SetPreference(blocked, RadioPreference.None);
            Check(reloaded.GetPreference(duplicate) == RadioPreference.None, "unchecking feedback restores a neutral preference");

            var api = DispatchProxy.Create<IYtDlpService, FeedbackCatalogue>();
            var catalogue = (FeedbackCatalogue)(object)api;
            var current = Song("current");
            var allowed = Song("allowed", "Other Artist");
            var favored = Song("favored", "Favorite Artist");
            catalogue.Radios[current.Id] = new[] { blocked, duplicate, allowed };
            catalogue.Radios[liked.Id] = new[] { favored };
            catalogue.Charts = new[] { blocked, Song("chart", "Chart Artist") };
            catalogue.Fresh = new[] { duplicate, Song("new", "New Artist") };
            var player = new PlayerViewModel(new PlaybackService(), new CacheService(), new SettingsService(), api, feedback);
            await player.PlayRadioAsync(current);
            Check(player.Queue.Select(t => t.Id).SequenceEqual(new[] { "current", "allowed", "favored" })
                && catalogue.Seeds.Contains(liked.Id), "radio blends preferred-song recommendations and filters hidden tracks");
            var callsBefore = catalogue.Seeds.Count;
            feedback.SetPreference(allowed, RadioPreference.DontRecommend);
            Check(player.Queue.All(t => t.Id != allowed.Id) && player.CurrentIndex == 0,
                "hiding an upcoming radio song removes it without interrupting the current track");
            var collection = new[] { current, blocked, allowed };
            await player.PlayFromAsync(collection, 0, lockQueue: true);
            feedback.SetPreference(blocked, RadioPreference.DontRecommend);
            Check(player.Queue.SequenceEqual(collection), "radio feedback leaves explicitly played playlist and album queues intact");

            var home = new HomeViewModel(new CacheService(), new PlaylistService(), new FavoritesService(), api, player, feedback);
            await home.RefreshRecommendationsAsync();
            Check(home.Suggestions.Any(t => t.Id == favored.Id) && home.Trending.Any(t => t.Id == "chart")
                && home.Fresh.Any(t => t.Id == "new") && home.Suggestions.Concat(home.Trending).Concat(home.Fresh).All(t => !feedback.IsExcluded(t)),
                "Home applies feedback to personalized picks, charts and new releases");

            var lateApi = DispatchProxy.Create<IYtDlpService, FeedbackCatalogue>();
            var lateCatalogue = (FeedbackCatalogue)(object)lateApi;
            lateCatalogue.Pending = new TaskCompletionSource<IReadOnlyList<Track>>();
            var lateFeedback = new RadioFeedbackService();
            var latePlayer = new PlayerViewModel(new PlaybackService(), new CacheService(), new SettingsService(), lateApi, lateFeedback);
            var pending = latePlayer.PlayRadioAsync(current);
            lateFeedback.SetPreference(blocked, RadioPreference.DontRecommend);
            lateCatalogue.Pending.SetResult(new[] { blocked, allowed });
            await pending;
            Check(latePlayer.Queue.Select(t => t.Id).SequenceEqual(new[] { "current", "allowed" }),
                "pending radio results honor feedback given while recommendations are loading");
            File.WriteAllText(path, "invalid json");
            Check(new RadioFeedbackService(path).Preferred.Count == 0, "invalid feedback storage safely falls back to neutral recommendations");
        }
        finally { Directory.Delete(folder, true); }
    }
}

public class FeedbackCatalogue : DispatchProxy
{
    public Dictionary<string, IReadOnlyList<Track>> Radios { get; } = new();
    public List<string> Seeds { get; } = new();
    public IReadOnlyList<Track> Charts { get; set; } = Array.Empty<Track>();
    public IReadOnlyList<Track> Fresh { get; set; } = Array.Empty<Track>();
    public TaskCompletionSource<IReadOnlyList<Track>>? Pending { get; set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name == nameof(IYtDlpService.GetSimilarAsync))
        {
            var track = (Track)args![0]!;
            Seeds.Add(track.Id);
            return Pending?.Task ?? Task.FromResult(Radios.GetValueOrDefault(track.Id) ?? Array.Empty<Track>());
        }
        if (method?.Name == nameof(IYtDlpService.GetTrendingAsync)) return Task.FromResult(Charts);
        if (method?.Name == nameof(IYtDlpService.GetNewReleasesAsync)) return Task.FromResult(Fresh);
        throw new NotSupportedException(method?.Name);
    }
}
