using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Wavy.Controls;

internal static class NavigationArtwork
{
    public const string AlbumPath = "M5 2.5H19Q21.5 2.5 21.5 5V19Q21.5 21.5 19 21.5H5Q2.5 21.5 2.5 19V5Q2.5 2.5 5 2.5Z M12 6A6 6 0 1 0 12 18A6 6 0 1 0 12 6Z M12 10.5A1.5 1.5 0 1 0 12 13.5A1.5 1.5 0 1 0 12 10.5Z";
    public static readonly string GearPath = CreateGear();

    public static async void Set(NavigationViewItem item, string path, bool selected, bool dark)
    {
        var foreground = dark ? "#F5F5F5" : "#242424";
        var outline = selected ? (dark ? "#969696" : "#787878") : foreground;
        var fill = selected ? foreground : "none";
        var svg = $"<svg xmlns='http://www.w3.org/2000/svg' width='24' height='24' viewBox='0 0 24 24'><path d='{path}' fill='{fill}' fill-rule='evenodd' stroke='{outline}' stroke-width='1.45' stroke-linejoin='round'/></svg>";
        var source = new SvgImageSource { RasterizePixelWidth = 64, RasterizePixelHeight = 64 };
        var icon = new ImageIcon { Source = source, Width = 20, Height = 20 };
        item.Icon = icon;
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(svg).AsBuffer());
            stream.Seek(0);
            await source.SetSourceAsync(stream);
        }
        catch
        {
            if (ReferenceEquals(item.Icon, icon)) item.Icon = new FontIcon { Glyph = path == AlbumPath ? "\uE93C" : "\uE713" };
        }
    }

    private static string CreateGear()
    {
        var path = new StringBuilder();
        for (var tooth = 0; tooth < 8; tooth++)
            foreach (var (angleOffset, radius) in new[] { (-22.5, 8.1), (-13.0, 8.1), (-9.0, 10.4), (9.0, 10.4), (13.0, 8.1) })
            {
                var angle = (tooth * 45 + angleOffset) * Math.PI / 180;
                var x = 12 + radius * Math.Cos(angle);
                var y = 12 + radius * Math.Sin(angle);
                path.Append(path.Length == 0 ? "M" : "L").Append(x.ToString("F3", CultureInfo.InvariantCulture)).Append(' ').Append(y.ToString("F3", CultureInfo.InvariantCulture));
            }
        return path.Append("Z M12 8.6A3.4 3.4 0 1 0 12 15.4A3.4 3.4 0 1 0 12 8.6Z").ToString();
    }
}
