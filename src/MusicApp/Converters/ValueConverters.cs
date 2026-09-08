using System.Collections.Concurrent;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using MusicApp.Core.Services;
using MusicApp.Services;
using Windows.Storage.Streams;

namespace MusicApp.Converters;

public sealed class DecodedImageConverter : IValueConverter
{
    private static readonly HttpClient Http = new(Ipv4Http.CreateHandler())
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    private const int CacheCapacity = 400;
    private static readonly ConcurrentDictionary<string, Task<byte[]>> Downloads = new(StringComparer.Ordinal);

    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string url)
            return null;

        var width = parameter is string s && int.TryParse(s, out var w) && w > 0 ? w : 0;
        return Load(url, width);
    }

    public static BitmapImage? Load(string? url, int width)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        Uri uri;
        try { uri = new Uri(url); }
        catch (UriFormatException) { return null; }

        var bitmap = new BitmapImage { DecodePixelType = DecodePixelType.Logical };
        if (width > 0)
            bitmap.DecodePixelWidth = width;

        if (!uri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            bitmap.UriSource = uri;
            return bitmap;
        }

        _ = FillAsync(bitmap, url, uri);
        return bitmap;
    }

    public static Task<byte[]> BytesAsync(string url) =>
        Downloads.GetOrAdd(url, key => Http.GetByteArrayAsync(new Uri(key)));

    private static async Task FillAsync(BitmapImage bitmap, string url, Uri uri)
    {
        try
        {
            if (Downloads.Count > CacheCapacity)
                Downloads.Clear();

            var bytes = await Downloads.GetOrAdd(url, _ => Http.GetByteArrayAsync(uri));
            if (bytes.Length == 0)
                return;

            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
        }
        catch
        {

            Downloads.TryRemove(url, out _);
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool b && b;
        if (parameter is string p && p.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility v && v == Visibility.Visible;
}

public sealed class PercentTooltipConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is double d ? ((int)Math.Round(d * 100)).ToString() : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class BoolToAccentBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is bool b && b ? ThemeManager.AccentTint : ThemeManager.NeutralTint;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
