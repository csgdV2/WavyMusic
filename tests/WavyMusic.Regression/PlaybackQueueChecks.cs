using System.Reflection;
using Wavy.Core.Abstractions;
using Wavy.Core.Models;
using Wavy.Services;
using Wavy.ViewModels;

internal static class PlaybackQueueChecks
{
    public static async Task RunAsync()
    {
        static Track Song(string id) => new() { Id = id, Title = id, Artist = "Artist" };
        static void Check(bool pass, string name)
        {
            if (!pass) throw new Exception("FAIL: " + name);
            Console.WriteLine("PASS: " + name);
        }
        static (PlayerViewModel Player, PlaybackService Playback, RadioCatalogue Catalogue, IYtDlpService Api) Create()
        {
            var api = DispatchProxy.Create<IYtDlpService, RadioCatalogue>();
            var playback = new PlaybackService();
            return (new PlayerViewModel(playback, new CacheService(), new SettingsService(), api), playback, (RadioCatalogue)(object)api, api);
        }

        foreach (var source in new[] { "recent", "mix", "suggestions", "trending", "fresh" })
        {
            var fixture = Create();
            var home = new HomeViewModel(new CacheService(), new PlaylistService(), new FavoritesService(), fixture.Api, fixture.Player);
            var row = source switch { "recent" => home.Recent, "mix" => home.Mix, "suggestions" => home.Suggestions, "trending" => home.Trending, _ => home.Fresh };
            foreach (var i in Enumerable.Range(0, 6)) row.Add(Song($"visible-{i}"));
            var selected = row[3];
            await (source switch
            {
                "recent" => home.PlayRecentAsync(selected), "mix" => home.PlayMixAsync(selected),
                "suggestions" => home.PlaySuggestionAsync(selected), "trending" => home.PlayTrendingAsync(selected), _ => home.PlayFreshAsync(selected)
            });
            Check(fixture.Player.Queue.Select(t => t.Id).SequenceEqual(new[] { selected.Id, selected.Id + "-similar-1", selected.Id + "-similar-2" })
                && fixture.Playback.CurrentTrack == selected && fixture.Catalogue.Seeds.SequenceEqual(new[] { selected.Id }),
                $"Home {source} starts the selected song followed by similar music, without queuing the visible row");
        }

        var locked = Create();
        var collection = new[] { Song("album-1"), Song("album-2"), Song("album-3") };
        await locked.Player.PlayFromAsync(collection, 1);
        await locked.Player.NextCommand.ExecuteAsync(null);
        await locked.Player.NextCommand.ExecuteAsync(null);
        Check(locked.Player.Queue.SequenceEqual(collection) && locked.Playback.CurrentTrack == collection[2]
            && locked.Catalogue.Seeds.Count == 0 && !locked.Player.AutoplayIsOn,
            "collection queues retain their order and do not append radio songs at the end");

        var pending = Create();
        var response = new TaskCompletionSource<IReadOnlyList<Track>>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending.Catalogue.Pending["radio"] = response;
        var radio = pending.Player.PlayRadioAsync(Song("radio"));
        var next = pending.Player.NextCommand.ExecuteAsync(null);
        Check(pending.Playback.CurrentTrack?.Id == "radio" && !next.IsCompleted, "radio starts playback immediately while Next waits for recommendations");
        response.SetResult(new[] { Song("radio"), Song("next-1"), Song("next-1"), Song("next-2") });
        await Task.WhenAll(radio, next);
        Check(pending.Playback.CurrentTrack?.Id == "next-1" && pending.Playback.PreparedNext?.Id == "next-2"
            && pending.Player.Queue.Count(t => t.Id == "next-1") == 1 && pending.Catalogue.Seeds.Count(s => s == "radio") == 1,
            "Next shares the pending radio request, excludes duplicates and prepares the next similar song");

        var stale = Create();
        var oldResponse = new TaskCompletionSource<IReadOnlyList<Track>>(TaskCreationOptions.RunContinuationsAsynchronously);
        stale.Catalogue.Pending["old-radio"] = oldResponse;
        var oldRadio = stale.Player.PlayRadioAsync(Song("old-radio"));
        await stale.Player.PlayFromAsync(collection, 0, lockQueue: true);
        oldResponse.SetResult(new[] { Song("stale-recommendation") });
        await oldRadio;
        Check(stale.Player.Queue.SequenceEqual(collection), "late radio results cannot change a newly selected playlist or album queue");

        var disabled = Create();
        disabled.Player.AutoplayEnabled = false;
        await disabled.Player.PlayRadioAsync(Song("single"));
        Check(disabled.Player.Queue.Count == 1 && disabled.Catalogue.Seeds.Count == 0, "starting a single song respects the autoplay-off setting");

        var same = Create();
        await same.Player.PlayFromAsync(new[] { Song("same") }, 0, lockQueue: true);
        await same.Player.PlayRadioAsync(same.Playback.CurrentTrack!);
        Check(same.Player.Queue.Count == 3 && same.Playback.PreparedNext?.Id == "same-similar-1" && same.Player.AutoplayIsOn,
            "starting radio for the song already playing still loads recommendations and prepares the next track");
    }
}

public class RadioCatalogue : DispatchProxy
{
    public List<string> Seeds { get; } = new();
    public Dictionary<string, TaskCompletionSource<IReadOnlyList<Track>>> Pending { get; } = new();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name != nameof(IYtDlpService.GetSimilarAsync)) throw new NotSupportedException(targetMethod?.Name);
        var seed = (Track)args![0]!;
        Seeds.Add(seed.Id);
        if (Pending.TryGetValue(seed.Id, out var pending)) return pending.Task;
        return Task.FromResult<IReadOnlyList<Track>>(new[]
        {
            new Track { Id = seed.Id, Title = seed.Title },
            new Track { Id = seed.Id + "-similar-1", Title = "Similar one" },
            new Track { Id = seed.Id + "-similar-2", Title = "Similar two" }
        });
    }
}
