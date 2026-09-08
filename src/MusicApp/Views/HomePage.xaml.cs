using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MusicApp.Controls;
using MusicApp.Core.Abstractions;
using MusicApp.Core.Models;
using MusicApp.ViewModels;
using Windows.UI.Text;

namespace MusicApp.Views;

public sealed partial class HomePage : Page
{
    private static readonly char[] ArtistSeparators = { ',', '&', '/', '·', '•', ';' };

    private static readonly string[] FeatureMarkers =
        { " feat. ", " feat ", " ft. ", " ft ", " featuring ", " with " };

    private bool _findingArtist;

    public HomeViewModel ViewModel { get; }

    public HomePage()
    {
        ViewModel = App.Services.GetRequiredService<HomeViewModel>();
        InitializeComponent();
        Loaded += (_, _) => ViewModel.Refresh();
    }

    private async void Recent_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Track track)
            await ViewModel.PlayRecentAsync(track);
    }

    private async void Suggestion_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Track track)
            await ViewModel.PlaySuggestionAsync(track);
    }

    private async void Mix_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Track track)
            await ViewModel.PlayMixAsync(track);
    }

    private async void Trending_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Track track)
            await ViewModel.PlayTrendingAsync(track);
    }

    private async void Fresh_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Track track)
            await ViewModel.PlayFreshAsync(track);
    }

    private void Playlist_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not Playlist playlist)
            return;

        Frame.Navigate(typeof(PlaylistsPage));
        if (Frame.Content is PlaylistsPage page)
            page.Select(playlist);
    }

    private void Album_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AlbumResult album)
            Frame.Navigate(typeof(AlbumPage), album.BrowseId);
    }

    private void SeeAll_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
            (App.MainWindow as MainWindow)?.ShowSection(tag);
    }

    private void Tile_PointerEntered(object sender, PointerRoutedEventArgs e)
        => TrackMenu.SetHoverPlateVisible(sender, true);

    private void Tile_PointerExited(object sender, PointerRoutedEventArgs e)
        => TrackMenu.SetHoverPlateVisible(sender, false);

    private void CollectionTile_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        TrackMenu.SetHoverPlateVisible(sender, true);
        TrackMenu.SetHoverActionsVisible(sender, true);
    }

    private void CollectionTile_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        TrackMenu.SetHoverPlateVisible(sender, false);
        TrackMenu.SetHoverActionsVisible(sender, false);
    }

    private async void PlayPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Playlist playlist })
            await ViewModel.PlayPlaylistAsync(playlist);
    }

    private async void PlayAlbum_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AlbumResult album })
            await ViewModel.PlayAlbumAsync(album);
    }

    private async void Artist_Click(object sender, RoutedEventArgs e)
    {
        if (_findingArtist || sender is not FrameworkElement { DataContext: Track track })
            return;

        if (track.Artist is not { Length: > 0 } artist)
            return;

        var name = PrimaryArtist(artist);
        if (name.Length < 2)
            return;

        _findingArtist = true;
        try
        {
            var matches = await App.Services.GetRequiredService<IYtDlpService>().SearchArtistsAsync(name, 4);
            var hit = matches.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
                      ?? matches.FirstOrDefault();

            if (hit?.BrowseId is { Length: > 0 } browseId && App.MainWindow is MainWindow window)
                window.ShowArtist(browseId);
        }
        catch
        {
        }
        finally
        {
            _findingArtist = false;
        }
    }

    private void Artist_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (ArtistLabel(sender) is { } text)
        {
            text.TextDecorations = TextDecorations.Underline;
            text.Opacity = 1.0;
        }
    }

    private void Artist_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (ArtistLabel(sender) is { } text)
        {
            text.TextDecorations = TextDecorations.None;
            text.Opacity = 0.7;
        }
    }

    private static TextBlock? ArtistLabel(object sender)
        => sender is ContentControl { Content: TextBlock text } ? text : sender as TextBlock;

    private static string PrimaryArtist(string artist)
    {
        var flat = artist;
        foreach (var marker in FeatureMarkers)
        {
            var at = flat.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at > 0)
                flat = flat[..at];
        }

        var cut = flat.IndexOfAny(ArtistSeparators);
        if (cut > 0)
            flat = flat[..cut];

        return flat.Trim().TrimEnd('.').Trim();
    }

    private void TrackTile_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        TrackMenu.SetHoverPlateVisible(sender, true);
        TrackMenu.SetHoverActionsVisible(sender, true);
    }

    private void TrackTile_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        TrackMenu.SetHoverPlateVisible(sender, false);
        TrackMenu.SetHoverActionsVisible(sender, false);
    }

    private void RowActions_Click(object sender, RoutedEventArgs e)
        => TrackMenu.ShowForSender(sender);
}
