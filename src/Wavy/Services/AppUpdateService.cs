using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Wavy.Core.Services;

namespace Wavy.Services;

public sealed partial class AppUpdateService : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly GitHubUpdateClient _client;
    private CancellationTokenSource? _operation;
    private string? _installer;
    private AppRelease? _release;
    private static Version InstalledVersion => typeof(App).Assembly.GetName().Version ?? new Version(1, 9, 0);

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasUpdate;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NotificationVisibility))] private bool _isUpdateNotificationVisible;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private string _status = "Checks GitHub releases for newer versions of Wavy.";

    public AppUpdateService(SettingsService settings, GitHubUpdateClient client)
    {
        _settings = settings;
        _client = client;
    }

    public bool AutomaticChecks
    {
        get => _settings.AutomaticUpdateChecks;
        set { _settings.AutomaticUpdateChecks = value; OnPropertyChanged(); }
    }

    public Microsoft.UI.Xaml.Visibility NotificationVisibility => IsUpdateNotificationVisible ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public bool CanAct => !IsBusy;
    public string ActionLabel => _release is null ? "Check for updates" : _installer is null ? "Download update" : "Install and restart";
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanAct));

    public async Task CheckAtStartupAsync()
    {
        if (!AutomaticChecks || DateTimeOffset.UtcNow - _settings.LastUpdateCheck < TimeSpan.FromHours(12)) return;
        await CheckAsync();
    }

    private async Task CheckAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        _operation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Status = "Checking for updates…";
        try
        {
            var release = await _client.CheckAsync(InstalledVersion, _operation.Token);
            _settings.LastUpdateCheck = DateTimeOffset.UtcNow;
            if (_release?.Tag != release?.Tag)
            {
                _installer = null;
                IsUpdateNotificationVisible = release is not null;
            }
            _release = release;
            HasUpdate = release is not null;
            Status = release is null ? "You're up to date." : $"Wavy {release.Tag.TrimStart('v', 'V')} is available.";
        }
        catch (OperationCanceledException) { Status = "The update check was canceled or timed out. Try again."; }
        catch (Exception ex) { Status = $"Couldn't check for updates: {ex.Message}"; }
        finally { IsBusy = false; _operation.Dispose(); _operation = null; OnPropertyChanged(nameof(ActionLabel)); }
    }

    public async Task ActAsync()
    {
        if (IsBusy) return;
        if (_release is null) { await CheckAsync(); return; }
        if (_installer is not null) { await InstallAsync(); return; }
        IsBusy = IsDownloading = true;
        DownloadProgress = 0;
        _operation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        Status = "Downloading update…";
        try
        {
            _installer = await _client.DownloadAsync(_release, Path.Combine(AppPaths.DataRoot, "updates"),
                new Progress<double>(value => DownloadProgress = value), _operation.Token);
            Status = "Update downloaded and verified. Install when you're ready to restart Wavy.";
        }
        catch (OperationCanceledException) { Status = "Download canceled. You can try again."; }
        catch (Exception ex) { Status = $"Couldn't download the update: {ex.Message}"; }
        finally { IsDownloading = IsBusy = false; _operation.Dispose(); _operation = null; OnPropertyChanged(nameof(ActionLabel)); }
    }

    public void Cancel() => _operation?.Cancel();

    public void DismissNotification() => IsUpdateNotificationVisible = false;

    private async Task InstallAsync()
    {
        if (_installer is null || _release is null) return;
        IsBusy = true;
        Status = "Verifying installer…";
        try
        {
            if (!await GitHubUpdateClient.VerifyAsync(_installer, _release))
            {
                _installer = null;
                throw new InvalidDataException("Installer verification failed. Download the update again.");
            }
            var folder = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var process = Process.Start(new ProcessStartInfo(_installer)
            {
                UseShellExecute = true,
                Arguments = $"/SP- /SILENT /NORESTART /CLOSEAPPLICATIONS /WAVYUPDATE=1 /DIR=\"{folder}\""
            });
            if (process is null) throw new IOException("The installer could not be started.");
            process.Dispose();
            App.MainWindow?.Close();
        }
        catch (Exception ex) { Status = $"Couldn't install the update: {ex.Message}"; }
        finally { IsBusy = false; OnPropertyChanged(nameof(ActionLabel)); }
    }
}
