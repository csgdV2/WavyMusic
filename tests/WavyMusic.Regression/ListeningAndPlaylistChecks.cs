using System.Reflection;
using Wavy.Core.Abstractions;
using Wavy.Core.Models;
using Wavy.Core.Services;
using Wavy.Services;
using Wavy.ViewModels;

internal static class ListeningAndPlaylistChecks
{
    public static async Task RunAsync()
    {
        static void Check(bool pass, string name)
        {
            if (!pass) throw new Exception("FAIL: " + name);
            Console.WriteLine("PASS: " + name);
        }
        var first = new Track { Id = "one", Title = "Blue Sky", Artist = "First Artist", ArtistBrowseId = "artist-one", Album = "Sunrise", Duration = TimeSpan.FromSeconds(120) };
        var second = new Track { Id = "two", Title = "Evening", Artist = "Second Artist", Album = "Moonlight", Duration = TimeSpan.FromSeconds(20) };
        var tracks = new[] { first, second };
        Check(PlaylistSearch.Filter(tracks, "FIRST sunrise").Single() == first, "playlist search matches words across title, artist and album without case sensitivity");
        Check(PlaylistSearch.Filter(tracks, "  ").SequenceEqual(tracks) && !PlaylistSearch.Filter(tracks, "missing").Any(), "clearing playlist search restores all songs and unmatched queries stay empty");
        var api = DispatchProxy.Create<IYtDlpService, FeedbackCatalogue>();
        var playback = new PlaybackService();
        var player = new PlayerViewModel(playback, new CacheService(), new SettingsService(), api);
        var playlists = new PlaylistService();
        var playlist = playlists.Create("Test playlist");
        playlists.SetTracks(playlist, tracks);
        var vm = new PlaylistsViewModel(playlists, player, api, new SpotifyImportService(api));
        vm.Selected = playlist;
        vm.SearchQuery = "Evening";
        Check(vm.VisibleTracks.SequenceEqual(new[] { second }) && !vm.CanReorderTracks && playlist.Tracks.SequenceEqual(tracks), "filtering a playlist disables reorder without modifying its saved contents");
        await vm.PlayFromAsync(second);
        Check(player.Queue.SequenceEqual(tracks) && player.CurrentIndex == 1, "playing a filtered match retains the full playlist and correct song index");
        vm.SearchQuery = "missing";
        Check(vm.NoSearchMatches && !vm.SelectedIsEmpty, "an empty search result is distinct from an empty playlist");
        vm.SearchQuery = "";
        Check(vm.CanReorderTracks && vm.VisibleTracks.SequenceEqual(tracks), "clearing the playlist filter restores order and drag support");
        vm.VisibleTracks.CollectionChanged += (_, _) => { };
        vm.VisibleTracks.Move(1, 0);
        Check(playlist.Tracks.SequenceEqual(new[] { second, first }), "unfiltered drag reordering updates the complete playlist");
        var online = new OnlinePlaylistViewModel(api, playlists) { Playlist = new PlaylistDetails { Title = "Online", Tracks = tracks } };
        online.SearchQuery = "moonlight";
        Check(online.VisibleTracks.SequenceEqual(new[] { second }) && online.Playlist.Tracks.Count == 2, "online playlist filtering preserves the complete source playlist");

        var folder = Path.Combine(Path.GetTempPath(), "Wavy-recap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "listening-history.json");
            var history = new ListeningHistoryService(path);
            var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
            var day = DateOnly.FromDateTime(now.LocalDateTime);
            history.Advance(first, true, TimeSpan.Zero, now);
            for (var i = 0; i < 35; i++) history.Advance(first, true, TimeSpan.FromSeconds(1), now = now.AddSeconds(1));
            history.Advance(first, false, TimeSpan.Zero, now);
            history.Advance(first, false, TimeSpan.FromSeconds(120), now = now.AddSeconds(120));
            history.Advance(first, true, TimeSpan.Zero, now);
            for (var i = 0; i < 5; i++) history.Advance(first, true, TimeSpan.FromSeconds(1), now = now.AddSeconds(1));
            var recap = history.Recap(day, day);
            Check(recap.Seconds == 40 && recap.Plays == 1, "recap counts actual listening time and ignores pauses and timeline position changes");
            history.Advance(second, true, TimeSpan.Zero, now);
            for (var i = 0; i < 11; i++) history.Advance(second, true, TimeSpan.FromSeconds(1), now = now.AddSeconds(1));
            Check(history.Recap(day, day).Plays == 2, "short songs count after listening to half their duration");
            history.EndSession();
            history.Advance(second, true, TimeSpan.Zero, now);
            for (var i = 0; i < 11; i++) history.Advance(second, true, TimeSpan.FromSeconds(1), now = now.AddSeconds(1));
            history.Advance(second, true, TimeSpan.FromHours(1), now = now.AddHours(1));
            history.EndSession();
            recap = new ListeningHistoryService(path).Recap(day, day);
            Check(recap.Plays == 3 && recap.Seconds == 62 && recap.TopSongs[0].Track.Id == second.Id, "recap persists repeat plays and ranks songs without counting suspension time");
            Check(recap.TopArtists.Single(a => a.Name == "First Artist").BrowseId == "artist-one", "recap retains artist profile IDs after saving and reloading listening history");
            Check(recap.Songs == 2 && recap.Artists == 2 && recap.TopArtists[0].Name == "Second Artist", "recap groups songs and artists using local listening records");
            Check(new ListeningHistoryService(path).Recap(day.AddDays(1), day.AddDays(10)).Seconds == 0, "recap date ranges exclude listening outside the chosen period");
            var ranked = new ListeningHistoryService();
            for (var i = 0; i < 7; i++)
            {
                var song = new Track { Id = $"rank-{i}", Title = $"Song {i}", Artist = $"Artist {i}", Duration = TimeSpan.FromSeconds(2) };
                ranked.Advance(song, true, TimeSpan.Zero, now);
                ranked.Advance(song, true, TimeSpan.FromSeconds(1), now);
                ranked.EndSession();
            }
            var capped = ranked.Recap(day, day);
            Check(capped.TopSongs.Count == 5 && capped.TopArtists.Count == 5 && capped.Songs == 7 && capped.Artists == 7,
                "recap shows only the top five songs and artists while totals include the full listening history");
            var feedbackPath = Path.Combine(folder, "radio-feedback.json");
            var feedback = new RadioFeedbackService(feedbackPath);
            feedback.SetPreference(first, RadioPreference.MoreLikeThis);
            feedback.SetPreference(second, RadioPreference.DontRecommend);
            feedback.Reset();
            var reset = new RadioFeedbackService(feedbackPath);
            Check(reset.Preferred.Count == 0 && reset.Excluded.Count == 0 && new ListeningHistoryService(path).Recap(day, day).Plays == 3,
                "resetting radio preferences persists and preserves listening stats");
        }
        finally { Directory.Delete(folder, true); }
    }
}
