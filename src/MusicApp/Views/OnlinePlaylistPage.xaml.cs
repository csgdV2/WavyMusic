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

public sealed partial class OnlinePlaylistPage : Page
{
    public OnlinePlaylistViewModel ViewModel { get; }

    private readonly PlayerViewModel _player;

    public OnlinePlaylistPage()
    {
        ViewModel = App.Services.GetRequiredService<OnlinePlaylistViewModel>();
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
        if (ViewModel.Playlist is not { Tracks.Count: > 0 } playlist)
            return Task.CompletedTask;

        var tracks = playlist.Tracks.ToList();
        return _player.PlayFromAsync(tracks, index < 0 || index >= tracks.Count ? 0 : index);
    }

    private async void Play_Click(object sender, RoutedEventArgs e) => await PlayFromAsync(0);

    private async void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Playlist is { Tracks.Count: > 0 } playlist)
            await _player.PlayShuffledAsync(playlist.Tracks.ToList());
    }

    private void Save_Click(object sender, RoutedEventArgs e) => ViewModel.SaveToLibrary();

    private async void Tracks_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (ViewModel.Playlist is not { } playlist || e.ClickedItem is not Track track)
            return;

        await PlayFromAsync(playlist.Tracks.ToList().IndexOf(track));
    }

    private void Row_PointerEntered(object sender, PointerRoutedEventArgs e)
        => TrackMenu.SetHoverActionsVisible(sender, true);

    private void Row_PointerExited(object sender, PointerRoutedEventArgs e)
        => TrackMenu.SetHoverActionsVisible(sender, false);

    private void RowActions_Click(object sender, RoutedEventArgs e)
        => TrackMenu.ShowForSender(sender);
}
