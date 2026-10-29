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
using Wavy.Controls;
using Wavy.Core.Models;
using Wavy.Services;
using Wavy.ViewModels;
using Wavy.Views;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace Wavy;

public sealed partial class MainWindow : Window
{

    public PlayerViewModel Player { get; }
    public AppUpdateService Updates { get; }

    private readonly SearchViewModel _search;
    private readonly SettingsService _settings;
    private readonly AppTheme _startupTheme;
    private bool _themeRestartDialogOpen;

    private readonly AmbientBackdrop _ambient;

    private readonly SystemMediaControls? _systemControls;
    private readonly MediaKeys? _mediaKeys;


    private readonly DispatcherTimer _placementSave = new() { Interval = TimeSpan.FromMilliseconds(400) };

    public MainWindow()
    {
        Player = App.Services.GetRequiredService<PlayerViewModel>();
        Updates = App.Services.GetRequiredService<AppUpdateService>();
        _search = App.Services.GetRequiredService<SearchViewModel>();
        _settings = App.Services.GetRequiredService<SettingsService>();
        _startupTheme = _settings.Theme;
        InitializeComponent();
        ContentArea.SizeChanged += (_, _) => UpdateSidePanelLayout();

        SystemBackdrop = new MicaBackdrop();
        Title = "Wavy";

        ConfigureMinimumSize();
        RootGrid.Loaded += (_, _) => ConfigureMinimumSize();
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
            if (args.PropertyName is nameof(PlayerViewModel.IsLyricsOpen) or nameof(PlayerViewModel.IsQueueOpen))
                UpdateSidePanelLayout();
        };

        _settings.Changed += (_, name) =>
        {
            if (name == nameof(SettingsService.Theme))
            {
                DispatcherQueue.TryEnqueue(async () => await RequireThemeRestartAsync());
                return;
            }
            if (name == nameof(SettingsService.AccentColor))
                ApplyTheme();

            if (name == nameof(SettingsService.AmbientBackground))
            {
                _ambient.IsEnabled = _settings.AmbientBackground;
                ApplyTheme();
                _ambient.Show(Player.CurrentTrack);
            }

        };

