using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using MusicApp.Core.Models;
using MusicApp.Services;
using Windows.UI;

namespace MusicApp.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly CacheService _cache;

    private static readonly int[] KeepDays = { 1, 7, 30, 0 };

    private Color? _pendingAccent;

    public SettingsViewModel(SettingsService settings, CacheService cache)
    {
        _settings = settings;
        _cache = cache;
    }

    public int ThemeIndex
    {
        get => (int)_settings.Theme;
        set
        {
            if (value < 0 || value == ThemeIndex)
                return;
            _settings.Theme = (AppTheme)value;
            OnPropertyChanged();
        }
    }

    public Color AccentColor
    {
        get => _pendingAccent ?? ThemeManager.Parse(_settings.AccentColor) ?? ThemeManager.SystemAccent;
        set
        {
            if (value == AccentColor)
                return;
            _pendingAccent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AccentBrush));
        }
    }

    public Brush AccentBrush => new SolidColorBrush(AccentColor);

    public string AccentLabel =>
        ThemeManager.Parse(_settings.AccentColor) is { } color ? ThemeManager.ToHex(color) : "Windows accent";

    public void CommitAccent()
    {
        if (_pendingAccent is not { } color)
            return;

        _pendingAccent = null;
        _settings.AccentColor = ThemeManager.ToHex(color);
        RaiseAccent();
    }

    public void UseSystemAccent()
    {
        _pendingAccent = null;
        _settings.AccentColor = string.Empty;
        RaiseAccent();
    }

    private void RaiseAccent()
    {
        OnPropertyChanged(nameof(AccentColor));
        OnPropertyChanged(nameof(AccentBrush));
        OnPropertyChanged(nameof(AccentLabel));
    }

    public bool OpenSidebarOnStartup
    {
        get => _settings.OpenSidebarOnStartup;
        set
        {
            if (value == _settings.OpenSidebarOnStartup)
                return;
            _settings.OpenSidebarOnStartup = value;
            OnPropertyChanged();
        }
    }

    public bool AmbientBackground
    {
        get => _settings.AmbientBackground;
        set
        {
            if (value == _settings.AmbientBackground)
                return;
            _settings.AmbientBackground = value;
            OnPropertyChanged();
        }
    }

    public bool PrefetchNext
    {
        get => _settings.PrefetchNext;
        set
        {
            if (value == _settings.PrefetchNext)
                return;
            _settings.PrefetchNext = value;
            OnPropertyChanged();
        }
    }

    public int VolumeStepIndex
    {
        get => _settings.VolumeStepPercent == 1 ? 0 : 1;
        set
        {
            if (value < 0)
                return;
            var step = value == 0 ? 1 : 5;
            if (step == _settings.VolumeStepPercent)
                return;
            _settings.VolumeStepPercent = step;
            OnPropertyChanged();
        }
    }

    public bool DiscordPresence
    {
        get => _settings.DiscordPresence;
        set
        {
            if (value == _settings.DiscordPresence)
                return;
            _settings.DiscordPresence = value;
            OnPropertyChanged();
        }
    }

    public int DiscordIdentityIndex
    {
        get => (int)_settings.DiscordIdentity;
        set
        {
            if (value < 0 || value == DiscordIdentityIndex)
                return;
            _settings.DiscordIdentity = (DiscordIdentity)value;
            OnPropertyChanged();
        }
    }

    public int MenuLayoutIndex
    {
        get => (int)_settings.MenuLayout;
        set
        {
            if (value < 0 || value == MenuLayoutIndex)
                return;
            _settings.MenuLayout = (MenuLayout)value;
            OnPropertyChanged();
        }
    }

    public int LyricsSourceIndex
    {
        get => (int)_settings.LyricsSource;
        set
        {
            if (value < 0 || value == LyricsSourceIndex)
                return;
            _settings.LyricsSource = (LyricsSource)value;
            OnPropertyChanged();
        }
    }

    public int KeepIndex
    {
        get
        {
            var index = Array.IndexOf(KeepDays, _settings.KeepOfflineDays);
            return index >= 0 ? index : 0;
        }
        set
        {
            if (value < 0 || value >= KeepDays.Length || value == KeepIndex)
                return;
            _settings.KeepOfflineDays = KeepDays[value];
            OnPropertyChanged();
        }
    }

    public string CacheSummary
    {
        get
        {
            var (count, bytes) = _cache.Usage();
            if (count == 0)
                return "Nothing saved offline yet.";

            var songs = count == 1 ? "1 song" : $"{count} songs";
            return $"{songs}, {Size(bytes)} on disk.";
        }
    }

    public string ClearCache()
    {
        var removed = _cache.ClearAll();
        OnPropertyChanged(nameof(CacheSummary));
        return removed switch
        {
            0 => "There was nothing to clear.",
            1 => "Removed 1 saved song.",
            var n => $"Removed {n} saved songs.",
        };
    }

    public void RefreshCacheSummary() => OnPropertyChanged(nameof(CacheSummary));

    public string Version =>
        typeof(SettingsViewModel).Assembly.GetName().Version is { } v
            ? $"Version {v.Major}.{v.Minor}.{v.Build}"
            : "Version unknown";

    public string Author => "by csgd";

    public string DataFolder => AppPaths.DataRoot;

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.#} KB",
        _ => $"{bytes} bytes",
    };
}
