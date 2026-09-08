using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using MusicApp.Controls;
using MusicApp.Core.Models;
using MusicApp.ViewModels;

namespace MusicApp.Views;

public sealed partial class AlbumPage : Page
{
    public AlbumViewModel ViewModel { get; }

    private readonly PlayerViewModel _player;

    public AlbumPage()
    {
        ViewModel = App.Services.GetRequiredService<AlbumViewModel>();
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

    private Task PlayFromAsync(int index)
    {
        if (ViewModel.Album is not { HasTracks: true } album)
            return Task.CompletedTask;

        var tracks = album.Tracks.ToList();
        return _player.PlayFromAsync(tracks, index < 0 || index >= tracks.Count ? 0 : index);
    }

    private async void Play_Click(object sender, RoutedEventArgs e) => await PlayFromAsync(0);

    private void Favorite_Click(object sender, RoutedEventArgs e) => ViewModel.ToggleFavorite();

    private async void Tracks_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (ViewModel.Album is not { } album || e.ClickedItem is not Track track)
            return;

        await PlayFromAsync(album.Tracks.ToList().IndexOf(track));
    }

    private void Artist_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Album?.ArtistBrowseId is { Length: > 0 } browseId)
            Frame.Navigate(typeof(ArtistPage), browseId);
    }

    private void Row_PointerEntered(object sender, PointerRoutedEventArgs e)
        => TrackMenu.SetHoverActionsVisible(sender, true);

    private void Row_PointerExited(object sender, PointerRoutedEventArgs e)
        => TrackMenu.SetHoverActionsVisible(sender, false);

    private void RowActions_Click(object sender, RoutedEventArgs e)
        => TrackMenu.ShowForSender(sender);
}
