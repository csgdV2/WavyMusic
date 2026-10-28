using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wavy.Core.Models;
using Wavy.ViewModels;

namespace Wavy.Controls;

public sealed partial class QueuePanel : UserControl
{
    public PlayerViewModel ViewModel { get; }

    public QueuePanel()
    {
        ViewModel = App.Services.GetRequiredService<PlayerViewModel>();
        InitializeComponent();
        Loaded += (_, _) => ViewModel.PropertyChanged += OnPlayerChanged;
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnPlayerChanged;
    }

    public CornerRadius PanelCorner
    {
        set => Root.CornerRadius = value;
    }

    public double PanelTopInset
    {
        set => Root.Margin = new Thickness(0, value, 0, 0);
    }

    public double PanelWidth
    {
        set => Root.Width = value;
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

    private void QueueRow_Loaded(object sender, RoutedEventArgs e) => UpdatePlayingPlate(sender as FrameworkElement);
    private void OnPlayerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(PlayerViewModel.CurrentIndex) or nameof(PlayerViewModel.CurrentTrack))) return;
        for (var i = 0; i < QueueList.Items.Count; i++)
            if (QueueList.ContainerFromIndex(i) is ListViewItem container && container.ContentTemplateRoot is FrameworkElement root)
                UpdatePlayingPlate(root);
    }
    private void UpdatePlayingPlate(FrameworkElement? row)
    {
        if (row?.FindName("PlayingPlate") is Border plate)
            plate.Opacity = row.DataContext is Track track && track.Id == ViewModel.CurrentTrack?.Id ? 1 : 0;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Track track })
            ViewModel.RemoveFromQueue(track);
    }
}
