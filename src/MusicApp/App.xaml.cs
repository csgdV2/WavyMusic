using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using MusicApp.Core.Abstractions;
using MusicApp.Core.Services;
using MusicApp.Services;
using MusicApp.ViewModels;

namespace MusicApp;

public partial class App : Application
{

    public static IServiceProvider Services { get; private set; } = null!;

    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();

        UnhandledException += (_, e) =>
        {
            LogCrash("UI", e.Exception);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash("Domain", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("Task", e.Exception);
            e.SetObserved();
        };
    }

    private static void LogCrash(string origin, Exception? error)
    {
        if (error is null)
            return;

        try
        {
            var path = Path.Combine(AppPaths.DataRoot, "crash.log");
            Directory.CreateDirectory(AppPaths.DataRoot);
            File.AppendAllText(path,
                $"[{DateTime.Now:u}] {origin}: {error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Services = ConfigureServices();

        _ = Task.Run(() => Services.GetRequiredService<IYtDlpService>().PrewarmAsync());

        var settings = Services.GetRequiredService<SettingsService>();
        if (settings.KeepOfflineDays > 0)
        {
            var maxAge = TimeSpan.FromDays(settings.KeepOfflineDays);
            _ = Task.Run(() => Services.GetRequiredService<CacheService>().EvictStale(maxAge));
        }

        MainWindow = new MainWindow();
        MainWindow.Activate();

        Services.GetRequiredService<DiscordPresenceService>();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton(DispatcherQueue.GetForCurrentThread());

        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IToolLocator>(
            new ToolProvisioner(AppPaths.BundledYtDlp, AppPaths.DownloadedYtDlp));
        services.AddSingleton<IYtDlpService>(sp =>
            new YtDlpService(sp.GetRequiredService<IProcessRunner>(),
                sp.GetRequiredService<IToolLocator>(), AppPaths.Ffmpeg));
        services.AddSingleton(sp =>
            new CacheService(sp.GetRequiredService<IYtDlpService>(), AppPaths.AudioCache));

        services.AddSingleton(sp =>
            new SpotifyImportService(sp.GetRequiredService<IYtDlpService>()));

        services.AddSingleton(new PlaylistService(AppPaths.DataRoot));

        services.AddSingleton(new FavoritesService(AppPaths.DataRoot));
        services.AddSingleton(new SettingsService(AppPaths.DataRoot));

        services.AddSingleton<PlaybackService>();
        services.AddSingleton<PlayerViewModel>();
        services.AddSingleton<LyricsViewModel>();

        services.AddSingleton<DiscordPresenceService>();

        services.AddSingleton<SearchViewModel>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<LibraryViewModel>();

        services.AddTransient<ArtistViewModel>();
        services.AddTransient<AlbumViewModel>();
        services.AddTransient<AlbumsViewModel>();
        services.AddTransient<OnlinePlaylistViewModel>();
        services.AddTransient<PlaylistsViewModel>();
        services.AddTransient<SettingsViewModel>();

        return services.BuildServiceProvider();
    }
}
