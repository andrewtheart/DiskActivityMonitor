using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using DiskActivityMonitor.Core.Configuration;
using DiskActivityMonitor.Core.Updates;

namespace DiskActivityMonitor.Tray;

public partial class MainWindow
{
    private AppUpdateCheckResult? _pendingAppUpdateCheck;
    private AppReleaseInfo? _pendingAppUpdateRelease;
    private string? _downloadedUpdateInstaller;
    private CancellationTokenSource? _appUpdateCts;
    private bool _appUpdateBusy;

    internal Func<string, CancellationToken, Task<AppUpdateCheckResult?>> AppUpdateCheck =
        (version, cancellationToken) => AppUpdateChecker.CheckLatestAsync(version, cancellationToken: cancellationToken);

    internal Func<AppReleaseAsset, string, int, IProgress<AppUpdateDownloadProgress>?, CancellationToken, Task<string>>
        AppUpdateDownload = (asset, destination, maximumMb, progress, cancellationToken) =>
            AppUpdateDownloader.DownloadAsync(asset, destination, maximumMb, progress, cancellationToken: cancellationToken);

    internal Func<string, AppReleaseAsset, CancellationToken, Task<VerifiedAppUpdateInstaller?>> AppUpdateOpenVerifiedInstaller =
        AppUpdateChecker.OpenVerifiedAssetAsync;

    internal Func<string, bool> AppUpdateInstallerLauncher = path =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" }) is not null;

    internal Func<ProcessStartInfo, Process?> AppUpdateShellLauncher = Process.Start;

    internal Action AppUpdateExit = () => System.Windows.Application.Current.Shutdown();
    internal Action AutomaticUpdateCheckRequested { get; set; } = () => { };
    internal Action AppUpdateConsentCompleted { get; set; } = () => { };

    internal static bool ShouldPromptAppUpdateConsent(UserSettings settings)
        => settings.AppUpdateCheckMode == AppUpdateCheckMode.Prompt;

    internal static string CurrentAppVersion()
    {
        return CurrentAppVersion(typeof(MainWindow).Assembly);
    }

    internal static string CurrentAppVersion(Assembly assembly)
    {
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return SelectCurrentAppVersion(informational, assembly.GetName().Version);
    }

    internal static string SelectCurrentAppVersion(string? informational, Version? assemblyVersion)
    {
        if (AppUpdateChecker.TryParseVersion(informational, out Version version))
            return version.ToString();
        return assemblyVersion is null ? "0.0.0" : assemblyVersion.ToString(3);
    }

    internal void ShowAppUpdateConsent()
    {
        AppUpdateConsentOverlay.Visibility = Visibility.Visible;
        AppUpdateConsentOverlay.Focus();
    }

    private void AppUpdateConsentAutomatic_Click(object sender, RoutedEventArgs e)
        => ChooseAppUpdateMode(AppUpdateCheckMode.Automatic);

    private void AppUpdateConsentManual_Click(object sender, RoutedEventArgs e)
        => ChooseAppUpdateMode(AppUpdateCheckMode.Manual);

    private void AppUpdateConsentOff_Click(object sender, RoutedEventArgs e)
        => ChooseAppUpdateMode(AppUpdateCheckMode.Off);

    private void AppUpdateConsentClose_Click(object sender, RoutedEventArgs e)
    {
        AppUpdateConsentOverlay.Visibility = Visibility.Collapsed;
        AppUpdateConsentCompleted();
    }

    private void ChooseAppUpdateMode(AppUpdateCheckMode mode)
    {
        _userSettings.Update(settings => settings.AppUpdateCheckMode = mode);
        AppUpdateConsentOverlay.Visibility = Visibility.Collapsed;
        LoadAppUpdateSettings(_userSettings.Current);
        AppUpdateConsentCompleted();
        if (mode == AppUpdateCheckMode.Automatic)
            AutomaticUpdateCheckRequested();
    }

    internal async Task<AppUpdateCheckResult?> MaybeRunAutomaticAppUpdateCheckAsync()
    {
        UserSettings settings = _userSettings.Current;
        if (settings.AppUpdateCheckMode != AppUpdateCheckMode.Automatic
            || !AppUpdateChecker.ShouldAutoCheck(
                settings.LastAppUpdateCheckUtc,
                DateTimeOffset.UtcNow,
                AppUpdateChecker.DefaultAutoCheckInterval))
        {
            return null;
        }

        AppUpdateCheckResult? check = await RunAppUpdateCheckCoreAsync(CancellationToken.None);
        if (check?.UpdateAvailable != true || check.Release is not { } release)
            return null;
        return string.Equals(
            release.Version.ToString(),
            _userSettings.Current.LastAppUpdateAlertedVersion,
            StringComparison.Ordinal)
            ? null
            : check;
    }

