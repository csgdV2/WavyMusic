using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MusicApp.Controls;
using MusicApp.Core.Models;
using MusicApp.Services;
using MusicApp.ViewModels;
using MusicApp.Views;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace MusicApp;

public sealed partial class MainWindow : Window
{

    public PlayerViewModel Player { get; }

    private readonly SearchViewModel _search;
    private readonly PlaylistService _playlists;
    private readonly FavoritesService _favorites;
    private readonly SettingsService _settings;

    private readonly AmbientBackdrop _ambient;

    private readonly SystemMediaControls? _systemControls;
    private readonly MediaKeys? _mediaKeys;

    private RectInt32[] _passthroughRects = Array.Empty<RectInt32>();

    private readonly DispatcherTimer _placementSave = new() { Interval = TimeSpan.FromMilliseconds(400) };

    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(160) };

    private readonly List<NavigationViewItem> _playlistNavItems = new();
    private readonly List<NavigationViewItem> _albumNavItems = new();
    private bool _playlistsExpanded = true;
    private bool _albumsExpanded = true;

    private static readonly Thickness ChildIndent = new(28, 0, 0, 0);

    private FrameworkElement? _brandBack;
    private int _brandLookups;
    private Thickness _brandOffset = new(-1);

    public MainWindow()
    {
        Player = App.Services.GetRequiredService<PlayerViewModel>();
        _search = App.Services.GetRequiredService<SearchViewModel>();
        _playlists = App.Services.GetRequiredService<PlaylistService>();
        _favorites = App.Services.GetRequiredService<FavoritesService>();
        _settings = App.Services.GetRequiredService<SettingsService>();
        InitializeComponent();

        SystemBackdrop = new MicaBackdrop();
        Title = "Wavy";

        RestorePlacement();
        AppWindow.Changed += OnAppWindowChanged;
        _placementSave.Tick += (_, _) => { _placementSave.Stop(); SavePlacement(); };
        Closed += (_, _) => { _placementSave.Stop(); SavePlacement(); Shutdown(); };

        _ambient = new AmbientBackdrop(Ambient, AmbientBack, AmbientFront, AmbientScrim) { IsEnabled = _settings.AmbientBackground };
        ApplyTheme();
        _ambient.Show(Player.CurrentTrack);

        Player.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PlayerViewModel.CurrentTrack))
                _ambient.Show(Player.CurrentTrack);
        };

        _settings.Changed += (_, name) =>
        {
            if (name is nameof(SettingsService.Theme) or nameof(SettingsService.AccentColor))
                ApplyTheme();

            if (name == nameof(SettingsService.MenuLayout))
                ApplyMenuLayout();

            if (name == nameof(SettingsService.AmbientBackground))
            {
                _ambient.IsEnabled = _settings.AmbientBackground;
                ApplyTheme();
                _ambient.Show(Player.CurrentTrack);
            }
        };

        SyncPlaylistNavItems();
        _playlists.Playlists.CollectionChanged += (_, _) => SyncNavChildren();
        _playlists.PlaylistChanged += (_, playlist) => RenamePlaylistNavItem(playlist);
        SyncAlbumNavItems();
        _favorites.Albums.CollectionChanged += (_, _) => SyncNavChildren();

        ExtendsContentIntoTitleBar = true;
        ConfigureCaptionButtons();
        ApplyMenuLayout();

        NowPlaying.Loaded += (_, _) => UpdateTitleBarPassthrough();
        NowPlaying.LayoutUpdated += (_, _) => UpdateTitleBarPassthrough();

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath))
        {
            try { AppWindow.SetIcon(iconPath); }
            catch {  }
        }

        UpdateSearchItemVisibility();

        _searchDebounce.Tick += async (_, _) =>
        {
            _searchDebounce.Stop();
            await _search.SearchAsync(SearchSuggest.Text);
        };

        ContentFrame.Navigated += (_, _) => Nav.IsBackEnabled = ContentFrame.CanGoBack;

        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), handledEventsToo: true);

        RootGrid.PreviewKeyDown += OnPreviewKeyDown;

        Nav.Loaded += (_, _) =>
        {

            Nav.IsPaneOpen = _settings.OpenSidebarOnStartup;
            SyncNavChildren();
            PlaceBrand();
            UpdateSearchItemVisibility();
        };
        Nav.LayoutUpdated += (_, _) => PlaceBrand();

        _systemControls = SystemMediaControls.Attach(this, Player);

        _mediaKeys = MediaKeys.Attach(this, Player);

        ContentFrame.Navigate(typeof(HomePage));
    }

    private void Shutdown()
    {
        _mediaKeys?.Detach();
        _systemControls?.Detach();

        try
        {
            App.Services.GetRequiredService<PlaybackService>().Stop();
        }
        catch
        {
        }

        (App.Services as IDisposable)?.Dispose();
        Environment.Exit(0);
    }

    private void ApplyTheme() =>
        ThemeManager.Apply(this, RootGrid, _settings.Theme, _settings.AccentColor, _settings.AmbientBackground);

    #region Window placement

    private void RestorePlacement()
    {
        if (_settings.Placement is not { } saved)
            return;

        var rect = new RectInt32(saved.X, saved.Y, saved.Width, saved.Height);
        var work = DisplayArea.GetFromRect(rect, DisplayAreaFallback.Nearest)?.WorkArea;
        if (work is { } screen)
        {

            rect.Width = Math.Min(rect.Width, screen.Width);
            rect.Height = Math.Min(rect.Height, screen.Height);
            rect.X = Math.Clamp(rect.X, screen.X, screen.X + screen.Width - rect.Width);
            rect.Y = Math.Clamp(rect.Y, screen.Y, screen.Y + screen.Height - rect.Height);
        }

        AppWindow.MoveAndResize(rect);
        if (saved.Maximized && AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange)
            _placementSave.Start();
    }

    private void SavePlacement()
    {
        if (AppWindow is null)
            return;

        var state = (AppWindow.Presenter as OverlappedPresenter)?.State;
        if (state == OverlappedPresenterState.Minimized)
            return;

        var maximized = state == OverlappedPresenterState.Maximized;
        if (maximized && _settings.Placement is { } previous)
        {
            _settings.Placement = previous with { Maximized = true };
            return;
        }

        var position = AppWindow.Position;
        var size = AppWindow.Size;
        if (size.Width <= 0 || size.Height <= 0)
            return;

        _settings.Placement = new WindowPlacement(position.X, position.Y, size.Width, size.Height, maximized);
    }

    #endregion

    private void ConfigureCaptionButtons()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
            return;

        var titleBar = AppWindow.TitleBar;
        titleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        var dark = RootGrid.ActualTheme == ElementTheme.Dark
                   || (RootGrid.ActualTheme == ElementTheme.Default
                       && Application.Current.RequestedTheme == ApplicationTheme.Dark);

        var glyph = dark ? Colors.White : Color.FromArgb(255, 26, 26, 26);
        var dim = dark ? Color.FromArgb(255, 160, 160, 160) : Color.FromArgb(255, 120, 120, 120);
        var hover = dark ? Color.FromArgb(38, 255, 255, 255) : Color.FromArgb(28, 0, 0, 0);
        var pressed = dark ? Color.FromArgb(64, 255, 255, 255) : Color.FromArgb(48, 0, 0, 0);

        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = glyph;
        titleBar.ButtonInactiveForegroundColor = dim;
        titleBar.ButtonHoverBackgroundColor = hover;
        titleBar.ButtonHoverForegroundColor = glyph;
        titleBar.ButtonPressedBackgroundColor = pressed;
        titleBar.ButtonPressedForegroundColor = glyph;
    }

    private void ApplyMenuLayout()
    {
        var bottom = _settings.MenuLayout == MenuLayout.Bottom;

        ConfigureCaptionButtons();

        Grid.SetRow(NowPlaying, bottom ? 1 : 0);
        NowPlaying.VerticalAlignment = bottom ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
        NowPlaying.IsFloating = bottom;
        TitleStrip.Visibility = bottom ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetRow(TitleStrip, bottom ? 1 : 0);

        var corner = bottom ? new CornerRadius(12, 0, 0, 0) : new CornerRadius(0);
        LyricsSide.PanelCorner = corner;
        QueueSide.PanelCorner = corner;

        var inset = bottom ? 32.0 : 0;
        ContentArea.Margin = new Thickness(0, inset, 0, 0);

        var panelInset = bottom ? Math.Max(0, CaptionHeight() - inset) : 0;
        LyricsSide.PanelTopInset = panelInset;
        QueueSide.PanelTopInset = panelInset;

        if (bottom)
            TitleStrip.Height = CaptionHeight();

        SetTitleBar(bottom ? TitleStrip : NowPlaying);
        ClearTitleBarPassthrough();
        UpdateTitleBarPassthrough();
    }

    private double CaptionHeight()
    {
        const double nominal = 48.0;

        if (!AppWindowTitleBar.IsCustomizationSupported())
            return nominal;

        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        var height = AppWindow.TitleBar.Height / (scale <= 0 ? 1.0 : scale);
        if (height < 20 || height > nominal + 4)
            return nominal;

        return Math.Ceiling(height);
    }

    private void ClearTitleBarPassthrough()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
            return;

        _passthroughRects = Array.Empty<RectInt32>();
        try
        {
            InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
                .SetRegionRects(NonClientRegionKind.Passthrough, Array.Empty<RectInt32>());
        }
        catch
        {
        }
    }

    private void UpdateTitleBarPassthrough()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
            return;

        if (_settings.MenuLayout == MenuLayout.Bottom)
            return;

        var xamlRoot = NowPlaying.XamlRoot;
        if (xamlRoot is null)
            return;

        try
        {
            var scale = xamlRoot.RasterizationScale;
            var rects = new List<RectInt32>();
            foreach (var el in NowPlaying.InteractiveRegions)
            {
                if (el is null || el.ActualWidth <= 0 || el.ActualHeight <= 0)
                    continue;

                var bounds = el.TransformToVisual(null)
                               .TransformBounds(new Windows.Foundation.Rect(0, 0, el.ActualWidth, el.ActualHeight));
                rects.Add(new RectInt32(
                    (int)Math.Round(bounds.X * scale),
                    (int)Math.Round(bounds.Y * scale),
                    (int)Math.Round(bounds.Width * scale),
                    (int)Math.Round(bounds.Height * scale)));
            }

            if (rects.Count == 0 || SameRects(rects, _passthroughRects))
                return;
            _passthroughRects = rects.ToArray();

            InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
                .SetRegionRects(NonClientRegionKind.Passthrough, _passthroughRects);
        }
        catch
        {

        }
    }

    private static bool SameRects(List<RectInt32> a, RectInt32[] b)
    {
        if (a.Count != b.Length)
            return false;
        for (int i = 0; i < b.Length; i++)
            if (a[i].X != b[i].X || a[i].Y != b[i].Y || a[i].Width != b[i].Width || a[i].Height != b[i].Height)
                return false;
        return true;
    }

    private void PlaceBrand()
    {
        if (!Nav.IsPaneOpen)
        {
            Brand.Visibility = Visibility.Collapsed;
            return;
        }

        if (_brandBack is null && _brandLookups < 40)
        {
            _brandLookups++;
            _brandBack = FindPart(Nav, "NavigationViewBackButton");
        }

        if (_brandBack is null || _brandBack.Visibility != Visibility.Visible ||
            _brandBack.ActualWidth <= 0 || _brandBack.ActualHeight <= 0)
        {
            Brand.Visibility = Visibility.Collapsed;
            return;
        }

        var bounds = _brandBack.TransformToVisual(RootGrid)
                               .TransformBounds(new Windows.Foundation.Rect(
                                   0, 0, _brandBack.ActualWidth, _brandBack.ActualHeight));

        Brand.Visibility = Visibility.Visible;
        var offset = new Thickness(Math.Round(bounds.Right + 8), Math.Round(bounds.Y), 0, 0);

        if (offset.Left == _brandOffset.Left && offset.Top == _brandOffset.Top)
            return;

        _brandOffset = offset;
        Brand.Margin = offset;
        Brand.Height = bounds.Height;
    }

    private static FrameworkElement? FindPart(DependencyObject root, string name)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element && element.Name == name)
                return element;

            if (FindPart(child, name) is { } found)
                return found;
        }

        return null;
    }

    private void Nav_DisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
        => UpdateSearchItemVisibility();

    private void Nav_PaneClosed(NavigationView sender, object args)
    {
        UpdateSearchItemVisibility();
        PlaceBrand();
        SyncNavChildren();
    }

    private void Nav_PaneOpened(NavigationView sender, object args)
    {
        UpdateSearchItemVisibility();
        SyncNavChildren();

        var query = _search.Query ?? string.Empty;
        if (SearchSuggest.Text != query)
            SearchSuggest.Text = query;
    }

    public static bool IsNavPaneCollapsed { get; private set; }

    public static event EventHandler? NavPaneStateChanged;

    private void UpdateSearchItemVisibility()
    {
        var minimal = Nav.DisplayMode == NavigationViewDisplayMode.Minimal && !Nav.IsPaneOpen;

        SearchNavItem.Visibility = minimal ? Visibility.Visible : Visibility.Collapsed;

        var collapsed = !Nav.IsPaneOpen;
        if (IsNavPaneCollapsed == collapsed)
            return;

        IsNavPaneCollapsed = collapsed;
        NavPaneStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ShowPage(typeof(SettingsPage));
            return;
        }
        NavigateTo(args.SelectedItemContainer as NavigationViewItem);
    }

    private void Nav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            ClearSearch();
            ShowPage(typeof(SettingsPage));
            return;
        }

        var invoked = args.InvokedItemContainer as NavigationViewItem;
        if (invoked?.Tag as string != "search")
            ClearSearch();

        ToggleSection(invoked?.Tag as string);
        NavigateTo(invoked);
    }

    private void ClearSearch()
    {
        _searchDebounce.Stop();
        SearchSuggest.Text = string.Empty;
        _search.Clear();
    }

    private void NavigateTo(NavigationViewItem? item)
    {
        switch (item?.Tag)
        {
            case Playlist playlist:
                ShowPlaylist(playlist);
                break;
            case AlbumResult album:
                ShowAlbum(album);
                break;
            case string tag:
                GoToSection(tag);
                break;
        }
    }

    public void ShowSection(string tag)
    {
        GoToSection(tag);

        foreach (var item in Nav.MenuItems)
        {
            if (item is NavigationViewItem candidate && candidate.Tag as string == tag)
            {
                Nav.SelectedItem = candidate;
                break;
            }
        }
    }

    private void GoToSection(string? tag) => ShowPage(tag switch
    {
        "search" => typeof(SearchPage),
        "history" => typeof(LibraryPage),
        "playlists" => typeof(PlaylistsPage),
        "albums" => typeof(AlbumsPage),
        _ => typeof(HomePage),
    });

    private void ShowPage(Type pageType)
    {
        if (ContentFrame.CurrentSourcePageType == pageType)
            return;

        ContentFrame.Navigate(pageType);
        ContentFrame.BackStack.Clear();
        Nav.IsBackEnabled = false;
    }

    private void ShowPlaylist(Playlist playlist)
    {
        ShowPage(typeof(PlaylistsPage));
        if (ContentFrame.Content is PlaylistsPage page)
            page.Select(playlist);
    }

    private void ShowAlbum(AlbumResult album)
    {
        ContentFrame.Navigate(typeof(AlbumPage), album.BrowseId);
        ContentFrame.BackStack.Clear();
        Nav.IsBackEnabled = false;
    }

    public void ShowArtist(string browseId)
    {
        ContentFrame.Navigate(typeof(ArtistPage), browseId);
        ContentFrame.BackStack.Clear();
        Nav.IsBackEnabled = false;
    }

    private void SyncPlaylistNavItems()
    {
        foreach (var item in _playlistNavItems)
            Nav.MenuItems.Remove(item);
        _playlistNavItems.Clear();

        var at = Nav.MenuItems.IndexOf(PlaylistsNavItem) + 1;
        foreach (var playlist in _playlists.Playlists)
        {
            var item = new NavigationViewItem
            {
                Content = PlaylistNavContent(playlist),
                Tag = playlist,

                Margin = ChildIndent,

            };
            Nav.MenuItems.Insert(at++, item);
            _playlistNavItems.Add(item);
        }

        ApplyChildVisibility();
    }

    private void SyncAlbumNavItems()
    {
        foreach (var item in _albumNavItems)
            Nav.MenuItems.Remove(item);
        _albumNavItems.Clear();

        var at = Nav.MenuItems.IndexOf(AlbumsNavItem) + 1;
        foreach (var album in _favorites.Albums)
        {
            var item = new NavigationViewItem
            {
                Content = AlbumNavContent(album),
                Tag = album,
                Margin = ChildIndent,
            };

            if (!string.IsNullOrWhiteSpace(album.Artist))
                ToolTipService.SetToolTip(item, album.Artist);
            Nav.MenuItems.Insert(at++, item);
            _albumNavItems.Add(item);
        }

        ApplyChildVisibility();
    }

    private void ApplyChildVisibility()
    {
        var playlists = Nav.IsPaneOpen && _playlistsExpanded ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in _playlistNavItems)
            item.Visibility = playlists;

        var albums = Nav.IsPaneOpen && _albumsExpanded ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in _albumNavItems)
            item.Visibility = albums;

        if (Nav.SelectedItem is not NavigationViewItem selected)
            return;
        if (playlists == Visibility.Collapsed && _playlistNavItems.Contains(selected))
            Nav.SelectedItem = PlaylistsNavItem;
        else if (albums == Visibility.Collapsed && _albumNavItems.Contains(selected))
            Nav.SelectedItem = AlbumsNavItem;
    }

    private void ToggleSection(string? tag)
    {
        switch (tag)
        {
            case "playlists" when _playlists.Playlists.Count > 0:
                _playlistsExpanded = !_playlistsExpanded;
                ApplyChildVisibility();
                break;
            case "albums" when _favorites.Albums.Count > 0:
                _albumsExpanded = !_albumsExpanded;
                ApplyChildVisibility();
                break;
        }
    }

    private void SyncNavChildren()
    {
        SyncPlaylistNavItems();
        SyncAlbumNavItems();
    }

    private static StackPanel NavContent(UIElement cover, string text)    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        panel.Children.Add(cover);
        panel.Children.Add(new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        });
        return panel;
    }

    private static StackPanel PlaylistNavContent(Playlist playlist)
        => NavContent(new PlaylistCover { Playlist = playlist, Size = 20, Radius = 4 }, playlist.Name);

    private static StackPanel AlbumNavContent(AlbumResult album)
    {
        var frame = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(4),
        };

        if (Application.Current.Resources.TryGetValue("ControlAltFillColorSecondaryBrush", out var fill) &&
            fill is Brush brush)
        {
            frame.Background = brush;
        }

        if (!string.IsNullOrWhiteSpace(album.ThumbnailUrl) &&
            Uri.TryCreate(album.ThumbnailUrl, UriKind.Absolute, out _))
        {
            frame.Child = new Image
            {
                Stretch = Stretch.UniformToFill,
                Source = Converters.DecodedImageConverter.Load(album.ThumbnailUrl, 40),
            };
        }

        return NavContent(frame, album.Title);
    }

    private void RenamePlaylistNavItem(Playlist playlist)
    {
        foreach (var child in _playlistNavItems)
            if (child is NavigationViewItem item && ReferenceEquals(item.Tag, playlist))
            {
                item.Content = PlaylistNavContent(playlist);
                return;
            }
    }

    private void Nav_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack)
            ContentFrame.GoBack();
    }

    private void SearchSuggest_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {

        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;

        _searchDebounce.Stop();
        if (string.IsNullOrWhiteSpace(sender.Text))
        {

            _search.Clear();
            return;
        }

        ShowSearchPage();
        _searchDebounce.Start();
    }

    private async void SearchSuggest_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _searchDebounce.Stop();
        ShowSearchPage();
        await _search.SearchAsync(args.QueryText);
    }

    private void ShowSearchPage()
    {
        Nav.SelectedItem = SearchNavItem;
        GoToSection("search");
    }

    private void Status_CloseButtonClick(InfoBar sender, object args) => Player.ClearStatus();

    #region Keyboard shortcuts

    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var focused = FocusManager.GetFocusedElement(RootGrid.XamlRoot);
        bool typing = focused is TextBox or AutoSuggestBox or RichEditBox or PasswordBox;
        bool ctrl = IsDown(VirtualKey.Control);
        bool shift = IsDown(VirtualKey.Shift);

        switch (e.Key)
        {

            case (VirtualKey)0xB3 when MediaCommandGate.TryClaim(MediaCommandGate.PlayPause):
                Player.PlayPauseCommand.Execute(null);
                break;
            case (VirtualKey)0xB0 when MediaCommandGate.TryClaim(MediaCommandGate.Next):
                await Player.NextCommand.ExecuteAsync(null);
                break;
            case (VirtualKey)0xB1 when MediaCommandGate.TryClaim(MediaCommandGate.Previous):
                await Player.PreviousCommand.ExecuteAsync(null);
                break;

            case VirtualKey.Space:

                return;

            case VirtualKey.Right when ctrl:
                if (typing)
                    return;
                await Player.NextCommand.ExecuteAsync(null);
                break;
            case VirtualKey.Left when ctrl:
                if (typing)
                    return;
                await Player.PreviousCommand.ExecuteAsync(null);
                break;

            case VirtualKey.Right when shift:
            case VirtualKey.Left when shift:
                if (typing)
                    return;
                Player.SeekBy(e.Key == VirtualKey.Right ? 10 : -10);
                break;

            case VirtualKey.Up when ctrl:
            case VirtualKey.Down when ctrl:

                if (typing || focused is Slider)
                    return;
                Player.AdjustVolume(e.Key == VirtualKey.Up ? 0.05 : -0.05);
                break;

            case VirtualKey.F when ctrl:
                FocusSearchBox();
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Space)
            return;

        var focused = FocusManager.GetFocusedElement(RootGrid.XamlRoot);
        if (focused is TextBox or AutoSuggestBox or RichEditBox or PasswordBox)
            return;

        if (IsDown(VirtualKey.Control))
        {
            Nav.IsPaneOpen = !Nav.IsPaneOpen;
            e.Handled = true;
            return;
        }

        Player.PlayPauseCommand.Execute(null);
        e.Handled = true;
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void FocusSearchBox()
    {
        if (Nav.DisplayMode == NavigationViewDisplayMode.Minimal && !Nav.IsPaneOpen)
            Nav.IsPaneOpen = true;
        SearchSuggest.Focus(FocusState.Programmatic);
    }

    #endregion
}
