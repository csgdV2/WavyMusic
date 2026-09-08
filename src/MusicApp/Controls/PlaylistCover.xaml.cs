using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using MusicApp.Core.Models;

namespace MusicApp.Controls;

public sealed partial class PlaylistCover : UserControl
{
    public static readonly DependencyProperty PlaylistProperty =
        DependencyProperty.Register(nameof(Playlist), typeof(object), typeof(PlaylistCover),
            new PropertyMetadata(null, OnPlaylistChanged));

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(nameof(Size), typeof(double), typeof(PlaylistCover),
            new PropertyMetadata(40d, OnSizeChanged));

    public static readonly DependencyProperty RadiusProperty =
        DependencyProperty.Register(nameof(Radius), typeof(double), typeof(PlaylistCover),
            new PropertyMetadata(4d, OnRadiusChanged));

    public PlaylistCover() => InitializeComponent();

    public object? Playlist
    {
        get => GetValue(PlaylistProperty);
        set => SetValue(PlaylistProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public double Radius
    {
        get => (double)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    private static void OnPlaylistChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((PlaylistCover)d).Apply();

    private static void OnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var cover = (PlaylistCover)d;
        cover.Frame.Width = cover.Size;
        cover.Frame.Height = cover.Size;
        cover.Apply();
    }

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var cover = (PlaylistCover)d;
        cover.Frame.CornerRadius = new CornerRadius(cover.Radius);
    }

    private void Apply()
    {
        var covers = (Playlist as MusicApp.Core.Models.Playlist)?.CoverUrls ?? Array.Empty<string>();
        var mosaic = covers.Count >= 4;

        Mosaic.Visibility = mosaic ? Visibility.Visible : Visibility.Collapsed;
        Single.Visibility = mosaic ? Visibility.Collapsed : Visibility.Visible;

        if (mosaic)
        {
            var half = (int)Math.Ceiling(Size);
            Single.Source = null;
            Tile0.Source = Decode(covers[0], half);
            Tile1.Source = Decode(covers[1], half);
            Tile2.Source = Decode(covers[2], half);
            Tile3.Source = Decode(covers[3], half);
            return;
        }

        Tile0.Source = Tile1.Source = Tile2.Source = Tile3.Source = null;
        Single.Source = covers.Count > 0 ? Decode(covers[0], (int)Math.Ceiling(Size * 2)) : null;
    }

    private static BitmapImage? Decode(string url, int width) =>
        MusicApp.Converters.DecodedImageConverter.Load(url, width);
}
