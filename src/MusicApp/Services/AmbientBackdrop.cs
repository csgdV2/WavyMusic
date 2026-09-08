using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using MusicApp.Converters;
using MusicApp.Core.Models;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace MusicApp.Services;

public sealed class AmbientBackdrop
{
    private const uint Samples = 12;
    private const double Muted = 0.55;
    private const int Regions = 3;
    private const double Radius = 0.62;

    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(1200);

    private readonly Panel _host;
    private Panel _back;
    private Panel _front;

    private string? _shown;
    private bool _enabled;

    public AmbientBackdrop(Panel host, Panel back, Panel front, Shape scrim)
    {
        _host = host;
        _back = back;
        _front = front;
        scrim.Fill = ThemeManager.Scrim;
    }

    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;

            _enabled = value;
            _host.Visibility = value ? Visibility.Visible : Visibility.Collapsed;

            if (value)
                return;

            _shown = null;
            _front.Children.Clear();
            _back.Children.Clear();
            _front.Opacity = 0;
            _back.Opacity = 0;
        }
    }

    public void Show(Track? track)
    {
        var url = track?.ThumbnailUrl;

        if (!_enabled || string.IsNullOrWhiteSpace(url) || _shown == url)
            return;

        _shown = url;
        _ = BuildAsync(url);
    }

    private async Task BuildAsync(string url)
    {
        List<Color> colors;
        try
        {
            var bytes = await DecodedImageConverter.BytesAsync(url).ConfigureAwait(true);
            colors = await SampleAsync(bytes);
        }
        catch
        {
            return;
        }

        if (!_enabled || _shown != url || colors.Count == 0)
            return;

        var spare = _back;
        Paint(spare, colors);

        _back = _front;
        _front = spare;

        spare.Opacity = 0;
        Animate(spare, 1.0);
        Animate(_back, 0.0);
    }

    private static void Paint(Panel layer, IReadOnlyList<Color> colors)
    {
        layer.Children.Clear();
        layer.Children.Add(new Rectangle { Fill = new SolidColorBrush(colors[0]) });

        for (var i = 1; i < colors.Count; i++)
        {
            var color = colors[i];
            var cell = i - 1;
            var centre = new Point(
                (cell % Regions + 0.5) / Regions,
                (cell / Regions + 0.5) / Regions);

            var brush = new RadialGradientBrush
            {
                Center = centre,
                GradientOrigin = centre,
                RadiusX = Radius,
                RadiusY = Radius,
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
            };

            brush.GradientStops.Add(new GradientStop { Color = color, Offset = 0 });
            brush.GradientStops.Add(new GradientStop
            {
                Color = Color.FromArgb(0, color.R, color.G, color.B),
                Offset = 1,
            });

            layer.Children.Add(new Rectangle { Fill = brush });
        }
    }

    private static async Task<List<Color>> SampleAsync(byte[] bytes)
    {
        var colors = new List<Color>();
        if (bytes.Length == 0)
            return colors;

        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Rgba8,
            BitmapAlphaMode.Ignore,
            new BitmapTransform
            {
                ScaledWidth = Samples,
                ScaledHeight = Samples,
                InterpolationMode = BitmapInterpolationMode.Fant,
            },
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);

        var pixels = data.DetachPixelData();
        var size = (int)Samples;
        var step = size / Regions;

        colors.Add(Average(pixels, size, 0, 0, size, size));

        for (var row = 0; row < Regions; row++)
            for (var column = 0; column < Regions; column++)
                colors.Add(Average(
                    pixels, size,
                    column * step, row * step,
                    column == Regions - 1 ? size : (column + 1) * step,
                    row == Regions - 1 ? size : (row + 1) * step));

        return colors;
    }

    private static Color Average(byte[] pixels, int size, int left, int top, int right, int bottom)
    {
        long r = 0, g = 0, b = 0, count = 0;

        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var at = (y * size + x) * 4;
                if (at + 2 >= pixels.Length)
                    continue;

                r += pixels[at];
                g += pixels[at + 1];
                b += pixels[at + 2];
                count++;
            }
        }

        if (count == 0)
            return ThemeManager.Scrim.Color;

        return Mix(
            Color.FromArgb(255, (byte)(r / count), (byte)(g / count), (byte)(b / count)),
            ThemeManager.Scrim.Color,
            Muted);
    }

    private static Color Mix(Color color, Color towards, double amount)
    {
        static byte Blend(byte from, byte to, double amount) =>
            (byte)Math.Clamp(from + (to - from) * amount, 0, 255);

        return Color.FromArgb(
            255,
            Blend(color.R, towards.R, amount),
            Blend(color.G, towards.G, amount),
            Blend(color.B, towards.B, amount));
    }

    private static void Animate(UIElement target, double to)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = Fade,
            EnableDependentAnimation = true,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };

        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }
}
