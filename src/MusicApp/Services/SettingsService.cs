using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MusicApp.Core.Models;

namespace MusicApp.Services;

public enum AppTheme
{
    System,
    Light,
    Dark,
    Darker,
}

public enum DiscordIdentity
{
    YouTubeMusic,
    Music,
}

public enum MenuLayout
{
    Sidebar,
    Bottom,
}

public sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized);

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly Stored _stored;

    public SettingsService(string dataRoot)
    {
        _path = Path.Combine(dataRoot, "settings.json");
        _stored = Load();
    }

    public event EventHandler<string>? Changed;

    public AppTheme Theme
    {
        get => _stored.Theme;
        set
        {
            if (_stored.Theme == value)
                return;
            _stored.Theme = value;
            Commit(nameof(Theme));
        }
    }

    public string AccentColor
    {
        get => _stored.AccentColor;
        set
        {
            var hex = ThemeManager.Parse(value) is { } color ? ThemeManager.ToHex(color) : string.Empty;
            if (_stored.AccentColor == hex)
                return;
            _stored.AccentColor = hex;
            Commit(nameof(AccentColor));
        }
    }

    public bool OpenSidebarOnStartup
    {
        get => _stored.OpenSidebarOnStartup;
        set
        {
            if (_stored.OpenSidebarOnStartup == value)
                return;
            _stored.OpenSidebarOnStartup = value;
            Commit(nameof(OpenSidebarOnStartup));
        }
    }

    public bool AmbientBackground
    {
        get => _stored.AmbientBackground;
        set
        {
            if (_stored.AmbientBackground == value)
                return;
            _stored.AmbientBackground = value;
            Commit(nameof(AmbientBackground));
        }
    }

    public bool PrefetchNext
    {
        get => _stored.PrefetchNext;
        set
        {
            if (_stored.PrefetchNext == value)
                return;
            _stored.PrefetchNext = value;
            Commit(nameof(PrefetchNext));
        }
    }

    public int KeepOfflineDays
    {
        get => _stored.KeepOfflineDays;
        set
        {
            var days = Math.Max(0, value);
            if (_stored.KeepOfflineDays == days)
                return;
            _stored.KeepOfflineDays = days;
            Commit(nameof(KeepOfflineDays));
        }
    }

    public double Volume
    {
        get => _stored.Volume;
        set
        {
            var volume = Math.Clamp(value, 0, 1);
            if (Math.Abs(_stored.Volume - volume) < 0.001)
                return;
            _stored.Volume = volume;
            Commit(nameof(Volume));
        }
    }

    public int VolumeStepPercent
    {
        get => _stored.VolumeStepPercent == 1 ? 1 : 5;
        set
        {
            var step = value == 1 ? 1 : 5;
            if (_stored.VolumeStepPercent == step)
                return;
            _stored.VolumeStepPercent = step;
            Commit(nameof(VolumeStepPercent));
        }
    }

    public bool DiscordPresence
    {
        get => _stored.DiscordPresence;
        set
        {
            if (_stored.DiscordPresence == value)
                return;
            _stored.DiscordPresence = value;
            Commit(nameof(DiscordPresence));
        }
    }

    public DiscordIdentity DiscordIdentity
    {
        get => _stored.DiscordIdentity;
        set
        {
            if (_stored.DiscordIdentity == value)
                return;
            _stored.DiscordIdentity = value;
            Commit(nameof(DiscordIdentity));
        }
    }

    public string DiscordApplicationId => _stored.DiscordIdentity switch
    {
        DiscordIdentity.Music => "1542630837483343955",
        _ => "1542630455248167002",
    };

    public MenuLayout MenuLayout
    {
        get => _stored.MenuLayout;
        set
        {
            if (_stored.MenuLayout == value)
                return;
            _stored.MenuLayout = value;
            Commit(nameof(MenuLayout));
        }
    }

    public LyricsSource LyricsSource
    {
        get => _stored.LyricsSource;
        set
        {
            if (_stored.LyricsSource == value)
                return;
            _stored.LyricsSource = value;
            Commit(nameof(LyricsSource));
        }
    }

    public WindowPlacement? Placement
    {
        get => _stored.WindowWidth > 0 && _stored.WindowHeight > 0
            ? new WindowPlacement(_stored.WindowX, _stored.WindowY, _stored.WindowWidth, _stored.WindowHeight, _stored.WindowMaximized)
            : null;
        set
        {
            if (value is null || value == Placement)
                return;
            _stored.WindowX = value.X;
            _stored.WindowY = value.Y;
            _stored.WindowWidth = value.Width;
            _stored.WindowHeight = value.Height;
            _stored.WindowMaximized = value.Maximized;

            Save();
        }
    }

    private void Commit(string name)
    {
        Save();
        Changed?.Invoke(this, name);
    }

    private Stored Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path), JsonOptions) ?? new()
                : new();
        }
        catch
        {

            return new();
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_stored, JsonOptions));
        }
        catch
        {

        }
    }

    private sealed class Stored
    {
        public AppTheme Theme { get; set; } = AppTheme.System;
        public string AccentColor { get; set; } = string.Empty;
        public bool OpenSidebarOnStartup { get; set; }
        public bool AmbientBackground { get; set; } = true;
        public bool PrefetchNext { get; set; } = true;

        public int KeepOfflineDays { get; set; } = 1;
        public double Volume { get; set; } = 1.0;
        public int VolumeStepPercent { get; set; } = 1;
        public bool DiscordPresence { get; set; } = true;
        public DiscordIdentity DiscordIdentity { get; set; } = DiscordIdentity.Music;
        public MenuLayout MenuLayout { get; set; } = MenuLayout.Bottom;
        public LyricsSource LyricsSource { get; set; } = LyricsSource.Auto;

        public int WindowX { get; set; }
        public int WindowY { get; set; }
        public int WindowWidth { get; set; }
        public int WindowHeight { get; set; }
        public bool WindowMaximized { get; set; }
    }
}
