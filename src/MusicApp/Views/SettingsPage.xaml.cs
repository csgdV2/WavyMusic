using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using MusicApp.Services;
using MusicApp.ViewModels;

namespace MusicApp.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    private Slider[] _eqSliders = Array.Empty<Slider>();
    private bool _loadingEqualizer;

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
        _eqSliders = new[] { Eq32, Eq64, Eq125, Eq250, Eq500, Eq1k, Eq2k, Eq4k, Eq8k, Eq16k };
        ViewModel.SearchHistoryChanged += OnSearchHistoryChanged;

        Loaded += (_, _) =>
        {
            LoadEqualizerValues();
        };

        Unloaded += (_, _) => ViewModel.SearchHistoryChanged -= OnSearchHistoryChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        ViewModel.RefreshCacheSummary();
    }

    private void AccentFlyout_Closed(object sender, object e) => ViewModel.CommitAccent();

    private void OnSearchHistoryChanged(object? sender, EventArgs e)
    {
        ViewModel.RefreshSearchHistory();
    }

    private void ClearSearchHistory_Click(object sender, RoutedEventArgs e)
        => ViewModel.ClearSearchHistory();

    private void SystemAccent_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.UseSystemAccent();
        AccentFlyout.Hide();
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Clear saved songs?",
            Content = "The downloaded copies are deleted. Songs still play — they'll just stream again.",
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        ClearResult.Text = ViewModel.ClearCache();
        ClearResult.Visibility = Visibility.Visible;
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Windows.System.Launcher.LaunchFolderPathAsync(AppPaths.DataRoot);
        }
        catch
        {

        }
    }

    private void LoadEqualizerValues()
    {
        var bands = ViewModel.EqualizerBands;
        _loadingEqualizer = true;
        if (bands.Length >= 10)
        {
            for (int i = 0; i < _eqSliders.Length; i++)
                _eqSliders[i].Value = bands[i];
        }
        _loadingEqualizer = false;
        DrawEqualizerCurve();
    }

    private void Equalizer_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loadingEqualizer || _eqSliders.Length == 0)
            return;

        var bands = new double[10];
        for (int i = 0; i < 10 && i < _eqSliders.Length; i++)
        {
            bands[i] = _eqSliders[i].Value;
        }
        ViewModel.EqualizerBands = bands;
        DrawEqualizerCurve();
    }

    private void ResetEqualizer_Click(object sender, RoutedEventArgs e)
    {
        foreach (var slider in _eqSliders)
        {
            slider.Value = 0;
        }
    }

    private void EqualizerCurve_SizeChanged(object sender, SizeChangedEventArgs e) => DrawEqualizerCurve();

    private void DrawEqualizerCurve()
    {
        if (EqualizerCurve is null || EqualizerCurve.ActualWidth <= 0 || EqualizerCurve.ActualHeight <= 0)
            return;

        var width = EqualizerCurve.ActualWidth;
        var height = EqualizerCurve.ActualHeight;
        SetGridLine(EqGrid12, 12, width, height);
        SetGridLine(EqGrid6, 6, width, height);
        SetGridLine(EqGrid0, 0, width, height);
        SetGridLine(EqGridMinus6, -6, width, height);
        SetGridLine(EqGridMinus12, -12, width, height);

        var points = new PointCollection();
        for (var i = 0; i < _eqSliders.Length; i++)
        {
            var x = (i + 0.5) * width / _eqSliders.Length;
            var y = (12d - _eqSliders[i].Value) / 24d * height;
            points.Add(new Windows.Foundation.Point(x, y));
        }

        var figure = new PathFigure { StartPoint = points[0], IsClosed = false };
        for (var i = 0; i < points.Count - 1; i++)
        {
            var previous = i > 0 ? points[i - 1] : points[i];
            var start = points[i];
            var end = points[i + 1];
            var next = i + 2 < points.Count ? points[i + 2] : end;
            figure.Segments.Add(new BezierSegment
            {
                Point1 = new Windows.Foundation.Point(start.X + (end.X - previous.X) / 6, start.Y + (end.Y - previous.Y) / 6),
                Point2 = new Windows.Foundation.Point(end.X - (next.X - start.X) / 6, end.Y - (next.Y - start.Y) / 6),
                Point3 = end
            });
        }
        var responseGeometry = new PathGeometry();
        responseGeometry.Figures.Add(figure);
        EqualizerResponse.Data = responseGeometry;
        foreach (var child in EqualizerCurve.Children.OfType<Ellipse>().ToArray())
            EqualizerCurve.Children.Remove(child);

        foreach (var point in points)
        {
            var dot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = EqualizerResponse.Stroke
            };
            Canvas.SetLeft(dot, point.X - dot.Width / 2);
            Canvas.SetTop(dot, point.Y - dot.Height / 2);
            EqualizerCurve.Children.Add(dot);
        }
    }

    private static void SetGridLine(Line line, double gain, double width, double height)
    {
        var y = (12d - gain) / 24d * height;
        line.X1 = 0;
        line.X2 = width;
        line.Y1 = y;
        line.Y2 = y;
    }
}
