using System.ComponentModel;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MusicApp.Controls;
using MusicApp.Core.Models;
using MusicApp.ViewModels;

namespace MusicApp.Views;

public sealed partial class SearchPage : Page
{
    public SearchViewModel ViewModel { get; }
    private readonly PlayerViewModel _player;

    public SearchPage()
    {
        ViewModel = App.Services.GetRequiredService<SearchViewModel>();
        _player = App.Services.GetRequiredService<PlayerViewModel>();
        InitializeComponent();

        SelectFilterItem(ViewModel.SelectedFilter);
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        MainWindow.NavPaneStateChanged += OnNavPaneStateChanged;

        Loaded += (_, _) => SyncInlineSearch();

        Unloaded += (_, _) =>
        {
            _debounce?.Stop();
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            MainWindow.NavPaneStateChanged -= OnNavPaneStateChanged;
        };
    }

    private DispatcherQueueTimer? _debounce;

    private void OnNavPaneStateChanged(object? sender, EventArgs e) => SyncInlineSearch();

    private void SyncInlineSearch()
    {
        var show = MainWindow.IsNavPaneCollapsed;
        InlineSearch.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        if (!show)
        {
            _debounce?.Stop();
            return;
        }

        var query = ViewModel.Query ?? string.Empty;
        if (InlineSearch.Text != query)
            InlineSearch.Text = query;
    }

    private void InlineSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;

        _debounce ??= CreateDebounce();
        _debounce.Stop();

        if (string.IsNullOrWhiteSpace(sender.Text))
        {
            ViewModel.Clear();
            return;
        }

        _debounce.Start();
    }

    private DispatcherQueueTimer CreateDebounce()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(450);
        timer.IsRepeating = false;
        timer.Tick += async (_, _) => await ViewModel.SearchAsync(InlineSearch.Text);
        return timer;
    }

    private async void InlineSearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _debounce?.Stop();
        await ViewModel.SearchAsync(args.QueryText);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchViewModel.SelectedFilter))
            SelectFilterItem(ViewModel.SelectedFilter);

        if (e.PropertyName == nameof(SearchViewModel.HasQuery)
            && !ViewModel.HasQuery
            && InlineSearch.Text.Length > 0)
        {
            _debounce?.Stop();
            InlineSearch.Text = string.Empty;
        }
    }

    private void Filters_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is string tag && Enum.TryParse<SearchFilter>(tag, out var filter))
            ViewModel.SelectedFilter = filter;
    }

    private void SelectFilterItem(SearchFilter filter)
    {
        foreach (var item in Filters.Items)
            item.IsSelected = item.Tag as string == filter.ToString();
    }

    private async void Results_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Track track)
            await _player.PlayFromAsync(new[] { track }, 0);
    }

    private void Artists_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ArtistResult artist)
            Frame.Navigate(typeof(ArtistPage), artist.BrowseId);
    }

    private void Albums_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AlbumResult album)
            Frame.Navigate(typeof(AlbumPage), album.BrowseId);
    }

    private void Playlists_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlaylistResult playlist)
            Frame.Navigate(typeof(OnlinePlaylistPage), playlist.BrowseId);
    }

    private void Row_PointerEntered(object sender, PointerRoutedEventArgs e)
        => TrackMenu.SetHoverActionsVisible(sender, true);

    private void Row_PointerExited(object sender, PointerRoutedEventArgs e)
        => TrackMenu.SetHoverActionsVisible(sender, false);

    private void RowActions_Click(object sender, RoutedEventArgs e)
        => TrackMenu.ShowForSender(sender);

    private void Tile_PointerEntered(object sender, PointerRoutedEventArgs e)
        => TrackMenu.SetHoverPlateVisible(sender, true);

    private void Tile_PointerExited(object sender, PointerRoutedEventArgs e)
        => TrackMenu.SetHoverPlateVisible(sender, false);
}
