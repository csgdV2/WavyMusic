using CommunityToolkit.Mvvm.ComponentModel;
using Wavy.Core.Services;

namespace Wavy.Services;

public sealed partial class AppUpdateService : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly GitHubUpdateClient _client;
    private readonly UpdateInstallerLauncher _installerLauncher;
    private CancellationTokenSource? _operation;
    private string? _installer;
    private AppRelease? _release;
    private static Version InstalledVersion => typeof(App).Assembly.GetName().Version ?? new Version(2, 0, 0);

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasUpdate;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NotificationVisibility))] private bool _isUpdateNotificationVisible;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private string _status = "Checks GitHub releases for newer versions of Wavy.";

    public AppUpdateService(SettingsService settings, GitHubUpdateClient client, UpdateInstallerLauncher? installerLauncher = null)
    {
        _settings = settings;
        _client = client;
        _installerLauncher = installerLauncher ?? new UpdateInstallerLauncher();
    }

    public bool AutomaticChecks
    {
        get => _settings.AutomaticUpdateChecks;
        set { _settings.AutomaticUpdateChecks = value; OnPropertyChanged(); }
    }

    public Microsoft.UI.Xaml.Visibility NotificationVisibility => IsUpdateNotificationVisible ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public bool CanAct => !IsBusy;
    public string ActionLabel => IsInstalling ? "Installing update…" : IsDownloading ? "Downloading update…"
        : _release is null ? "Check for updates" : _installer is null ? "Download update" : "Retry update";
    public bool CanCancel => IsBusy && !IsInstalling;
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanAct)); OnPropertyChanged(nameof(CanCancel)); }
    partial void OnIsDownloadingChanged(bool value) => OnPropertyChanged(nameof(ActionLabel));
    partial void OnIsInstallingChanged(bool value) { OnPropertyChanged(nameof(ActionLabel)); OnPropertyChanged(nameof(CanCancel)); }

    public async Task CheckAtStartupAsync()
    {
        if (!AutomaticChecks) return;
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
        IsBusy = true;
        _operation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        try
        {
            if (_installer is null)
            {
                IsDownloading = true;
                DownloadProgress = 0;
                Status = "Downloading update…";
                _installer = await _client.DownloadAsync(_release, Path.Combine(AppPaths.DataRoot, "updates"),
                    new Progress<double>(value => DownloadProgress = value), _operation.Token);
            }
            IsDownloading = false;
            IsInstalling = true;
            Status = "Verifying update…";
            if (!await GitHubUpdateClient.VerifyAsync(_installer, _release, _operation.Token))
            {
                _installer = null;
                throw new InvalidDataException("Installer verification failed. Download the update again.");
            }
            Status = "Installing update… Wavy will restart automatically.";
            await _installerLauncher.LaunchAsync(_installer, _release, _operation.Token);
            IsUpdateNotificationVisible = false;
            App.MainWindow?.Close();
        }
        catch (OperationCanceledException) { Status = "Update canceled or timed out. You can try again."; }
        catch (Exception ex) { Status = $"Couldn't update Wavy: {ex.Message}"; }
        finally
        {
            IsInstalling = IsDownloading = IsBusy = false;
            _operation?.Dispose();
            _operation = null;
            OnPropertyChanged(nameof(ActionLabel));
        }
    }

    public void Cancel() { if (CanCancel) _operation?.Cancel(); }

    public void DismissNotification() => IsUpdateNotificationVisible = false;

}
