using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Wavy.Controls;
using Wavy.Core.Models;
using Wavy.Services;
using Wavy.ViewModels;

namespace Wavy.Views;

public sealed partial class SearchPage : Page
{
    public SearchViewModel ViewModel { get; }
    private readonly PlayerViewModel _player;
    private readonly SettingsService _settings;
    private readonly ObservableCollection<string> _history = new();

    public SearchPage()
    {
        ViewModel = App.Services.GetRequiredService<SearchViewModel>();
        _player = App.Services.GetRequiredService<PlayerViewModel>();
        _settings = App.Services.GetRequiredService<SettingsService>();
        InitializeComponent();
        HistoryItems.ItemsSource = _history;
        InlineSearch.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(InlineSearch_PointerPressed), true);
        PageRoot.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(PageRoot_PointerPressed), true);
        InlineSearch.SizeChanged += (_, _) => SizeSearchButtons(InlineSearch);
        InlineSearch.ActualThemeChanged += (_, _) => DispatcherQueue.TryEnqueue(() => SizeSearchButtons(InlineSearch));
        RefreshSearchHistory();

        SelectFilterItem(ViewModel.SelectedFilter);
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _settings.Changed += OnSettingsChanged;

        Loaded += (_, _) => { SizeSearchButtons(InlineSearch); SyncInlineSearch(); ViewModel.RefreshRecent(); };

        Unloaded += (_, _) =>
        {
            _debounce?.Stop();
            HistoryPopup.IsOpen = false;
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _settings.Changed -= OnSettingsChanged;
        };
    }

    private static void SizeSearchButtons(DependencyObject parent)
    {
        if (parent is TextBox box) SearchFieldAlignment.Align(box);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Button button && button.Name is "DeleteButton" or "QueryButton")
            {
                button.Width = button.Height = 28;
                button.MinWidth = button.MinHeight = 0;
                button.Padding = new Thickness(0);
                button.Margin = new Thickness(2, 0, 2, 0);
                button.CornerRadius = new CornerRadius(8);
                button.VerticalAlignment = VerticalAlignment.Center;
            }
            if (child is Grid { Name: "ButtonLayoutGrid" } plate)
            {
                plate.Width = plate.Height = 28;
                plate.Margin = new Thickness(0);
                plate.CornerRadius = new CornerRadius(8);
                plate.VerticalAlignment = VerticalAlignment.Center;
                plate.HorizontalAlignment = HorizontalAlignment.Center;
            }
            SizeSearchButtons(child);
        }
    }

    private DispatcherQueueTimer? _debounce;
    private bool _removingHistory;
    private bool _suppressHistoryFocus;
    private bool _filterHistoryByText;


    private void OnSettingsChanged(object? sender, string name)
    {
        if (name == nameof(SettingsService.SearchHistory))
            RefreshSearchHistory();
    }

    private void RefreshSearchHistory()
    {
        var filter = _filterHistoryByText ? InlineSearch.Text?.Trim() ?? string.Empty : string.Empty;
        var history = _settings.SearchHistory.Where(query => query.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        for (var i = _history.Count - 1; i >= 0; i--)
            if (!history.Contains(_history[i])) _history.RemoveAt(i);
        for (var i = 0; i < history.Length; i++)
        {
            if (i < _history.Count && _history[i] == history[i]) continue;
            var oldIndex = _history.IndexOf(history[i]);
            if (oldIndex >= 0) _history.Move(oldIndex, i); else _history.Insert(i, history[i]);
        }
        HistoryEmptyText.Visibility = _history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryEmptyText.Text = filter.Length == 0 ? "No recent searches" : "No matching recent searches";
        if (HistoryPopup.IsOpen) PositionHistory();
    }

    private void InlineSearch_GotFocus(object sender, RoutedEventArgs e)
    {
        if (_removingHistory || _suppressHistoryFocus) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (XamlRoot is not null && IsInside(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, InlineSearch)) OpenHistory();
        });
    }

    private void InlineSearch_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() => { if (IsLoaded && !_suppressHistoryFocus) OpenHistory(); });
    }

    private void OpenHistory(bool allowEmpty = true, bool filterByText = false)
    {
        _filterHistoryByText = filterByText;
        RefreshSearchHistory();
        if (!allowEmpty && _history.Count == 0) { HistoryPopup.IsOpen = false; return; }
        PositionHistory();
        HistoryPopup.IsOpen = true;
    }

    private void PositionHistory()
    {
        var point = InlineSearch.TransformToVisual(SearchHeader).TransformPoint(new Windows.Foundation.Point());
        HistorySurface.Width = InlineSearch.ActualWidth;
        HistoryPopup.HorizontalOffset = point.X;
        HistoryPopup.VerticalOffset = point.Y + InlineSearch.ActualHeight + 6;
    }

    private static bool IsInside(DependencyObject? element, DependencyObject parent)
    {
        for (; element is not null; element = VisualTreeHelper.GetParent(element))
            if (ReferenceEquals(element, parent)) return true;
        return false;
    }

    private void HistoryFocus_Lost(object sender, RoutedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_removingHistory || !HistoryPopup.IsOpen || XamlRoot is null) return;
            var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            if (!IsInside(focused, InlineSearch) && !IsInside(focused, HistorySurface)) HistoryPopup.IsOpen = false;
        });
    }

    private void PageRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (HistoryPopup.IsOpen && !IsInside(e.OriginalSource as DependencyObject, InlineSearch)
            && !IsInside(e.OriginalSource as DependencyObject, HistorySurface)) HistoryPopup.IsOpen = false;
    }

    private void Search_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && HistoryPopup.IsOpen)
        {
            HistoryPopup.IsOpen = false;
            _suppressHistoryFocus = true;
            InlineSearch.Focus(FocusState.Programmatic);
            _suppressHistoryFocus = false;
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Down && ReferenceEquals(sender, InlineSearch))
        {
            OpenHistory();
            if (HistoryPopup.IsOpen && FocusManager.FindFirstFocusableElement(HistoryItems) is Control first)
                first.Focus(FocusState.Keyboard);
            e.Handled = HistoryPopup.IsOpen;
        }
    }

    private async void HistoryQuery_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: string query }) return;
        InlineSearch.Text = query;
        _debounce?.Stop();
        HistoryPopup.IsOpen = false;
        await ViewModel.SearchAsync(query, recordInHistory: true);
    }

    private void RemoveSearchHistory_Click(object sender, RoutedEventArgs e)
    {
        _removingHistory = true;
        if (sender is FrameworkElement { DataContext: string query })
            _settings.RemoveSearchHistory(query);
        DispatcherQueue.TryEnqueue(() =>
        {
            InlineSearch.Focus(FocusState.Programmatic);
            OpenHistory(allowEmpty: true);
            _removingHistory = false;
        });
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
        if (HistoryPopup is not null && HistoryPopup.IsOpen) PositionHistory();
    }

    private void InlineSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() => SizeSearchButtons(InlineSearch));
        _debounce ??= CreateDebounce();
        _debounce.Stop();

        if (string.IsNullOrWhiteSpace(sender.Text))
        {
            ViewModel.Clear();
            if (XamlRoot is not null && IsInside(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, InlineSearch)) OpenHistory();
            return;
        }

        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;
        ViewModel.CancelPendingSearch();
        OpenHistory(allowEmpty: false, filterByText: true);
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
        if (_removingHistory) return;
        HistoryPopup.IsOpen = false;
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
        {
            if (ViewModel.HasQuery) _settings.AddSearchHistory(ViewModel.Query!);
            await _player.PlayRadioAsync(track);
        }
    }

    private void FeaturedArtist_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.FeaturedArtist is { } artist)
            Frame.Navigate(typeof(ArtistPage), artist.BrowseId);
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
