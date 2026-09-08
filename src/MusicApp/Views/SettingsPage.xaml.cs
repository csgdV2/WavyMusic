using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MusicApp.Services;
using MusicApp.ViewModels;

namespace MusicApp.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        ViewModel.RefreshCacheSummary();
    }

    private void AccentFlyout_Closed(object sender, object e) => ViewModel.CommitAccent();

    private void SystemAccent_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.UseSystemAccent();
        AccentFlyout.Hide();
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Clear saved songs?",
            Content = "The downloaded copies are deleted. Songs still play — they'll just stream again.",
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        ClearResult.Text = ViewModel.ClearCache();
        ClearResult.Visibility = Visibility.Visible;
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Windows.System.Launcher.LaunchFolderPathAsync(AppPaths.DataRoot);
        }
        catch
        {

        }
    }
}
