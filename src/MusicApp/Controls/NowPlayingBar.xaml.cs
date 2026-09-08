using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MusicApp.Core.Abstractions;
using MusicApp.ViewModels;
using Windows.UI.Text;

namespace MusicApp.Controls;

public sealed partial class NowPlayingBar : UserControl
{
    public PlayerViewModel ViewModel { get; }

    public IReadOnlyList<FrameworkElement> InteractiveRegions =>
        new FrameworkElement[] { TransportPanel, SongCard, VolumePanel };

    private static readonly char[] ArtistSeparators = { ',', '&', '/', '·', '•', ';' };

    private static readonly string[] FeatureMarkers =
        { " feat. ", " feat ", " ft. ", " ft ", " featuring ", " with " };

    private bool _scrubbing;

    private bool _findingArtist;

    private const double EdgeZone = 30;

    private const double SeekGrab = 2;

    public NowPlayingBar()
    {
        ViewModel = App.Services.GetRequiredService<PlayerViewModel>();
        InitializeComponent();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ProgressTrack.SizeChanged += (_, _) => PlaceSeekThumb();

        var settings = App.Services.GetRequiredService<Services.SettingsService>();
        ApplyVolumeStep(settings);
        settings.Changed += (_, name) =>
        {
            if (name == nameof(Services.SettingsService.VolumeStepPercent))
                DispatcherQueue.TryEnqueue(() => ApplyVolumeStep(settings));
        };

        Loaded += (_, _) =>
        {
            if (!_floating)
                return;

            EnsureGlass();
            SizeGlass();
        };

        Unloaded += (_, _) => ReleaseGlass();

        ActualThemeChanged += (_, _) =>
        {
            if (_floating)
                Root.Background = FloatingFill();
        };
    }

    private bool _floating;

    public bool IsFloating
    {
        get => _floating;
        set
        {
            if (_floating == value)
                return;

            _floating = value;

            if (value)
            {
                HorizontalAlignment = HorizontalAlignment.Center;
                Margin = new Thickness(16, 10, 16, 16);
                Root.Height = 64;
                Root.Padding = new Thickness(12, 7, 12, 7);
                Root.CornerRadius = new CornerRadius(32);
                Root.BorderThickness = new Thickness(0);
                Outline.CornerRadius = new CornerRadius(32);
                Outline.BorderThickness = new Thickness(1);
                Outline.Margin = new Thickness(0, 0, 0, 0);
                Outline.Height = 64;
                Outline.VerticalAlignment = VerticalAlignment.Bottom;
                Glass.CornerRadius = new CornerRadius(32);
                Glass.Visibility = Visibility.Visible;
                EnsureGlass();
                SizeGlass();
                Root.Background = FloatingFill();
                SongCard.Width = 400;
                SongCard.HorizontalAlignment = HorizontalAlignment.Center;
                SongCard.Background = null;
                SongCard.BorderThickness = new Thickness(0);
            }
            else
            {
                HorizontalAlignment = HorizontalAlignment.Stretch;
                Margin = new Thickness(0);
                Root.Height = 66;
                Root.Padding = new Thickness(16, 8, 150, 8);
                Root.CornerRadius = new CornerRadius(0);
                Root.BorderThickness = new Thickness(0);
                Outline.CornerRadius = new CornerRadius(0);
                Outline.BorderThickness = new Thickness(0, 0, 0, 1);
                Outline.Height = double.NaN;
                Outline.VerticalAlignment = VerticalAlignment.Stretch;
                Glass.Visibility = Visibility.Collapsed;
                ReleaseGlass();
                Root.Background = Application.Current.Resources["LayerFillColorDefaultBrush"] as Brush
                    ?? Root.Background;
                SongCard.Width = double.NaN;
                SongCard.HorizontalAlignment = HorizontalAlignment.Stretch;
                SongCard.Background =
                    Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Brush;
                SongCard.BorderThickness = new Thickness(1);
            }
        }
    }

    private void ApplyVolumeStep(Services.SettingsService settings)
    {
        var step = settings.VolumeStepPercent / 100.0;
        VolumeSlider.StepFrequency = step;
        VolumeSlider.SmallChange = step;
        VolumeSlider.LargeChange = step;
    }

    private SpriteVisual? _glassVisual;

    private CompositionRoundedRectangleGeometry? _glassGeometry;

    private bool _glassFailed;

    private bool IsDark =>
        ActualTheme == ElementTheme.Dark
        || (ActualTheme == ElementTheme.Default
            && Application.Current.RequestedTheme == ApplicationTheme.Dark);

    private Brush FloatingFill()
        => new SolidColorBrush(IsDark
            ? Windows.UI.Color.FromArgb(120, 26, 26, 26)
            : Windows.UI.Color.FromArgb(140, 246, 246, 246));

    private void EnsureGlass()
    {
        if (_glassVisual is not null || _glassFailed)
            return;

        try
        {
            var compositor = ElementCompositionPreview.GetElementVisual(Glass).Compositor;

            var effect = new GaussianBlurEffect
            {
                BlurAmount = 24f,
                BorderMode = EffectBorderMode.Hard,
                Optimization = EffectOptimization.Speed,
                Source = new CompositionEffectSourceParameter("Backdrop"),
            };

            var brush = compositor.CreateEffectFactory(effect).CreateBrush();
            brush.SetSourceParameter("Backdrop", compositor.CreateBackdropBrush());

            _glassGeometry = compositor.CreateRoundedRectangleGeometry();
            _glassGeometry.CornerRadius = new Vector2(32f);

            _glassVisual = compositor.CreateSpriteVisual();
            _glassVisual.Brush = brush;
            _glassVisual.Clip = compositor.CreateGeometricClip(_glassGeometry);

            ElementCompositionPreview.SetElementChildVisual(Glass, _glassVisual);
        }
        catch
        {
            _glassFailed = true;
            ReleaseGlass();
        }
    }

