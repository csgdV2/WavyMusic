using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using MusicApp.Controls;
using MusicApp.Core.Models;
using MusicApp.ViewModels;

namespace MusicApp.Views;

public sealed partial class ArtistPage : Page
{
    public ArtistViewModel ViewModel { get; }

    private readonly PlayerViewModel _player;

    public ArtistPage()
    {
        ViewModel = App.Services.GetRequiredService<ArtistViewModel>();
        _player = App.Services.GetRequiredService<PlayerViewModel>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = ViewModel.LoadAsync(e.Parameter as string);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        ViewModel.Cancel();
    }

    private async void Songs_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (ViewModel.Artist is null || e.ClickedItem is not Track track)
            return;

        var songs = ViewModel.AllSongs.ToList();
        if (songs.Count == 0)
            return;

        var index = songs.FindIndex(t => t.Id == track.Id);
        await _player.PlayFromAsync(songs, index < 0 ? 0 : index, lockQueue: true);
    }

    private void ShowMore_Click(object sender, RoutedEventArgs e) => ViewModel.ShowAllSongs();

    private void Similar_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ArtistResult artist)
            Frame.Navigate(typeof(ArtistPage), artist.BrowseId);
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        var songs = ViewModel.AllSongs.ToList();
        if (songs.Count > 0)
            await _player.PlayFromAsync(songs, 0, lockQueue: true);
    }

    private async void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        var songs = ViewModel.AllSongs.ToList();
        if (songs.Count > 0)
            await _player.PlayShuffledAsync(songs, lockQueue: true);
    }

    private void Releases_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AlbumResult release)
            Frame.Navigate(typeof(AlbumPage), release.BrowseId);
    }

    private void Tile_PointerEntered(object sender, PointerRoutedEventArgs e)
        => TrackMenu.SetHoverPlateVisible(sender, true);

    private void Tile_PointerExited(object sender, PointerRoutedEventArgs e)
        => TrackMenu.SetHoverPlateVisible(sender, false);
}
