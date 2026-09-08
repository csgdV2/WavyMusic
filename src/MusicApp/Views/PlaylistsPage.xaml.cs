using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MusicApp.Controls;
using MusicApp.Core.Models;
using MusicApp.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace MusicApp.Views;

public sealed partial class PlaylistsPage : Page
{
    public PlaylistsViewModel ViewModel { get; }

    public PlaylistsPage()
    {
        ViewModel = App.Services.GetRequiredService<PlaylistsViewModel>();
        InitializeComponent();
        Loaded += (_, _) => ViewModel.Refresh();
    }

    public void Select(Playlist playlist) => ViewModel.Selected = playlist;

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        if (await PromptForNameAsync("New playlist", ViewModel.SuggestedName) is { } name)
            ViewModel.Create(name);
    }

    private async void Import_Click(object sender, RoutedEventArgs e) =>
        await ImportDialogAsync(
            "Import from YouTube Music",
            "https://music.youtube.com/playlist?list=…",
            "Paste a link to a public playlist. Its songs are copied into a playlist here — the original isn't touched.",
            link => ViewModel.ImportAsync(link));

    private async void ImportSpotify_Click(object sender, RoutedEventArgs e) =>
        await ImportDialogAsync(
            "Import from Spotify",
            "https://open.spotify.com/playlist/…",
            "Paste a link to a public Spotify playlist or album. Each song is matched on YouTube Music, so a few may differ.",
            link => ViewModel.ImportSpotifyAsync(link));

    private async Task ImportDialogAsync(
        string title, string placeholder, string blurb, Func<string, Task<string?>> import)
    {
        var input = new TextBox
        {
            PlaceholderText = placeholder,

            Text = await ClipboardLinkAsync(),
        };
        input.SelectAll();

        var status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Foreground = Brush("SystemFillColorCriticalBrush"),
        };

        var ring = new ProgressRing { Width = 18, Height = 18, IsActive = false };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            PrimaryButtonText = "Import",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = blurb,
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.8,
                    },
                    input,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { ring, status },
                    },
                },
            },
        };

        dialog.PrimaryButtonClick += async (_, args) =>
        {

            args.Cancel = true;
            var deferral = args.GetDeferral();
            try
            {
                ring.IsActive = true;
                status.Visibility = Visibility.Collapsed;
                dialog.IsPrimaryButtonEnabled = false;

                var error = await import(input.Text);
                if (error is null)
                {
                    args.Cancel = false;
                }
                else
                {
                    status.Text = error;
                    status.Visibility = Visibility.Visible;
                }
            }
            finally
            {
                ring.IsActive = false;
                dialog.IsPrimaryButtonEnabled = true;
                deferral.Complete();
            }
        };

        await dialog.ShowAsync();
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } playlist)
            return;

        if (await PromptForNameAsync("Rename playlist", playlist.Name) is { } name)
            ViewModel.Rename(name);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } playlist)
            return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Delete “{playlist.Name}”?",
            Content = "The playlist is removed. The songs themselves stay available.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            ViewModel.DeleteSelectedCommand.Execute(null);
    }

    private async void Tracks_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Track track)
            await ViewModel.PlayFromAsync(track);
    }

    private void RemoveTrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { DataContext: Track track })
            ViewModel.Remove(track);
    }

    private void Row_PointerEntered(object sender, PointerRoutedEventArgs e)
        => TrackMenu.SetHoverActionsVisible(sender, true);

    private void Row_PointerExited(object sender, PointerRoutedEventArgs e)
        => TrackMenu.SetHoverActionsVisible(sender, false);

    private void RowActions_Click(object sender, RoutedEventArgs e)
    {

        if (sender is FrameworkElement { DataContext: Track track } anchor)
            TrackMenu.ShowFor(anchor, track, owner: ViewModel.Selected);
    }

    private async Task<string?> PromptForNameAsync(string title, string initial)
    {
        var input = new TextBox { Text = initial, SelectionStart = 0, SelectionLength = initial.Length };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = input,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        var committed = false;
        input.KeyDown += (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Enter)
                return;

            committed = true;
            args.Handled = true;
            dialog.Hide();
        };

        committed |= await dialog.ShowAsync() == ContentDialogResult.Primary;

        var name = input.Text.Trim();
        return committed && name.Length > 0 ? name : null;
    }

    private static async Task<string> ClipboardLinkAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
                return string.Empty;

            var text = (await content.GetTextAsync()).Trim();
            return text.Contains("list=", StringComparison.OrdinalIgnoreCase)
                   && text.Contains("youtu", StringComparison.OrdinalIgnoreCase)
                ? text
                : string.Empty;
        }
        catch
        {

            return string.Empty;
        }
    }

    private static Brush? Brush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;
}
