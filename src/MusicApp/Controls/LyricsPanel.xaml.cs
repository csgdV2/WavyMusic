using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MusicApp.ViewModels;

namespace MusicApp.Controls;

public sealed partial class LyricsPanel : UserControl
{
    public LyricsViewModel ViewModel { get; }

    public PlayerViewModel Player { get; }

    private const int LeadLines = 2;

    private static readonly TimeSpan GlideStep = TimeSpan.FromMilliseconds(16);

    private const double GlideMs = 520;

    private ScrollViewer? _scroller;

    private bool _detached;

    private bool _animating;

    private double _settledOffset;

    private int _pending = -1;

    private LyricsLineItem? _active;

    private DispatcherQueueTimer? _glide;

    private double _glideFrom;

    private double _glideTo;

    private long _glideStart;

    private bool _firstAlign = true;

    public LyricsPanel()
    {
        ViewModel = App.Services.GetRequiredService<LyricsViewModel>();
        Player = App.Services.GetRequiredService<PlayerViewModel>();
        InitializeComponent();

        LineList.Loaded += (_, _) => HookScroller();

        ViewModel.ActiveLineChanged += (_, line) =>
        {
            _active = line;
            if (Player.IsLyricsOpen && !_detached)
                ScrollTo(line);
        };

        ViewModel.LinesReset += (_, _) =>
        {
            _active = null;
            SetDetached(false);
            _pending = -1;
            _settledOffset = 0;
            StopGlide();
            _firstAlign = true;
            DispatcherQueue.TryEnqueue(() => _scroller?.ChangeView(null, 0, null, true));
        };
    }

    private void StopGlide()
    {
        _glide?.Stop();
        _animating = false;
    }

    private void GlideTo(double offset)
    {
        if (_scroller is null)
            return;

        var from = _scroller.VerticalOffset;
        if (Math.Abs(offset - from) < 0.5)
        {
            _settledOffset = offset;
            return;
        }

        if (_firstAlign || Math.Abs(offset - from) > Math.Max(600, _scroller.ViewportHeight * 2))
        {
            _firstAlign = false;
            StopGlide();
            _settledOffset = offset;
            _animating = true;
            _scroller.ChangeView(null, offset, null, true);
            return;
        }

        _glideFrom = from;
        _glideTo = offset;
        _glideStart = Environment.TickCount64;
        _settledOffset = offset;
        _animating = true;

        if (_glide is null)
        {
            _glide = DispatcherQueue.CreateTimer();
            _glide.Interval = GlideStep;
            _glide.IsRepeating = true;
            _glide.Tick += (_, _) => Step();
        }

        _glide.Start();
        Step();
    }

    private void Step()
    {
        if (_scroller is null)
        {
            StopGlide();
            return;
        }

        var elapsed = Environment.TickCount64 - _glideStart;
        var t = Math.Clamp(elapsed / GlideMs, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3);
        var value = _glideFrom + (_glideTo - _glideFrom) * eased;

        _scroller.ChangeView(null, value, null, true);

        if (t >= 1)
        {
            _glide?.Stop();
            _settledOffset = _glideTo;
            _animating = false;
        }
    }

    public CornerRadius PanelCorner
    {
        set => Root.CornerRadius = value;
    }

    public double PanelTopInset
    {
        set => Root.Margin = new Thickness(0, value, 0, 0);
    }

    private void HookScroller()
    {
        if (_scroller is not null)
            return;

        _scroller = FindScroller(LineList);
        if (_scroller is null)
            return;

        _scroller.ViewChanged += Scroller_ViewChanged;
    }

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer found)
                return found;

            if (FindScroller(child) is { } nested)
                return nested;
        }

        return null;
    }

    private void ScrollTo(LyricsLineItem line)
    {
        if (!IsLoaded || !Player.IsLyricsOpen || ViewModel.IsLoading)
            return;

        var index = ViewModel.Lines.IndexOf(line);
        if (index < 0)
            return;

        DispatcherQueue.TryEnqueue(() => Align(index, true));
    }

    private void Align(int index, bool allowRetry)
    {
        HookScroller();

        if (_scroller is null || !IsLoaded || !Player.IsLyricsOpen || ViewModel.IsLoading)
            return;

        if (index < 0 || index >= ViewModel.Lines.Count)
            return;

        var anchorIndex = Math.Max(0, index - LeadLines);

        try
        {
            if (LineList.ContainerFromIndex(anchorIndex) is FrameworkElement container)
            {
                var relative = container.TransformToVisual(_scroller)
                                        .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;

                var limit = Math.Max(0, _scroller.ExtentHeight - _scroller.ViewportHeight);
                var offset = Math.Clamp(_scroller.VerticalOffset + relative, 0, limit);

                _pending = -1;
                GlideTo(offset);
                return;
            }

            if (!allowRetry)
                return;

            _pending = index;
            _animating = true;
            LineList.ScrollIntoView(ViewModel.Lines[anchorIndex], ScrollIntoViewAlignment.Leading);
            DispatcherQueue.TryEnqueue(() =>
            {
                var retry = _pending;
                _pending = -1;
                if (retry >= 0)
                    Align(retry, false);

                if (_glide?.IsRunning != true)
                {
                    _animating = false;
                    if (_scroller is not null)
                        _settledOffset = _scroller.VerticalOffset;
                }
            });
        }
        catch
        {
            _pending = -1;
            StopGlide();
        }
    }

    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_scroller is null)
            return;

        if (_animating)
        {
            if (!e.IsIntermediate && _glide?.IsRunning != true)
            {
                _animating = false;
                _settledOffset = _scroller.VerticalOffset;
            }
            return;
        }

        if (_detached || ViewModel.Lines.Count == 0)
            return;

        if (e.IsIntermediate || Math.Abs(_scroller.VerticalOffset - _settledOffset) > 6)
            SetDetached(true);
    }

    private void SetDetached(bool detached)
    {
        if (_detached == detached)
            return;

        _detached = detached;
        if (detached)
            StopGlide();

        ResyncButton.Visibility = detached && ViewModel.IsSynced
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void Resync_Click(object sender, RoutedEventArgs e)
    {
        SetDetached(false);

        if (_active is { } line)
        {
            ScrollTo(line);
            return;
        }

        GlideTo(0);
    }

    private void Line_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;

        element.CenterPoint = new System.Numerics.Vector3(
            (float)(e.NewSize.Width / 2), (float)(e.NewSize.Height / 2), 0f);
    }

    private void Line_Click(object sender, ItemClickEventArgs e)    {
        if (e.ClickedItem is not LyricsLineItem line || line.StartSeconds is not { } start)
            return;

        SetDetached(false);
        Player.SeekTo(start);
    }
}
