using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace MusicApp.Services;

public static class ThemeManager
{
    private static readonly List<string> Injected = new();

    private static readonly Color Base = Color.FromArgb(255, 8, 8, 10);
    private static readonly Color Raised = Color.FromArgb(255, 16, 16, 19);
    private static readonly Color Layer = Color.FromArgb(255, 21, 21, 25);
    private static readonly Color Card = Color.FromArgb(255, 26, 26, 31);
    private static readonly Color Stroke = Color.FromArgb(255, 45, 45, 52);

    private static UISettings? _ui;
    private static bool _settled;
    private static int _pass;

    public static SolidColorBrush AccentTint { get; } = new(SystemAccent);

    public static SolidColorBrush NeutralTint { get; } = new(Colors.White);

    public static SolidColorBrush Scrim { get; } = new(Colors.Black);

    public static Color SystemAccent
    {
        get
        {
            try
            {
                _ui ??= new UISettings();
                return _ui.GetColorValue(UIColorType.Accent);
            }
            catch
            {
                return Color.FromArgb(255, 0, 120, 212);
            }
        }
    }

    public static Color? Parse(string? hex)
    {
        var text = (hex ?? string.Empty).Trim().TrimStart('#');
        if (text.Length == 8)
            text = text[2..];

        if (text.Length != 6 ||
            !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            return null;

        return Color.FromArgb(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    public static string ToHex(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static void Apply(Window window, Panel root, AppTheme theme, string accent, bool ambient)
    {
        var resources = Application.Current.Resources;

        foreach (var key in Injected)
            resources.Remove(key);
        Injected.Clear();

        var darker = theme == AppTheme.Darker;
        var dark = theme is AppTheme.Dark or AppTheme.Darker
                   || (theme == AppTheme.System && Application.Current.RequestedTheme == ApplicationTheme.Dark);

        if (Parse(accent) is { } color)
            InjectAccent(resources, color, dark);

        if (darker)
            InjectDarker(resources);

        if (ambient)
            InjectAmbient(resources, dark);

        AccentTint.Color = Shade(Parse(accent) ?? SystemAccent, dark ? 0.38 : -0.32);
        NeutralTint.Color = dark ? Colors.White : Color.FromArgb(0xE4, 0, 0, 0);
        Scrim.Color = darker ? Base : dark ? Color.FromArgb(255, 12, 12, 14) : Colors.White;

        window.SystemBackdrop = darker || ambient ? null : new MicaBackdrop();
        root.Background = darker || ambient ? new SolidColorBrush(darker ? Base : dark ? Raised : Colors.White) : null;

        ApplyCaptionButtons(window, dark);

        var target = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark or AppTheme.Darker => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        Retheme(root, target);
    }

    private static void Retheme(Panel root, ElementTheme target)
    {
        if (!_settled)
        {
            _settled = true;
            root.RequestedTheme = target;
            return;
        }

        var effective = target != ElementTheme.Default
            ? target
            : Application.Current.RequestedTheme == ApplicationTheme.Dark
                ? ElementTheme.Dark
                : ElementTheme.Light;

        var flip = effective == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        var pass = ++_pass;

        root.RequestedTheme = flip;
        root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (pass == _pass)
                root.RequestedTheme = target;
        });
    }

    private static void ApplyCaptionButtons(Window window, bool dark)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
            return;

        try
        {
            var bar = window.AppWindow.TitleBar;
            var fore = dark ? Colors.White : Colors.Black;

            bar.ButtonForegroundColor = fore;
            bar.ButtonHoverForegroundColor = fore;
            bar.ButtonPressedForegroundColor = fore;
            bar.ButtonInactiveForegroundColor = Color.FromArgb(0x9B, fore.R, fore.G, fore.B);
            bar.ButtonHoverBackgroundColor = Color.FromArgb(0x22, fore.R, fore.G, fore.B);
            bar.ButtonPressedBackgroundColor = Color.FromArgb(0x3A, fore.R, fore.G, fore.B);
        }
        catch
        {
        }
    }

    private static void InjectAccent(ResourceDictionary resources, Color color, bool dark)
    {
        var light1 = Shade(color, 0.20);
        var light2 = Shade(color, 0.38);
        var light3 = Shade(color, 0.56);
        var dark1 = Shade(color, -0.16);
        var dark2 = Shade(color, -0.32);
        var dark3 = Shade(color, -0.48);

        var text = dark ? light2 : dark2;
        var over = dark ? light1 : dark1;
        var press = dark ? light3 : dark3;
        var on = Readable(color);

        Put(resources, "SystemAccentColor", color);
        Put(resources, "SystemAccentColorLight1", light1);
        Put(resources, "SystemAccentColorLight2", light2);
        Put(resources, "SystemAccentColorLight3", light3);
        Put(resources, "SystemAccentColorDark1", dark1);
        Put(resources, "SystemAccentColorDark2", dark2);
        Put(resources, "SystemAccentColorDark3", dark3);

        Brush(resources, "AccentFillColorDefaultBrush", color);
        Brush(resources, "AccentFillColorSecondaryBrush", color, 0.9);
        Brush(resources, "AccentFillColorTertiaryBrush", color, 0.8);

        Brush(resources, "AccentTextFillColorPrimaryBrush", text);
        Brush(resources, "AccentTextFillColorSecondaryBrush", text);
        Brush(resources, "AccentTextFillColorTertiaryBrush", text);

        Brush(resources, "AccentButtonBackground", color);
        Brush(resources, "AccentButtonBackgroundPointerOver", color, 0.9);
        Brush(resources, "AccentButtonBackgroundPressed", color, 0.8);
        Brush(resources, "AccentButtonForeground", on);
        Brush(resources, "AccentButtonForegroundPointerOver", on);
        Brush(resources, "AccentButtonForegroundPressed", on);

        Brush(resources, "SliderTrackValueFill", color);
        Brush(resources, "SliderTrackValueFillPointerOver", over);
        Brush(resources, "SliderTrackValueFillPressed", press);
        Brush(resources, "SliderThumbBackground", color);
        Brush(resources, "SliderThumbBackgroundPointerOver", over);
        Brush(resources, "SliderThumbBackgroundPressed", press);

        Brush(resources, "ProgressBarForeground", color);
        Brush(resources, "ProgressRingForeground", color);

        Brush(resources, "NavigationViewSelectionIndicatorForeground", color);
        Brush(resources, "ComboBoxItemPillFillBrush", color);
        Brush(resources, "ListViewItemSelectionIndicatorBrush", color);

        Brush(resources, "ToggleSwitchFillOn", color);
        Brush(resources, "ToggleSwitchFillOnPointerOver", over);
        Brush(resources, "ToggleSwitchFillOnPressed", press);
        Brush(resources, "ToggleSwitchStrokeOn", color);
        Brush(resources, "ToggleSwitchStrokeOnPointerOver", over);
        Brush(resources, "ToggleSwitchStrokeOnPressed", press);
        Brush(resources, "ToggleSwitchKnobFillOn", on);

        Brush(resources, "CheckBoxCheckBackgroundFillChecked", color);
        Brush(resources, "CheckBoxCheckBackgroundStrokeChecked", color);
        Brush(resources, "RadioButtonOuterEllipseCheckedFill", color);

        Brush(resources, "HyperlinkButtonForeground", text);
        Brush(resources, "HyperlinkButtonForegroundPointerOver", text);
        Brush(resources, "HyperlinkButtonForegroundPressed", text);

        Put(resources, "TextControlSelectionHighlightColor", new SolidColorBrush(color));
    }

    private static void InjectAmbient(ResourceDictionary resources, bool dark)
    {
        var tint = dark ? Colors.White : Colors.Black;
        var clear = Color.FromArgb(0, 0, 0, 0);

        Brush(resources, "NavigationViewDefaultPaneBackground", clear);
        Brush(resources, "NavigationViewExpandedPaneBackground", clear);
        Brush(resources, "NavigationViewContentBackground", clear);
        Brush(resources, "NavigationViewContentGridBorderBrush", clear);

        Brush(resources, "LayerFillColorDefaultBrush", Color.FromArgb(dark ? (byte)0x14 : (byte)0x0A, tint.R, tint.G, tint.B));
        Brush(resources, "LayerFillColorAltBrush", Color.FromArgb(dark ? (byte)0x10 : (byte)0x08, tint.R, tint.G, tint.B));
        Brush(resources, "CardBackgroundFillColorDefaultBrush", Color.FromArgb(dark ? (byte)0x1A : (byte)0x12, tint.R, tint.G, tint.B));
        Brush(resources, "CardBackgroundFillColorSecondaryBrush", Color.FromArgb(dark ? (byte)0x12 : (byte)0x0C, tint.R, tint.G, tint.B));
    }

    private static void InjectDarker(ResourceDictionary resources)
    {
        Brush(resources, "ApplicationPageBackgroundThemeBrush", Base);
        Brush(resources, "SolidBackgroundFillColorBaseBrush", Base);
        Brush(resources, "SolidBackgroundFillColorSecondaryBrush", Raised);
        Brush(resources, "SolidBackgroundFillColorTertiaryBrush", Layer);
        Brush(resources, "SolidBackgroundFillColorQuarternaryBrush", Card);
        Brush(resources, "SolidBackgroundFillColorBaseAltBrush", Base);

        Brush(resources, "LayerFillColorDefaultBrush", Layer);
        Brush(resources, "LayerFillColorAltBrush", Base);
        Brush(resources, "LayerOnAcrylicFillColorDefaultBrush", Layer);
        Brush(resources, "LayerOnMicaBaseAltFillColorDefaultBrush", Layer);

        Brush(resources, "CardBackgroundFillColorDefaultBrush", Card);
        Brush(resources, "CardBackgroundFillColorSecondaryBrush", Layer);
        Brush(resources, "CardStrokeColorDefaultBrush", Stroke);

        Brush(resources, "ControlFillColorDefaultBrush", Card);
        Brush(resources, "ControlFillColorSecondaryBrush", Color.FromArgb(255, 31, 31, 37));
        Brush(resources, "ControlFillColorTertiaryBrush", Layer);
        Brush(resources, "ControlFillColorInputActiveBrush", Raised);

        Brush(resources, "ControlStrokeColorDefaultBrush", Stroke);
        Brush(resources, "ControlStrokeColorSecondaryBrush", Stroke);
        Brush(resources, "DividerStrokeColorDefaultBrush", Stroke);
        Brush(resources, "SurfaceStrokeColorDefaultBrush", Stroke);
        Brush(resources, "SurfaceStrokeColorFlyoutBrush", Stroke);

        Brush(resources, "SubtleFillColorSecondaryBrush", Color.FromArgb(0x1C, 255, 255, 255));
        Brush(resources, "SubtleFillColorTertiaryBrush", Color.FromArgb(0x12, 255, 255, 255));

        Brush(resources, "AcrylicBackgroundFillColorDefaultBrush", Base);
        Brush(resources, "AcrylicInAppFillColorDefaultBrush", Base);

        Brush(resources, "NavigationViewDefaultPaneBackground", Base);
        Brush(resources, "NavigationViewExpandedPaneBackground", Base);
        Brush(resources, "NavigationViewContentBackground", Base);
        Brush(resources, "NavigationViewContentGridBorderBrush", Stroke);

        Brush(resources, "FlyoutPresenterBackground", Card);
        Brush(resources, "MenuFlyoutPresenterBackground", Card);
        Brush(resources, "ComboBoxDropDownBackground", Card);
        Brush(resources, "ContentDialogBackground", Layer);
        Brush(resources, "ContentDialogSmokeFill", Color.FromArgb(0x99, 0, 0, 0));
        Brush(resources, "TeachingTipBackground", Card);
        Brush(resources, "TooltipBackground", Card);
    }

    private static void Brush(ResourceDictionary resources, string key, Color color, double opacity = 1.0) =>
        Put(resources, key, new SolidColorBrush(color) { Opacity = opacity });

    private static void Put(ResourceDictionary resources, string key, object value)
    {
        resources[key] = value;
        Injected.Add(key);
    }

    private static Color Shade(Color color, double amount)
    {
        static byte Mix(byte channel, double amount) =>
            (byte)Math.Clamp(amount >= 0
                ? channel + (255 - channel) * amount
                : channel * (1 + amount), 0, 255);

        return Color.FromArgb(255, Mix(color.R, amount), Mix(color.G, amount), Mix(color.B, amount));
    }

    private static Color Readable(Color color) =>
        color.R * 0.299 + color.G * 0.587 + color.B * 0.114 > 150 ? Colors.Black : Colors.White;
}
