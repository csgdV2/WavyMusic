using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MusicApp.Core.Models;
using MusicApp.ViewModels;

namespace MusicApp.Controls;

public sealed partial class QueuePanel : UserControl
{
    public PlayerViewModel ViewModel { get; }

    public QueuePanel()
    {
        ViewModel = App.Services.GetRequiredService<PlayerViewModel>();
        InitializeComponent();
    }

    public CornerRadius PanelCorner
    {
        set => Root.CornerRadius = value;
    }

    public double PanelTopInset
    {
        set => Root.Margin = new Thickness(0, value, 0, 0);
    }

    private async void Queue_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Track track)
            await ViewModel.PlayQueuedAsync(track);
    }

    private void Row_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        TrackMenu.SetHoverActionsVisible(sender, true);
        TrackMenu.SetHoverPlateVisible(sender, true);
    }

    private void Row_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        TrackMenu.SetHoverActionsVisible(sender, false);
        TrackMenu.SetHoverPlateVisible(sender, false);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Track track })
            ViewModel.RemoveFromQueue(track);
    }
}
