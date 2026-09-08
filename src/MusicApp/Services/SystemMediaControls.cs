using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using MusicApp.Core.Models;
using MusicApp.ViewModels;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;
using WinRT;

namespace MusicApp.Services;

public sealed class SystemMediaControls
{
    private const string RuntimeClassName = "Windows.Media.SystemMediaTransportControls";

    private static readonly Guid InteropIid = new("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a");
    private static readonly Guid ControlsIid = new("99fa3ff4-1742-42a6-902e-087d41f965ec");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly SystemMediaTransportControls _controls;
    private readonly PlayerViewModel _player;
    private readonly DispatcherQueue _dispatcher;

    private Track? _shown;
    private int _artVersion;

    private SystemMediaControls(SystemMediaTransportControls controls, PlayerViewModel player, DispatcherQueue dispatcher)
    {
        _controls = controls;
        _player = player;
        _dispatcher = dispatcher;

        _controls.IsEnabled = true;
        _controls.IsPlayEnabled = true;
        _controls.IsPauseEnabled = true;
        _controls.IsNextEnabled = true;
        _controls.IsPreviousEnabled = true;
        _controls.IsStopEnabled = false;
        _controls.DisplayUpdater.Type = MediaPlaybackType.Music;
        _controls.ButtonPressed += OnButtonPressed;

        _player.PropertyChanged += OnPlayerPropertyChanged;
        Refresh();
    }

    public static SystemMediaControls? Attach(Window window, PlayerViewModel player)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var controls = GetForWindow(hwnd);
            return controls is null ? null : new SystemMediaControls(controls, player, window.DispatcherQueue);
        }
        catch
        {
            return null;
        }
    }

    public void Detach()
    {
        _player.PropertyChanged -= OnPlayerPropertyChanged;

        try
        {
            _controls.ButtonPressed -= OnButtonPressed;
            _controls.PlaybackStatus = MediaPlaybackStatus.Closed;
            _controls.DisplayUpdater.ClearAll();
            _controls.DisplayUpdater.Update();
            _controls.IsEnabled = false;
        }
        catch
        {
        }
    }

    private static SystemMediaTransportControls? GetForWindow(IntPtr hwnd)
    {
        var name = IntPtr.Zero;
        var factory = IntPtr.Zero;
        object? interop = null;

        try
        {
            WindowsCreateString(RuntimeClassName, RuntimeClassName.Length, out name);

            var interopIid = InteropIid;
            RoGetActivationFactory(name, ref interopIid, out factory);
            if (factory == IntPtr.Zero)
                return null;

            interop = Marshal.GetObjectForIUnknown(factory);
            var controlsIid = ControlsIid;
            ((ISystemMediaTransportControlsInterop)interop).GetForWindow(hwnd, ref controlsIid, out var abi);

            return abi == IntPtr.Zero
                ? null
                : MarshalInspectable<SystemMediaTransportControls>.FromAbi(abi);
        }
        finally
        {
            if (interop is not null)
                Marshal.ReleaseComObject(interop);
            if (factory != IntPtr.Zero)
                Marshal.Release(factory);
            if (name != IntPtr.Zero)
                WindowsDeleteString(name);
        }
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.CurrentTrack)
            or nameof(PlayerViewModel.IsPlaying)
            or nameof(PlayerViewModel.HasTrack))
        {
            Refresh();
        }
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        var button = args.Button;
        _dispatcher.TryEnqueue(async () =>
        {
            switch (button)
            {
                case SystemMediaTransportControlsButton.Play:
                case SystemMediaTransportControlsButton.Pause:
                    if (MediaCommandGate.TryClaim(MediaCommandGate.PlayPause))
                        _player.PlayPauseCommand.Execute(null);
                    break;
                case SystemMediaTransportControlsButton.Next:
                    if (MediaCommandGate.TryClaim(MediaCommandGate.Next))
                        await _player.NextCommand.ExecuteAsync(null);
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    if (MediaCommandGate.TryClaim(MediaCommandGate.Previous))
                        await _player.PreviousCommand.ExecuteAsync(null);
                    break;
            }
        });
    }

    private void Refresh()
    {
        try
        {
            var track = _player.CurrentTrack;

            _controls.PlaybackStatus = track is null
                ? MediaPlaybackStatus.Closed
                : _player.IsPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;

            if (ReferenceEquals(track, _shown))
                return;

            _shown = track;
            var updater = _controls.DisplayUpdater;
            updater.ClearAll();
            updater.Type = MediaPlaybackType.Music;

            if (track is not null)
            {
                updater.MusicProperties.Title = track.Title;
                updater.MusicProperties.Artist = track.Artist ?? string.Empty;
                updater.MusicProperties.AlbumTitle = track.Album ?? string.Empty;
            }

            updater.Update();

            var version = ++_artVersion;
            if (!string.IsNullOrWhiteSpace(track?.ThumbnailUrl))
                _ = ShowArtAsync(track.ThumbnailUrl!, version);
        }
        catch
        {
        }
    }

    private async Task ShowArtAsync(string url, int version)
    {
        try
        {
            var path = await LocalArtAsync(url).ConfigureAwait(false);
            if (path is null || version != _artVersion)
                return;

            var file = await StorageFile.GetFileFromPathAsync(path);

            _dispatcher.TryEnqueue(() =>
            {

                if (version != _artVersion)
                    return;

                try
                {
                    _controls.DisplayUpdater.Thumbnail = RandomAccessStreamReference.CreateFromFile(file);
                    _controls.DisplayUpdater.Update();
                }
                catch
                {
                }
            });
        }
        catch
        {
        }
    }

    private static async Task<string?> LocalArtAsync(string url)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16];

        foreach (var existing in Directory.EnumerateFiles(AppPaths.ArtCache, key + ".*"))
        {
            if (new FileInfo(existing).Length > 0)
                return existing;
        }

        using var response = await Http.GetAsync(url).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        if (bytes.Length == 0)
            return null;

        var path = Path.Combine(AppPaths.ArtCache, key + Extension(response.Content.Headers.ContentType?.MediaType, url));
        var partial = path + ".part";
        await File.WriteAllBytesAsync(partial, bytes).ConfigureAwait(false);
        File.Move(partial, path, overwrite: true);
        return path;
    }

    private static string Extension(string? mediaType, string url) => mediaType switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/jpeg" => ".jpg",
        _ => Path.GetExtension(new Uri(url).GetLeftPart(UriPartial.Path)) is { Length: > 1 } fromUrl
            ? fromUrl
            : ".jpg",
    };

    [ComImport, Guid("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISystemMediaTransportControlsInterop
    {
        void GetIids(out uint iidCount, out IntPtr iids);
        void GetRuntimeClassName(out IntPtr className);
        void GetTrustLevel(out int trustLevel);
        void GetForWindow(IntPtr window, ref Guid iid, out IntPtr controls);
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, int length, out IntPtr result);

    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void WindowsDeleteString(IntPtr value);

    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
}
