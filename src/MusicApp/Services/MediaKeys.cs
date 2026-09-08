using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using MusicApp.ViewModels;

namespace MusicApp.Services;

public sealed class MediaKeys
{
    private const int GwlpWndProc = -4;
    private const uint WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;

    private const int IdNext = 0xB000;
    private const int IdPrevious = 0xB001;
    private const int IdPlayPause = 0xB002;

    private const uint VkMediaNextTrack = 0xB0;
    private const uint VkMediaPrevTrack = 0xB1;
    private const uint VkMediaPlayPause = 0xB3;

    private readonly IntPtr _hwnd;
    private readonly PlayerViewModel _player;
    private readonly DispatcherQueue _dispatcher;
    private readonly WindowProc _proc;
    private readonly List<int> _registered = new();

    private IntPtr _previousProc;
    private bool _detached;

    private MediaKeys(IntPtr hwnd, PlayerViewModel player, DispatcherQueue dispatcher)
    {
        _hwnd = hwnd;
        _player = player;
        _dispatcher = dispatcher;

        _proc = OnMessage;
        _previousProc = SetWindowLongPtr(_hwnd, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(_proc));

        Register(IdNext, VkMediaNextTrack);
        Register(IdPrevious, VkMediaPrevTrack);
        Register(IdPlayPause, VkMediaPlayPause);
    }

    public static MediaKeys? Attach(Window window, PlayerViewModel player)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            return new MediaKeys(hwnd, player, window.DispatcherQueue);
        }
        catch
        {
            return null;
        }
    }

    public void Detach()
    {
        if (_detached)
            return;

        _detached = true;

        foreach (var id in _registered)
            UnregisterHotKey(_hwnd, id);
        _registered.Clear();

        if (_previousProc != IntPtr.Zero)
        {
            SetWindowLongPtr(_hwnd, GwlpWndProc, _previousProc);
            _previousProc = IntPtr.Zero;
        }
    }

    private void Register(int id, uint key)
    {
        if (RegisterHotKey(_hwnd, id, ModNoRepeat, key))
            _registered.Add(id);
    }

    private IntPtr OnMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmHotkey)
        {
            switch (wParam.ToInt32())
            {
                case IdNext:
                    Invoke(MediaCommandGate.Next);
                    return IntPtr.Zero;
                case IdPrevious:
                    Invoke(MediaCommandGate.Previous);
                    return IntPtr.Zero;
                case IdPlayPause:
                    Invoke(MediaCommandGate.PlayPause);
                    return IntPtr.Zero;
            }
        }

        return CallWindowProc(_previousProc, hwnd, message, wParam, lParam);
    }

    private void Invoke(string command) => _dispatcher.TryEnqueue(async () =>
    {
        if (!MediaCommandGate.TryClaim(command))
            return;

        switch (command)
        {
            case MediaCommandGate.Next:
                await _player.NextCommand.ExecuteAsync(null);
                break;
            case MediaCommandGate.Previous:
                await _player.PreviousCommand.ExecuteAsync(null);
                break;
            case MediaCommandGate.PlayPause:
                _player.PlayPauseCommand.Execute(null);
                break;
        }
    });

    private delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