    private void ReleaseGlass()
    {
        try
        {
            ElementCompositionPreview.SetElementChildVisual(Glass, null);
        }
        catch
        {
        }

        _glassVisual?.Dispose();
        _glassVisual = null;
        _glassGeometry?.Dispose();
        _glassGeometry = null;
    }

    private void SizeGlass()
    {
        if (_glassVisual is null)
            return;

        var width = (float)Glass.ActualWidth;
        var height = (float)Glass.ActualHeight;
        if (width <= 0 || height <= 0 || float.IsNaN(width) || float.IsNaN(height))
            return;

        _glassVisual.Size = new Vector2(width, height);

        if (_glassGeometry is not null)
        {
            _glassGeometry.Size = new Vector2(width, height);
            _glassGeometry.CornerRadius = new Vector2((float)Root.CornerRadius.TopLeft);
        }
    }

    private void Glass_SizeChanged(object sender, SizeChangedEventArgs e) => SizeGlass();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.PositionSeconds) or nameof(PlayerViewModel.DurationSeconds))
            PlaceSeekThumb();
    }

    private void PlaceSeekThumb()
    {
        var width = ProgressTrack.ActualWidth;
        if (width <= 0)
            return;

        var ratio = ViewModel.DurationSeconds > 0
            ? Math.Clamp(ViewModel.PositionSeconds / ViewModel.DurationSeconds, 0, 1)
            : 0;

        var left = Math.Round(Math.Clamp(ratio * width - SeekThumb.Width / 2, 0, width - SeekThumb.Width));
        if (left == SeekThumb.Margin.Left)
            return;

        SeekThumb.Margin = new Thickness(left, 0, 0, 0);
    }

    private void CardGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!ViewModel.HasTrack || ViewModel.DurationSeconds <= 0 || sender is not FrameworkElement fe)
            return;

        if (!IsOverSeekBand(e))
            return;

        _scrubbing = true;
        fe.CapturePointer(e.Pointer);
        SeekFromPointer(fe, e);
        e.Handled = true;
    }

    private bool IsOverSeekBand(PointerRoutedEventArgs e)
    {
        if (SeekRow.ActualHeight <= 0)
            return false;

        var top = SeekRow.TransformToVisual(CardGrid)
                         .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
        var y = e.GetCurrentPoint(CardGrid).Position.Y;
        return y >= top - SeekGrab;
    }

    private void CardGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_scrubbing && sender is FrameworkElement fe)
            SeekFromPointer(fe, e);
    }

    private void CardGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_scrubbing && sender is FrameworkElement fe)
        {
            SeekFromPointer(fe, e);
            fe.ReleasePointerCapture(e.Pointer);
        }
        _scrubbing = false;
        SetHoverDetailsVisible(IsOverCard(e));
    }

    private void CardGrid_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _scrubbing = false;
        SetHoverDetailsVisible(IsOverCard(e));
    }

    private void CardGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        => e.Handled = true;

    private void CardGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
        => SetHoverDetailsVisible(true);

    private void CardGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {

        if (!_scrubbing)
            SetHoverDetailsVisible(false);
    }

    private void SetHoverDetailsVisible(bool visible)
    {
        var opacity = visible ? 1.0 : 0.0;
        ElapsedLabel.Opacity = opacity;
        RemainingLabel.Opacity = opacity;
        SeekThumb.Opacity = opacity;
    }

    private bool IsOverCard(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(CardGrid).Position;
        return point.X >= 0 && point.Y >= 0
            && point.X <= CardGrid.ActualWidth && point.Y <= CardGrid.ActualHeight;
    }

    private void SeekFromPointer(FrameworkElement card, PointerRoutedEventArgs e)
    {

        var seekWidth = card.ActualWidth;
        if (MoreButton.Visibility == Visibility.Visible)
            seekWidth -= MoreButton.ActualWidth + CardGrid.ColumnSpacing;

        var cardX = e.GetCurrentPoint(card).Position.X;
        if (seekWidth > EdgeZone * 3)
        {
            if (cardX <= EdgeZone)
            {
                ViewModel.SeekTo(0);
                return;
            }

            if (cardX >= seekWidth - EdgeZone)
            {

                ViewModel.SeekTo(Math.Max(0, ViewModel.DurationSeconds - 0.25));
                return;
            }
        }

        var track = ProgressTrack.ActualWidth > 0 ? (FrameworkElement)ProgressTrack : card;
        var x = e.GetCurrentPoint(track).Position.X;
        var ratio = Math.Clamp(x / track.ActualWidth, 0, 1);
        ViewModel.SeekTo(ratio * ViewModel.DurationSeconds);
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentTrack is { } track)
            TrackMenu.ShowFor(MoreButton, track, queueActions: false);
    }

    private async void Artist_Click(object sender, RoutedEventArgs e)
    {
        if (_findingArtist || ViewModel.CurrentTrack?.Artist is not { Length: > 0 } artist)
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
        ArtistText.TextDecorations = TextDecorations.Underline;
        ArtistText.Opacity = 1.0;
    }

    private void Artist_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        ArtistText.TextDecorations = TextDecorations.None;
        ArtistText.Opacity = 0.7;
    }

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
}
