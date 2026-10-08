using System.ComponentModel;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MusicApp.Controls;
using MusicApp.Core.Models;
using MusicApp.Services;
using MusicApp.ViewModels;

namespace MusicApp.Views;

public sealed partial class SearchPage : Page
{
    public SearchViewModel ViewModel { get; }
    private readonly PlayerViewModel _player;
    private readonly SettingsService _settings;

    public SearchPage()
    {
        ViewModel = App.Services.GetRequiredService<SearchViewModel>();
        _player = App.Services.GetRequiredService<PlayerViewModel>();
        _settings = App.Services.GetRequiredService<SettingsService>();
        InitializeComponent();
        RefreshSearchHistory();

        SelectFilterItem(ViewModel.SelectedFilter);
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        MainWindow.NavPaneStateChanged += OnNavPaneStateChanged;
        _settings.Changed += OnSettingsChanged;

        Loaded += (_, _) => SyncInlineSearch();

        Unloaded += (_, _) =>
        {
            _debounce?.Stop();
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            MainWindow.NavPaneStateChanged -= OnNavPaneStateChanged;
            _settings.Changed -= OnSettingsChanged;
        };
    }

    private DispatcherQueueTimer? _debounce;

    private void OnNavPaneStateChanged(object? sender, EventArgs e) => SyncInlineSearch();

    private void OnSettingsChanged(object? sender, string name)
    {
        if (name == nameof(SettingsService.SearchHistory))
            RefreshSearchHistory();
    }

    private void RefreshSearchHistory()
    {
        var history = _settings.SearchHistory.ToArray();
        InlineSearch.ItemsSource = history;
        if (history.Length > 0 && string.IsNullOrWhiteSpace(InlineSearch.Text)
            && InlineSearch.FocusState != FocusState.Unfocused)
            InlineSearch.IsSuggestionListOpen = true;
    }

    private void InlineSearch_GotFocus(object sender, RoutedEventArgs e)
    {
        InlineSearch.DispatcherQueue.TryEnqueue(() =>
        {
            if (string.IsNullOrWhiteSpace(InlineSearch.Text) && _settings.SearchHistory.Count > 0)
                InlineSearch.IsSuggestionListOpen = true;
        });
    }

    private async void InlineSearch_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is not string query)
            return;
        sender.Text = query;
        _debounce?.Stop();
        await ViewModel.SearchAsync(query, recordInHistory: true);
    }

    private void HistorySuggestion_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid row && row.Children.OfType<Button>().FirstOrDefault() is { } removeButton)
            removeButton.Visibility = Visibility.Visible;
    }

    private void HistorySuggestion_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid row && row.Children.OfType<Button>().FirstOrDefault() is { } removeButton)
            removeButton.Visibility = Visibility.Collapsed;
    }

    private void RemoveSearchHistory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: string query } button)
            _settings.RemoveSearchHistory(query);
    }

    private void SyncInlineSearch()
    {
        InlineSearch.Visibility = Visibility.Visible;
        var query = ViewModel.Query ?? string.Empty;
        if (InlineSearch.Text != query)
            InlineSearch.Text = query;
    }

    public void FocusSearchBox() => InlineSearch.Focus(FocusState.Programmatic);

    private void SearchHeader_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var available = Math.Max(120, e.NewSize.Width - PageTitle.ActualWidth - SearchHeader.ColumnSpacing);
        InlineSearch.Width = Math.Min(480, available);
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
            if (_settings.SearchHistory.Count > 0)
                sender.IsSuggestionListOpen = true;
            return;
        }

        _debounce.Start();
    }

    private DispatcherQueueTimer CreateDebounce()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(450);
        timer.IsRepeating = false;
        timer.Tick += async (_, _) => await ViewModel.SearchAsync(InlineSearch.Text, recordInHistory: true);
        return timer;
    }

    private async void InlineSearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _debounce?.Stop();
        await ViewModel.SearchAsync(args.QueryText, recordInHistory: true);
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