    internal async Task RunManualAppUpdateCheckAsync()
    {
        if (_appUpdateBusy)
            return;
        if (_userSettings.Current.AppUpdateCheckMode is AppUpdateCheckMode.Prompt or AppUpdateCheckMode.Off)
        {
            ShowAppUpdateResult(
                "Update checks are disabled",
                "Choose Automatic or Only when I ask under Settings > Updates, save settings, and try again.");
            return;
        }

        ResetAppUpdateOverlay();
        AppUpdateTitle.Text = "Checking for updates";
        AppUpdateMessage.Text = "Reading the official Disk Activity Monitor release from GitHub...";
        AppUpdateProgress.Visibility = Visibility.Visible;
        AppUpdateProgress.IsIndeterminate = true;
        AppUpdateLaterButton.Content = "Cancel";
        AppUpdateOverlay.Visibility = Visibility.Visible;
        AppUpdateOverlay.Focus();

        _appUpdateBusy = true;
        var operationCts = new CancellationTokenSource();
        _appUpdateCts = operationCts;
        try
        {
            AppUpdateCheckResult? check = await RunAppUpdateCheckCoreAsync(operationCts.Token);
            if (check is null)
            {
                ShowAppUpdateResult(
                    "Update check did not complete",
                    "Disk Activity Monitor could not read the official GitHub release information. Check your connection and try again.");
            }
            else if (!check.UpdateAvailable)
            {
                ShowAppUpdateResult(
                    "Disk Activity Monitor is up to date",
                    $"You are running version {check.CurrentVersion}, the latest official release.");
            }
            else if (check.Release is not { } release)
            {
                ShowAppUpdateResult(
                    "Update installer could not be verified",
                    $"Version {check.LatestVersion} is available, but GitHub did not provide exactly one architecture-matched installer with valid SHA-256 metadata. Nothing was downloaded.");
            }
            else
            {
                ShowAppUpdateRelease(check, release);
            }
        }
        catch (OperationCanceledException)
        {
            AppUpdateOverlay.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _appUpdateBusy = false;
            operationCts.Dispose();
            _appUpdateCts = null;
        }
    }

    internal void ShowAppUpdateRelease(AppUpdateCheckResult check, AppReleaseInfo release)
    {
        ResetAppUpdateOverlay();
        _pendingAppUpdateCheck = check;
        _pendingAppUpdateRelease = release;
        AppUpdateTitle.Text = $"Disk Activity Monitor {release.Version} is available";
        AppUpdateMessage.Text = $"You are running {check.CurrentVersion}. The architecture-matched installer is {FormatMegabytes(release.Installer.Size)} MB and carries a GitHub SHA-256 digest.";
        AppUpdateNotes.Markdown = string.IsNullOrWhiteSpace(release.ReleaseNotes)
            ? "No release notes were provided."
            : release.ReleaseNotes;
        AppUpdateNotesPanel.Visibility = Visibility.Visible;
        AppUpdateOpenReleaseButton.Visibility = Visibility.Visible;
        AppUpdateSkipButton.Visibility = Visibility.Visible;
        AppUpdatePrimaryButton.Content = "Download update";
        AppUpdatePrimaryButton.Visibility = Visibility.Visible;
        AppUpdateOverlay.Visibility = Visibility.Visible;
        AppUpdateOverlay.Focus();
    }

