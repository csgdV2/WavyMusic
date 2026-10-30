using Microsoft.Extensions.DependencyInjection;
using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Input;
using Wavy.Core.Abstractions;
using Wavy.Core.Models;
using Wavy.Core.Services;
using Wavy.ViewModels;

namespace Wavy.Views;

public sealed partial class ListeningStatsPage : Page
{
    private readonly ListeningHistoryService _history = App.Services.GetRequiredService<ListeningHistoryService>();
    private readonly DispatcherQueueTimer _refresh;
    private string? _lastKey;
    private string? _coverUrl;
    private CancellationTokenSource? _artistLookup;
    private bool _findingArtist;
    private readonly ObservableCollection<RecapArtistRow> _artistRows = new();
    private static readonly Dictionary<string, ArtistResult> ArtistImages = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _imageLookup;
    public ListeningStatsPage()
    {
        InitializeComponent();
        TopArtists.ItemsSource = _artistRows;
        _refresh = DispatcherQueue.CreateTimer();
        _refresh.Interval = TimeSpan.FromSeconds(5);
        _refresh.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _refresh.Start(); };
        Unloaded += (_, _) => { _refresh.Stop(); _artistLookup?.Cancel(); _imageLookup?.Cancel(); };
    }
    private void Period_Changed(object sender, SelectionChangedEventArgs args) { if (Minutes is not null) Refresh(); }
    private void Recap_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        var wide = args.NewSize.Width >= 880;
        ArtistColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(ArtistsSection, wide ? 1 : 0);
        Grid.SetRow(ArtistsSection, wide ? 0 : 1);
        var artSize = args.NewSize.Width < 540 ? 96 : 128;
        HeroArtColumn.Width = new GridLength(artSize);
        HeroArtBorder.Width = HeroArtBorder.Height = artSize;
    }
    private void Refresh()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var from = Period.SelectedIndex == 0 ? new DateOnly(today.Year, today.Month, 1)
            : Period.SelectedIndex == 1 ? new DateOnly(today.Year, 1, 1)
            : Period.SelectedIndex == 2 ? new DateOnly(today.Year - 1, 1, 1) : DateOnly.MinValue;
        var to = Period.SelectedIndex == 2 ? new DateOnly(today.Year - 1, 12, 31) : today;
        var recap = _history.Recap(from, to);
        var key = $"{from}:{to}:{Period.SelectedIndex}:{Math.Floor(recap.Seconds / 60)}:{recap.Plays}:{recap.Songs}:{recap.Artists}:"
            + string.Join("|", recap.TopSongs.Select(t => $"{t.Track.Id}:{t.Plays}:{Math.Floor(t.Seconds / 60)}"));
        if (key == _lastKey) return;
        _lastKey = key;
        RecapTitle.Text = Period.SelectedIndex == 0 ? DateTime.Now.ToString("MMMM yyyy") + " in music"
            : Period.SelectedIndex == 1 ? $"Your {today.Year} in music"
            : Period.SelectedIndex == 2 ? $"Your {today.Year - 1} in music" : "Your music, over time";
        Minutes.Text = Math.Floor(recap.Seconds / 60).ToString("N0");
        Plays.Text = recap.Plays.ToString("N0");
        SongCount.Text = recap.Songs.ToString("N0");
        ArtistCount.Text = recap.Artists.ToString("N0");
        Since.Text = "On this device since " + _history.StartedUtc.ToLocalTime().ToString("d MMM yyyy");
        TopSongs.ItemsSource = recap.TopSongs.Select((song, index) => new RecapSongRow(index + 1, song.Track,
            song.Plays == 1 ? "1 play" : $"{song.Plays:N0} plays", $"{Math.Floor(song.Seconds / 60):N0} min")).ToList();
        _imageLookup?.Cancel();
        _imageLookup?.Dispose();
        _imageLookup = new CancellationTokenSource();
        _artistRows.Clear();
        foreach (var (artist, index) in recap.TopArtists.Select((artist, index) => (artist, index)))
        {
            ArtistImages.TryGetValue(artist.BrowseId ?? artist.Name, out var image);
            _artistRows.Add(new RecapArtistRow(index + 1, artist.Name, $"{Math.Floor(artist.Seconds / 60):N0} min",
                image?.BrowseId ?? artist.BrowseId, image?.ThumbnailUrl));
        }
        _ = LoadArtistImagesAsync(_artistRows.ToArray(), _imageLookup.Token);
        var best = recap.TopSongs.FirstOrDefault()?.Track;
        TopSongTitle.Text = best?.Title ?? "";
        TopSongArtistText.Text = best?.Artist ?? "";
        TopSongArtist.Tag = best;
        if (best?.ThumbnailUrl != _coverUrl)
        {
            _coverUrl = best?.ThumbnailUrl;
            HeroArt.Source = Uri.TryCreate(_coverUrl, UriKind.Absolute, out var uri) ? new BitmapImage(uri) { DecodePixelWidth = 320 } : null;
        }
        EmptyState.Visibility = recap.Seconds == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecapBody.Visibility = recap.Seconds > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task LoadArtistImagesAsync(IReadOnlyList<RecapArtistRow> rows, CancellationToken ct)
    {
        using var limit = new SemaphoreSlim(2);
        async Task LoadAsync(RecapArtistRow row, int index)
        {
            if (!string.IsNullOrWhiteSpace(row.ThumbnailUrl)) return;
            try
            {
                await limit.WaitAsync(ct);
                try
                {
                    var catalogue = App.Services.GetRequiredService<IYtDlpService>();
                    var matches = await catalogue.SearchArtistsAsync(row.Name, 4, ct);
                    var artist = matches.FirstOrDefault(a => row.BrowseId is not null && a.BrowseId == row.BrowseId)
                        ?? matches.FirstOrDefault(a => string.Equals(a.Name, row.Name, StringComparison.OrdinalIgnoreCase));
                    if (artist?.ThumbnailUrl is null && row.BrowseId is { Length: > 0 } browseId)
                    {
                        var details = await catalogue.GetArtistAsync(browseId, ct);
                        if (details?.BannerUrl is { Length: > 0 } url)
                            artist = new ArtistResult { BrowseId = browseId, Name = row.Name, ThumbnailUrl = url };
                    }
                    if (ct.IsCancellationRequested || artist?.ThumbnailUrl is null) return;
                    if (ArtistImages.Count >= 100) ArtistImages.Clear();
                    ArtistImages[row.BrowseId ?? row.Name] = artist;
                    if (IsLoaded && index < _artistRows.Count && _artistRows[index] == row)
                        _artistRows[index] = row with { BrowseId = artist.BrowseId, ThumbnailUrl = artist.ThumbnailUrl };
                }
                finally { limit.Release(); }
            }
            catch (OperationCanceledException) { }
            catch { }
        }
        await Task.WhenAll(rows.Select(LoadAsync));
    }

    private async void Artist_Click(object sender, RoutedEventArgs args)
    {
        if (_findingArtist || sender is not FrameworkElement element) return;
        var value = element.Tag ?? element.DataContext;
        var (name, browseId) = value switch
        {
            RecapArtistRow artist => (artist.Name, artist.BrowseId),
            Track track => (track.Artist, track.ArtistBrowseId),
            _ => (null, null),
        };
        if (string.IsNullOrWhiteSpace(name)) return;
        _findingArtist = true;
        _artistLookup?.Dispose();
        _artistLookup = new CancellationTokenSource();
        var token = _artistLookup.Token;
        ArtistMessage.IsOpen = false;
        try
        {
            if (string.IsNullOrWhiteSpace(browseId))
            {
                var matches = await App.Services.GetRequiredService<IYtDlpService>().SearchArtistsAsync(name, 4, token);
                browseId = (matches.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? matches.FirstOrDefault())?.BrowseId;
            }
            if (token.IsCancellationRequested || !IsLoaded) return;
            if (!string.IsNullOrWhiteSpace(browseId)) Frame.Navigate(typeof(ArtistPage), browseId);
            else { ArtistMessage.Message = "Couldn't find this artist. Try again in a moment."; ArtistMessage.IsOpen = true; }
        }
        catch (OperationCanceledException) { }
        catch { if (IsLoaded && !token.IsCancellationRequested) { ArtistMessage.Message = "Couldn't open this artist. Try again in a moment."; ArtistMessage.IsOpen = true; } }
        finally { _findingArtist = false; }
    }
    private void Artist_PointerEntered(object sender, PointerRoutedEventArgs args)
    {
        if (sender is HyperlinkButton { Content: TextBlock text }) text.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
    }
    private void Artist_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        if (sender is HyperlinkButton { Content: TextBlock text }) text.TextDecorations = Windows.UI.Text.TextDecorations.None;
    }

    private async void Song_Click(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is RecapSongRow song) await App.Services.GetRequiredService<PlayerViewModel>().PlayRadioAsync(song.Track);
    }
}
