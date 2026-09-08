using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using MusicApp.Core.Models;
using MusicApp.Services;
using MusicApp.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace MusicApp.Controls;

internal static class TrackMenu
{
    private static readonly string AddGlyph = char.ConvertFromUtf32(0xE710);
    private static readonly string PlayNextGlyph = char.ConvertFromUtf32(0xE893);
    private static readonly string QueueGlyph = char.ConvertFromUtf32(0xE8FD);
    private static readonly string PlaylistGlyph = char.ConvertFromUtf32(0xE90B);
    private static readonly string RemoveGlyph = char.ConvertFromUtf32(0xE738);
    private static readonly string LinkGlyph = char.ConvertFromUtf32(0xE71B);

    public static void ShowFor(FrameworkElement anchor, Track track, bool queueActions = true, Playlist? owner = null)
    {
        var playlists = App.Services.GetRequiredService<PlaylistService>();
        var player = App.Services.GetRequiredService<PlayerViewModel>();

        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };

        var addTo = new MenuFlyoutSubItem
        {
            Text = "Add to playlist",
            Icon = new FontIcon { Glyph = PlaylistGlyph },
        };

        foreach (var playlist in playlists.Playlists)
        {
            var contains = playlist.Tracks.Any(t => t.Id == track.Id);
            var item = new ToggleMenuFlyoutItem { Text = playlist.Name, IsChecked = contains };
            var target = playlist;
            item.Click += (_, _) =>
            {
                if (contains)
                    playlists.Remove(target, track);
                else
                    playlists.TryAdd(target, track);
            };
            addTo.Items.Add(item);
        }

        if (playlists.Playlists.Count > 0)
            addTo.Items.Add(new MenuFlyoutSeparator());

        var newPlaylist = new MenuFlyoutItem
        {
            Text = "New playlist…",
            Icon = new FontIcon { Glyph = AddGlyph },
        };
        newPlaylist.Click += async (_, _) => await AddToNewPlaylistAsync(anchor.XamlRoot, playlists, track);
        addTo.Items.Add(newPlaylist);

        menu.Items.Add(addTo);
        AddRemoveEntry(menu, playlists, track, owner);

        if (queueActions)
        {
            var playNext = new MenuFlyoutItem
            {
                Text = "Play next",
                Icon = new FontIcon { Glyph = PlayNextGlyph },
            };
            playNext.Click += async (_, _) => await player.PlayNextAsync(track);
            menu.Items.Add(playNext);

            var enqueue = new MenuFlyoutItem
            {
                Text = "Add to queue",
                Icon = new FontIcon { Glyph = QueueGlyph },
            };
            enqueue.Click += async (_, _) => await player.EnqueueAsync(track);
            menu.Items.Add(enqueue);
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        var copy = new MenuFlyoutItem
        {
            Text = "Copy link",
            Icon = new FontIcon { Glyph = LinkGlyph },
        };
        copy.Click += (_, _) =>
        {
            var data = new DataPackage();
            data.SetText(track.SourceUrl);
            Clipboard.SetContent(data);
        };
        menu.Items.Add(copy);

        menu.ShowAt(anchor);
    }

    public static void ShowForSender(object sender)
    {
        if (sender is FrameworkElement { DataContext: Track track } anchor)
            ShowFor(anchor, track);
    }

    private static void AddRemoveEntry(MenuFlyout menu, PlaylistService playlists, Track track, Playlist? owner)
    {
        if (owner is not null)
        {
            menu.Items.Add(RemoveItem("Remove from playlist", playlists, owner, track));
            return;
        }

        var holders = playlists.Playlists.Where(p => p.Tracks.Any(t => t.Id == track.Id)).ToList();
        if (holders.Count == 0)
            return;

        if (holders.Count == 1)
        {
            menu.Items.Add(RemoveItem($"Remove from “{holders[0].Name}”", playlists, holders[0], track));
            return;
        }

        var submenu = new MenuFlyoutSubItem
        {
            Text = "Remove from playlist",
            Icon = new FontIcon { Glyph = RemoveGlyph },
        };

        foreach (var holder in holders)
            submenu.Items.Add(RemoveItem(holder.Name, playlists, holder, track, icon: false));

        menu.Items.Add(submenu);
    }

    private static MenuFlyoutItem RemoveItem(
        string text, PlaylistService playlists, Playlist playlist, Track track, bool icon = true)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            Icon = icon ? new FontIcon { Glyph = RemoveGlyph } : null,
        };
        item.Click += (_, _) => playlists.Remove(playlist, track);
        return item;
    }

    public static void SetHoverActionsVisible(object sender, bool visible)
        => Fade(sender, "RowActions", visible);

    public static void SetHoverPlateVisible(object sender, bool visible)
        => Fade(sender, "HoverPlate", visible);

    private static void Fade(object sender, string name, bool visible)
    {
        if (sender is not FrameworkElement root)
            return;

        var target = root.FindName(name) as UIElement ?? FindNamed(root, name);
        if (target is not null)
            target.Opacity = visible ? 1 : 0;
    }

    private static UIElement? FindNamed(DependencyObject root, string name)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element && element.Name == name)
                return element;
            if (FindNamed(child, name) is { } nested)
                return nested;
        }
        return null;
    }

    private static async Task AddToNewPlaylistAsync(XamlRoot? xamlRoot, PlaylistService playlists, Track track)
    {
        if (xamlRoot is null)
            return;

        var input = new TextBox
        {
            Text = playlists.SuggestName(),
            SelectionStart = 0,
        };
        input.SelectionLength = input.Text.Length;

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "New playlist",
            Content = input,
            PrimaryButtonText = "Create",
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
        if (!committed || name.Length == 0)
            return;

        playlists.TryAdd(playlists.Create(name), track);
    }
}