    private async Task<AppUpdateCheckResult?> RunAppUpdateCheckCoreAsync(CancellationToken cancellationToken)
    {
        AppUpdateCheckResult? check;
        try
        {
            check = await AppUpdateCheck(CurrentAppVersion(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            check = null;
        }
        _userSettings.Update(settings => settings.LastAppUpdateCheckUtc = DateTimeOffset.UtcNow);
        return check;
    }

    private void ShowAppUpdateResult(string title, string message)
    {
        ResetAppUpdateOverlay();
        AppUpdateTitle.Text = title;
        AppUpdateMessage.Text = message;
        AppUpdateOpenReleaseButton.Visibility = Visibility.Visible;
        AppUpdateOverlay.Visibility = Visibility.Visible;
        AppUpdateOverlay.Focus();
    }

    private async void AppUpdatePrimary_Click(object sender, RoutedEventArgs e)
    {
        if (_appUpdateBusy || _pendingAppUpdateRelease is not { } release)
            return;

        if (_downloadedUpdateInstaller is not null)
        {
            await LaunchVerifiedUpdateInstallerAsync(release);
            return;
        }

        _appUpdateBusy = true;
        var operationCts = new CancellationTokenSource();
        _appUpdateCts = operationCts;
        AppUpdatePrimaryButton.IsEnabled = false;
        AppUpdateSkipButton.IsEnabled = false;
        AppUpdateProgress.Visibility = Visibility.Visible;
        AppUpdateProgress.IsIndeterminate = false;
        AppUpdateProgress.Value = 0;
        AppUpdateStatus.Visibility = Visibility.Visible;
        AppUpdateStatus.Text = $"Downloading {release.Installer.Name}...";
        try
        {
            string updateRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DiskActivityMonitor",
                "Updates",
                release.Version.ToString());
            var progress = new Progress<AppUpdateDownloadProgress>(value =>
            {
                AppUpdateProgress.Value = AppUpdateProgressPercent(value);
                AppUpdateStatus.Text = $"Downloaded {FormatMegabytes(value.ReceivedBytes)} of {FormatMegabytes(value.TotalBytes)} MB";
            });
            string path = await AppUpdateDownload(
                release.Installer,
                updateRoot,
                _userSettings.Current.MaxInstallerSizeMb,
                progress,
                operationCts.Token);
            if (!await AppUpdateChecker.VerifyDownloadedAssetAsync(path, release.Installer, operationCts.Token))
            {
                try { File.Delete(path); } catch { }
                throw new InvalidDataException("The downloaded installer failed its final size or SHA-256 verification and was deleted.");
            }

            _downloadedUpdateInstaller = path;
            AppUpdateProgress.Value = 100;
            AppUpdateStatus.Text = "Download complete. GitHub size and SHA-256 verification passed.";
            AppUpdatePrimaryButton.Content = "Install and exit";
            AppUpdatePrimaryButton.IsEnabled = true;
            AppUpdateOpenFolderButton.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            AppUpdateStatus.Text = "Download canceled.";
            AppUpdateProgress.Visibility = Visibility.Collapsed;
            AppUpdatePrimaryButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            AppUpdateStatus.Text = ex.Message;
            AppUpdateProgress.Visibility = Visibility.Collapsed;
            AppUpdatePrimaryButton.IsEnabled = true;
        }
        finally
        {
            _appUpdateBusy = false;
            AppUpdateSkipButton.IsEnabled = true;
            operationCts.Dispose();
            _appUpdateCts = null;
        }
    }

    private async Task LaunchVerifiedUpdateInstallerAsync(AppReleaseInfo release)
    {
        string path = _downloadedUpdateInstaller!;
        _appUpdateBusy = true;
        var operationCts = new CancellationTokenSource();
        _appUpdateCts = operationCts;
        AppUpdatePrimaryButton.IsEnabled = false;
        AppUpdateSkipButton.IsEnabled = false;
        AppUpdateOpenFolderButton.IsEnabled = false;
        AppUpdateStatus.Visibility = Visibility.Visible;
        AppUpdateStatus.Text = "Verifying the installer again before launch...";
        try
        {
            await using VerifiedAppUpdateInstaller? verified = await AppUpdateOpenVerifiedInstaller(
                path,
                release.Installer,
                operationCts.Token);
            operationCts.Token.ThrowIfCancellationRequested();
            if (verified is null)
            {
                try { File.Delete(path); } catch { }
                _downloadedUpdateInstaller = null;
                AppUpdateStatus.Text = "The installer no longer matches GitHub's size and SHA-256 metadata. It was deleted and was not launched.";
                AppUpdatePrimaryButton.Content = "Download update";
                AppUpdateOpenFolderButton.Visibility = Visibility.Collapsed;
                return;
            }

            // Keep the verified stream open with FileShare.Read until CreateProcess returns. This
            // denies write/delete replacement between the final hash and executable image open.
            if (AppUpdateInstallerLauncher(verified.Path))
                AppUpdateExit();
            else
                AppUpdateStatus.Text = "The update installer did not start. Disk Activity Monitor remains open.";
        }
        catch (OperationCanceledException)
        {
            // A Later/Skip/Escape/close action is an explicit decision not to launch.
        }
        catch (Exception ex)
        {
            AppUpdateStatus.Text = $"The update installer did not start: {ex.Message}";
        }
        finally
        {
            _appUpdateBusy = false;
            operationCts.Dispose();
            _appUpdateCts = null;
            AppUpdatePrimaryButton.IsEnabled = true;
            AppUpdateSkipButton.IsEnabled = true;
            AppUpdateOpenFolderButton.IsEnabled = true;
        }
    }

    private void AppUpdateSkip_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingAppUpdateRelease is { } release)
        {
            _userSettings.Update(settings => settings.LastAppUpdateAlertedVersion = release.Version.ToString());
            AppUpdateSettingsStatus.Text = $"Skipped version {release.Version}.";
            AppUpdateSettingsStatus.Visibility = Visibility.Visible;
        }
        CloseAppUpdateOverlay();
    }

    private void AppUpdateClose_Click(object sender, RoutedEventArgs e) => CloseAppUpdateOverlay();

    private void CloseAppUpdateOverlay()
    {
        _appUpdateCts?.Cancel();
        AppUpdateOverlay.Visibility = Visibility.Collapsed;
    }

    private void AppUpdateOpenRelease_Click(object sender, RoutedEventArgs e)
    {
        Uri uri = _pendingAppUpdateRelease?.ReleasePage ?? AppUpdateChecker.LatestReleasePage;
        try { AppUpdateShellLauncher(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { }
    }

    private void AppUpdateOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadedUpdateInstaller is null)
            return;
        try
        {
            AppUpdateShellLauncher(new ProcessStartInfo("explorer.exe", $"/select,\"{_downloadedUpdateInstaller}\"")
            {
                UseShellExecute = true,
            });
        }
        catch { }
    }

    private void AppUpdateOverlay_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = HandleAppUpdateOverlayKey(e.Key);
    }

    internal bool HandleAppUpdateOverlayKey(System.Windows.Input.Key key)
    {
        if (key != System.Windows.Input.Key.Escape)
            return false;
        CloseAppUpdateOverlay();
        return true;
    }

    private void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAppUpdateMode() is AppUpdateCheckMode.Prompt or AppUpdateCheckMode.Off)
        {
            AppUpdateSettingsStatus.Text = "Choose Automatic or Only when I ask, then save settings before checking.";
            AppUpdateSettingsStatus.Visibility = Visibility.Visible;
            return;
        }
        _ = RunManualAppUpdateCheckAsync();
    }

    internal void LoadAppUpdateSettings(UserSettings settings)
    {
        AppUpdateCheckMode mode = settings.AppUpdateCheckMode;
        foreach (object item in AppUpdateModeSelector.Items)
        {
            if (item is ComboBoxItem combo
                && Enum.TryParse(combo.Tag?.ToString(), out AppUpdateCheckMode itemMode)
                && itemMode == mode)
            {
                AppUpdateModeSelector.SelectedItem = combo;
                break;
            }
        }
        TxtMaxInstallerSizeMb.Text = settings.MaxInstallerSizeMb.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal AppUpdateCheckMode SelectedAppUpdateMode()
    {
        string? value = (AppUpdateModeSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return Enum.TryParse(value, out AppUpdateCheckMode mode) ? mode : AppUpdateCheckMode.Manual;
    }

    internal static bool TryParseMaximumInstallerSize(string text, out int megabytes)
        => int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out megabytes)
            && megabytes > 0;

    private void ResetAppUpdateOverlay()
    {
        _downloadedUpdateInstaller = null;
        _pendingAppUpdateCheck = null;
        _pendingAppUpdateRelease = null;
        AppUpdateTitle.Text = "Application update";
        AppUpdateMessage.Text = "";
        AppUpdateNotes.Markdown = "";
        AppUpdateNotesPanel.Visibility = Visibility.Collapsed;
        AppUpdateProgress.Visibility = Visibility.Collapsed;
        AppUpdateProgress.IsIndeterminate = false;
        AppUpdateProgress.Value = 0;
        AppUpdateStatus.Text = "";
        AppUpdateStatus.Visibility = Visibility.Collapsed;
        AppUpdateOpenReleaseButton.Visibility = Visibility.Collapsed;
        AppUpdateOpenFolderButton.Visibility = Visibility.Collapsed;
        AppUpdateSkipButton.Visibility = Visibility.Collapsed;
        AppUpdateSkipButton.IsEnabled = true;
        AppUpdateLaterButton.Content = "Later";
        AppUpdatePrimaryButton.Content = "Download update";
        AppUpdatePrimaryButton.Visibility = Visibility.Collapsed;
        AppUpdatePrimaryButton.IsEnabled = true;
    }

    private static string FormatMegabytes(long bytes) => (bytes / 1024d / 1024d).ToString("0.##");

    internal static double AppUpdateProgressPercent(AppUpdateDownloadProgress value)
    {
        double percent = value.TotalBytes <= 0 ? 0 : value.ReceivedBytes * 100d / value.TotalBytes;
        return Math.Clamp(percent, 0, 100);
    }

    internal void CancelAppUpdateOperations() => _appUpdateCts?.Cancel();
}