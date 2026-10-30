using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Wavy.Controls;

public static class SearchFieldAlignment
{
    public static readonly DependencyProperty CenterTextProperty = DependencyProperty.RegisterAttached(
        "CenterText", typeof(bool), typeof(SearchFieldAlignment), new PropertyMetadata(false, OnChanged));
    public static bool GetCenterText(DependencyObject target) => (bool)target.GetValue(CenterTextProperty);
    public static void SetCenterText(DependencyObject target, bool value) => target.SetValue(CenterTextProperty, value);
    private static void OnChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not TextBox box) return;
        box.Loaded -= Loaded;
        box.SizeChanged -= Resized;
        box.ActualThemeChanged -= ThemeChanged;
        if (args.NewValue is true)
        {
            box.Loaded += Loaded;
            box.SizeChanged += Resized;
            box.ActualThemeChanged += ThemeChanged;
            if (box.IsLoaded) Align(box);
        }
    }
    private static void Loaded(object sender, RoutedEventArgs args) => Align((TextBox)sender);
    private static void Resized(object sender, SizeChangedEventArgs args) => Align((TextBox)sender);
    private static void ThemeChanged(FrameworkElement sender, object args) => sender.DispatcherQueue.TryEnqueue(() => Align((TextBox)sender));
    public static void Align(TextBox box)
    {
        box.VerticalContentAlignment = VerticalAlignment.Center;
        box.Padding = new Thickness(box.Padding.Left, 0, box.Padding.Right, 0);
        AlignParts(box);
    }
    private static void AlignParts(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer { Name: "ContentElement" } content)
            {
                content.VerticalAlignment = VerticalAlignment.Center;
                content.VerticalContentAlignment = VerticalAlignment.Center;
                content.Padding = new Thickness(content.Padding.Left, 0, content.Padding.Right, 0);
            }
            if (child is FrameworkElement { Name: "PlaceholderTextContentPresenter" } placeholder)
            {
                placeholder.VerticalAlignment = VerticalAlignment.Center;
                if (placeholder is TextBlock text) text.Padding = new Thickness(text.Padding.Left, 0, text.Padding.Right, 0);
                if (placeholder is Control control)
                {
                    control.VerticalContentAlignment = VerticalAlignment.Center;
                    control.Padding = new Thickness(control.Padding.Left, 0, control.Padding.Right, 0);
                }
            }
            AlignParts(child);
        }
    }
}