        ExtendsContentIntoTitleBar = true;
        ConfigureCaptionButtons();
        ConfigurePlayerLayout();


        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath))
        {
            try { AppWindow.SetIcon(iconPath); }
            catch {  }
        }

        ContentFrame.Navigated += (_, _) => Nav.IsBackEnabled = ContentFrame.CanGoBack;

        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), handledEventsToo: true);

        RootGrid.PreviewKeyDown += OnPreviewKeyDown;


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

    private async Task RequireThemeRestartAsync()
    {
        if (_themeRestartDialogOpen || _settings.Theme == _startupTheme) return;
        _themeRestartDialogOpen = true;
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Restart required",
            Content = "Restart Wavy to apply the new theme. Playback will stop when the app restarts.",
            PrimaryButtonText = "Restart Wavy",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.Closing += (_, args) => args.Cancel = true;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            _placementSave.Stop();
            SavePlacement();
            try
            {
                App.Services.GetRequiredService<PlaybackService>().Stop();
                Microsoft.Windows.AppLifecycle.AppInstance.Restart(string.Empty);
                dialog.Content = "Wavy couldn't restart automatically. Try again, or close and reopen the app to apply the theme.";
            }
            catch
            {
                dialog.Content = "Wavy couldn't restart automatically. Try again, or close and reopen the app to apply the theme.";
            }
        };
        try { await dialog.ShowAsync(); }
        finally { _themeRestartDialogOpen = false; }
    }

    private void ApplyTheme()
    {
        ThemeManager.Apply(this, RootGrid, _startupTheme, _settings.AccentColor, _settings.AmbientBackground);
        var dark = _startupTheme is AppTheme.Dark or AppTheme.Darker
            || (_startupTheme == AppTheme.System && Application.Current.RequestedTheme == ApplicationTheme.Dark);
        AmbientScrim.Opacity = dark ? 0.28 : 0.78;
    }

    #region Window placement

    private void RestorePlacement()
    {
        if (_settings.Placement is not { } saved)
            return;

        var minimum = MinimumSize();
        var rect = new RectInt32(saved.X, saved.Y, Math.Max(saved.Width, minimum.Width), Math.Max(saved.Height, minimum.Height));
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

    private SizeInt32 MinimumSize()
    {
        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest)?.WorkArea;
        return new SizeInt32(Math.Min((int)Math.Ceiling(960 * scale), work?.Width ?? int.MaxValue),
            Math.Min((int)Math.Ceiling(640 * scale), work?.Height ?? int.MaxValue));
    }

    private void ConfigureMinimumSize()
    {
        var minimum = MinimumSize();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = minimum.Width;
            presenter.PreferredMinimumHeight = minimum.Height;
            if (presenter.State == OverlappedPresenterState.Restored)
                AppWindow.Resize(new SizeInt32(Math.Max(AppWindow.Size.Width, minimum.Width), Math.Max(AppWindow.Size.Height, minimum.Height)));
        }
    }

    private void ConfigurePlayerLayout()
    {
        ConfigureCaptionButtons();
        NowPlaying.VerticalAlignment = VerticalAlignment.Bottom;
        NowPlaying.IsFloating = true;
        LyricsSide.PanelCorner = new CornerRadius(12, 0, 0, 0);
        QueueSide.PanelCorner = new CornerRadius(12, 0, 0, 0);
        ContentArea.Margin = new Thickness(0, 32, 0, 0);
        LyricsSide.PanelTopInset = Math.Max(0, CaptionHeight() - 32);
        QueueSide.PanelTopInset = Math.Max(0, CaptionHeight() - 32);
        TitleStrip.Height = CaptionHeight();
        SetTitleBar(TitleStrip);
        UpdateSidePanelLayout();
    }

    private void UpdateSidePanelLayout()
    {
        var width = Math.Max(0, ContentArea.ActualWidth);
        Grid.SetColumn(LyricsSide, 1);
        Grid.SetColumn(QueueSide, 2);
        LyricsSide.HorizontalAlignment = HorizontalAlignment.Stretch;
        QueueSide.HorizontalAlignment = HorizontalAlignment.Stretch;
        LyricsSide.Visibility = Player.IsLyricsOpen ? Visibility.Visible : Visibility.Collapsed;
        QueueSide.Visibility = Player.IsQueueOpen ? Visibility.Visible : Visibility.Collapsed;
        LyricsSide.PanelWidth = Math.Clamp(width * 0.36, 300, 380);
        QueueSide.PanelWidth = Math.Clamp(width * 0.32, 280, 320);
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

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        UpdateNavigationIcons(args.IsSettingsSelected ? Nav.SettingsItem as NavigationViewItem : args.SelectedItemContainer as NavigationViewItem);
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
            UpdateNavigationIcons(Nav.SettingsItem as NavigationViewItem);
            ClearSearch();
            ShowPage(typeof(SettingsPage));
            return;
        }

        var invoked = args.InvokedItemContainer as NavigationViewItem;
        UpdateNavigationIcons(invoked);
        if (invoked?.Tag as string != "search")
            ClearSearch();

        NavigateTo(invoked);
    }

    private void UpdateNavigationIcons(NavigationViewItem? selectedItem = null)
    {
        selectedItem ??= Nav.SelectedItem as NavigationViewItem;
        HomeIcon.Glyph = ReferenceEquals(selectedItem, HomeNavItem) ? "\uEA8A" : "\uE80F";
        AlbumsNavItem.Icon = ReferenceEquals(selectedItem, AlbumsNavItem)
            ? NavigationIcon("F0 M3,1 H17 Q19,1 19,3 V17 Q19,19 17,19 H3 Q1,19 1,17 V3 Q1,1 3,1 Z M10,5 A5,5 0 1 0 10,15 A5,5 0 1 0 10,5 Z M10,8.5 A1.5,1.5 0 1 0 10,11.5 A1.5,1.5 0 1 0 10,8.5 Z") : new FontIcon { Glyph = "\uE93C" };
        if (Nav.SettingsItem is NavigationViewItem settingsItem)
            settingsItem.Icon = new FontIcon { Glyph = ReferenceEquals(selectedItem, settingsItem) ? "\uF8B0" : "\uE713" };

    }

    private static PathIcon NavigationIcon(string data) =>
        (PathIcon)Microsoft.UI.Xaml.Markup.XamlReader.Load($"<PathIcon xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='{data}' />");

    private void ClearSearch()
    {
        _search.Clear();
    }

    private void NavigateTo(NavigationViewItem? item)
    {
        if (item?.Tag is string tag) GoToSection(tag);
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

    public void ShowArtist(string browseId)
    {
        ContentFrame.Navigate(typeof(ArtistPage), browseId);
        ContentFrame.BackStack.Clear();
        Nav.IsBackEnabled = false;
    }

    private void Nav_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack)
            ContentFrame.GoBack();
    }

    private void Update_OpenSettings(object sender, RoutedEventArgs e)
    {
        Updates.DismissNotification();
        Nav.SelectedItem = Nav.SettingsItem;
        ShowPage(typeof(SettingsPage));
        if (ContentFrame.Content is SettingsPage page) page.ShowUpdates();
    }

    private void Update_Dismiss(object sender, RoutedEventArgs e) => Updates.DismissNotification();

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

        Player.PlayPauseCommand.Execute(null);
        e.Handled = true;
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void FocusSearchBox()
    {
        Nav.SelectedItem = SearchNavItem;
        GoToSection("search");
        if (ContentFrame.Content is SearchPage page)
            page.FocusSearchBox();
    }

    #endregion
}
