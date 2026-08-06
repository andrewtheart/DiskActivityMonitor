using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using DiskActivityMonitor.Core.Configuration;
using DiskActivityMonitor.Core.Data;
using DiskActivityMonitor.Core.Models;
using DiskActivityMonitor.Core.Updates;
using DiskActivityMonitor.Tray;

namespace DiskActivityMonitor.Tests;

[Collection("WPF")]
public sealed class AppUpdateUiTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dam_update_ui_{Guid.NewGuid():N}.db");
    private readonly string _cfg = Path.Combine(Path.GetTempPath(), $"dam_update_ui_{Guid.NewGuid():N}.json");
    private readonly string _settings = Path.Combine(Path.GetTempPath(), $"dam_update_ui_{Guid.NewGuid():N}.json");
    private readonly string _installer = Path.Combine(Path.GetTempPath(), $"dam_update_{Guid.NewGuid():N}.exe");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_db) + "*"))
            try { File.Delete(file); } catch { }
        try { File.Delete(_cfg); } catch { }
        try { File.Delete(_settings); } catch { }
        try { File.Delete(_installer); } catch { }
    }

    [Fact]
    public void SettingsAndConsent_PersistTheUserChoiceAndInstallerLimit()
    {
        RunStaAsync(() =>
        {
            EnsureApplication();
            var repo = CreateRepository();
            using var config = new ConfigStore(_cfg);
            var settings = new UserSettingsStore(_settings);
            settings.Save(new UserSettings
            {
                AppUpdateCheckMode = AppUpdateCheckMode.Manual,
                MaxInstallerSizeMb = 384,
            });
            var window = new MainWindow(repo, config, settings);
            try
            {
                Assert.Equal("Manual", ((ComboBoxItem)window.AppUpdateModeSelector.SelectedItem).Tag);
                Assert.Equal("384", window.TxtMaxInstallerSizeMb.Text);
                Assert.True(AppUpdateChecker.TryParseVersion(MainWindow.CurrentAppVersion(), out _));

                window.ShowAppUpdateConsent();
                Assert.Equal(Visibility.Visible, window.AppUpdateConsentOverlay.Visibility);
                int completed = 0;
                int automaticRequested = 0;
                window.AutomaticUpdateCheckRequested = () => automaticRequested++;
                window.AppUpdateConsentCompleted = () => completed++;
                window.AppUpdateConsentManualButton().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(AppUpdateCheckMode.Manual, settings.Current.AppUpdateCheckMode);
                Assert.Equal(1, completed);
                Assert.Equal(Visibility.Collapsed, window.AppUpdateConsentOverlay.Visibility);

                window.ShowAppUpdateConsent();
                window.AppUpdateConsentAutomaticButton().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(AppUpdateCheckMode.Automatic, settings.Current.AppUpdateCheckMode);
                Assert.Equal(2, completed);
                Assert.Equal(1, automaticRequested);

                window.ShowAppUpdateConsent();
                window.InvokeAppUpdateConsentClose();
                Assert.Equal(3, completed);
                Assert.Equal(Visibility.Collapsed, window.AppUpdateConsentOverlay.Visibility);

                window.ShowAppUpdateConsent();
                window.AppUpdateConsentOffButton().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(AppUpdateCheckMode.Off, settings.Current.AppUpdateCheckMode);
                Assert.Equal(4, completed);

                SelectUpdateMode(window, AppUpdateCheckMode.Off);
                window.TxtMaxInstallerSizeMb.Text = "512";
                window.Save_Click(window, new RoutedEventArgs());
                Assert.Equal(AppUpdateCheckMode.Off, settings.Current.AppUpdateCheckMode);
                Assert.Equal(512, settings.Current.MaxInstallerSizeMb);

                window.TxtMaxInstallerSizeMb.Text = "0";
                window.Save_Click(window, new RoutedEventArgs());
                Assert.Contains("greater than 0", window.SaveStatus.Text);
                Assert.Equal(512, settings.Current.MaxInstallerSizeMb);
            }
            finally { window.ForceClose(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void DeferredConsent_RemainsPromptWhenUnrelatedSettingsAreSaved()
    {
        RunStaAsync(() =>
        {
            EnsureApplication();
            var repo = CreateRepository();
            using var config = new ConfigStore(_cfg);
            var settings = new UserSettingsStore(_settings);
            var window = new MainWindow(repo, config, settings);
            int automaticRequests = 0;
            window.AutomaticUpdateCheckRequested = () => automaticRequests++;
            try
            {
                Assert.Equal("Prompt", ((ComboBoxItem)window.AppUpdateModeSelector.SelectedItem).Tag);

                window.Save_Click(window, new RoutedEventArgs());

                Assert.Equal(AppUpdateCheckMode.Prompt, settings.Current.AppUpdateCheckMode);
                Assert.Equal(0, automaticRequests);
            }
            finally { window.ForceClose(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void AutomaticCheck_IsThrottledAndSurfacesOnlyANewUnskippedRelease()
    {
        RunStaAsync(async () =>
        {
            EnsureApplication();
            var repo = CreateRepository();
            using var config = new ConfigStore(_cfg);
            var settings = new UserSettingsStore(_settings);
            settings.Save(new UserSettings { AppUpdateCheckMode = AppUpdateCheckMode.Automatic });
            using var controller = new TrayController(repo, config, settings);
            int checks = 0;
            int presented = 0;
            AppUpdateCheckResult result = UpdateResult();
            controller.AppUpdateCheck = (_, _) =>
            {
                checks++;
                return Task.FromResult<AppUpdateCheckResult?>(result);
            };
            controller.AppUpdateAvailablePresenter = (_, _) => presented++;

            Assert.Same(result, await controller.MaybeRunAutomaticAppUpdateCheckAsync());
            Assert.Equal(1, checks);
            Assert.Equal(1, presented);
            Assert.NotNull(settings.Current.LastAppUpdateCheckUtc);

            Assert.Null(await controller.MaybeRunAutomaticAppUpdateCheckAsync());
            Assert.Equal(1, checks);

            settings.Update(value =>
            {
                value.LastAppUpdateCheckUtc = null;
                value.LastAppUpdateAlertedVersion = result.LatestVersion.ToString();
            });
            Assert.Same(result, await controller.MaybeRunAutomaticAppUpdateCheckAsync());
            Assert.Equal(2, checks);
            Assert.Equal(1, presented);
        });
    }

    [Fact]
    public void ManualCheck_DoesNotContactGitHubBeforeConsent()
    {
        RunStaAsync(async () =>
        {
            EnsureApplication();
            var repo = CreateRepository();
            using var config = new ConfigStore(_cfg);
            var settings = new UserSettingsStore(_settings);
            var window = new MainWindow(repo, config, settings);
            int requests = 0;
            window.AppUpdateCheck = (_, _) =>
            {
                requests++;
                return Task.FromResult<AppUpdateCheckResult?>(null);
            };
            try
            {
                await window.RunManualAppUpdateCheckAsync();

                Assert.Equal(0, requests);
                Assert.Equal("Update checks are disabled", window.AppUpdateTitle.Text);
            }
            finally { window.ForceClose(); }
        });
    }

    [Fact]
    public void CheckForUpdatesButton_ShowsSettingsWarning_WhenPromptOrOffSelected()
    {
        RunStaAsync(() =>
        {
            EnsureApplication();
            var repo = CreateRepository();
            using var config = new ConfigStore(_cfg);
            var settings = new UserSettingsStore(_settings);
            var window = new MainWindow(repo, config, settings);
            try
            {
                SelectUpdateMode(window, AppUpdateCheckMode.Off);
                window.InvokeCheckForUpdatesClick();

                Assert.Contains("Choose Automatic or Only when I ask", window.AppUpdateSettingsStatus.Text);
                Assert.Equal(Visibility.Visible, window.AppUpdateSettingsStatus.Visibility);
            }
            finally { window.ForceClose(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void AutomaticCheck_PathBranches_ManualModeNoUpdateAndAlreadySkipped()
    {
        RunStaAsync(async () =>
        {
            EnsureApplication();
            var repo = CreateRepository();
            using var config = new ConfigStore(_cfg);
            var settings = new UserSettingsStore(_settings);
            var window = new MainWindow(repo, config, settings);
            try
            {
                settings.Save(new UserSettings { AppUpdateCheckMode = AppUpdateCheckMode.Manual });
                window.AppUpdateCheck = (_, _) => Task.FromResult<AppUpdateCheckResult?>(UpdateResult());
                Assert.Null(await window.MaybeRunAutomaticAppUpdateCheckAsync());

                settings.Save(new UserSettings
                {
                    AppUpdateCheckMode = AppUpdateCheckMode.Automatic,
                    LastAppUpdateAlertedVersion = "9.9.9",
                    LastAppUpdateCheckUtc = null,
                });
                Assert.Null(await window.MaybeRunAutomaticAppUpdateCheckAsync());

                settings.Save(new UserSettings
                {
                    AppUpdateCheckMode = AppUpdateCheckMode.Automatic,
                    LastAppUpdateCheckUtc = null,
                });
                window.AppUpdateCheck = (_, _) => Task.FromResult<AppUpdateCheckResult?>(new AppUpdateCheckResult(new Version(1, 0, 0), new Version(1, 0, 0), null));
                Assert.Null(await window.MaybeRunAutomaticAppUpdateCheckAsync());

                settings.Update(value => value.LastAppUpdateCheckUtc = null);
                window.AppUpdateCheck = (_, _) => Task.FromResult<AppUpdateCheckResult?>(null);
                Assert.Null(await window.MaybeRunAutomaticAppUpdateCheckAsync());

                settings.Update(value => value.LastAppUpdateCheckUtc = null);
                window.AppUpdateCheck = (_, _) => Task.FromResult<AppUpdateCheckResult?>(new AppUpdateCheckResult(new Version(1, 0, 0), new Version(1, 0, 1), null));
                Assert.Null(await window.MaybeRunAutomaticAppUpdateCheckAsync());

                settings.Update(value =>
                {
                    value.LastAppUpdateCheckUtc = null;
                    value.LastAppUpdateAlertedVersion = null;
                });
                AppUpdateCheckResult available = UpdateResult();
                window.AppUpdateCheck = (_, _) => Task.FromResult<AppUpdateCheckResult?>(available);
                Assert.Same(available, await window.MaybeRunAutomaticAppUpdateCheckAsync());
            }
            finally { window.ForceClose(); }
        });
    }

    [Fact]
    public void VersionSelection_CoversInformationalAssemblyAndMissingFallbacks()
    {
        Assert.Equal("2.3.4", MainWindow.SelectCurrentAppVersion("2.3.4+build.5", new Version(1, 0, 0)));
        Assert.Equal("1.2.3", MainWindow.SelectCurrentAppVersion("not-a-version", new Version(1, 2, 3, 4)));
        Assert.Equal("0.0.0", MainWindow.SelectCurrentAppVersion(null, null));

        var assemblyName = new AssemblyName("AppUpdateVersionFallback")
        {
            Version = new Version(3, 2, 1, 4),
        };
        Assembly assembly = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
            assemblyName,
            System.Reflection.Emit.AssemblyBuilderAccess.Run);
        Assert.Equal("3.2.1", MainWindow.CurrentAppVersion(assembly));
    }

    [Fact]
    public void DownloadProgressPercent_HandlesUnknownTotalsAndClampsKnownTotals()
    {
        Assert.Equal(0, MainWindow.AppUpdateProgressPercent(new AppUpdateDownloadProgress(25, 0)));
        Assert.Equal(25, MainWindow.AppUpdateProgressPercent(new AppUpdateDownloadProgress(25, 100)));
        Assert.Equal(100, MainWindow.AppUpdateProgressPercent(new AppUpdateDownloadProgress(125, 100)));
    }

    [Fact]
    public void ManualCheck_ContainsFailureAndIgnoresASecondRequestWhileBusy()
    {
        RunStaAsync(async () =>
        {
            EnsureApplication();
            var repo = CreateRepository();
            using var config = new ConfigStore(_cfg);
            var settings = new UserSettingsStore(_settings);
            settings.Save(new UserSettings { AppUpdateCheckMode = AppUpdateCheckMode.Manual });
            var window = new MainWindow(repo, config, settings);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<AppUpdateCheckResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
            int requests = 0;
            window.AppUpdateCheck = (_, _) =>
            {
                requests++;
                started.TrySetResult();
                return release.Task;
            };
            try
            {
                Task first = window.RunManualAppUpdateCheckAsync();
                await started.Task;
                await window.RunManualAppUpdateCheckAsync();
                Assert.Equal(1, requests);

                release.SetResult(UpdateResult());
                await first;
                Assert.Contains("9.9.9 is available", window.AppUpdateTitle.Text);

                window.AppUpdateCheck = (_, _) => throw new InvalidOperationException("offline");
                await window.RunManualAppUpdateCheckAsync();
                Assert.Equal("Update check did not complete", window.AppUpdateTitle.Text);
            }
            finally { window.ForceClose(); }
        });
    }

    [Fact]
    public void ShowReleaseAndSkip_UpdateLastAlertedVersionAndOverlayState()
    {
        RunStaAsync(() =>
        {
            EnsureApplication();
            var repo = CreateRepository();
            using var config = new ConfigStore(_cfg);
            var settings = new UserSettingsStore(_settings);
            settings.Save(new UserSettings { AppUpdateCheckMode = AppUpdateCheckMode.Manual });
            var window = new MainWindow(repo, config, settings);
            try
            {
                AppUpdateCheckResult releaseWithNotes = UpdateResult();
                window.ShowAppUpdateRelease(releaseWithNotes, releaseWithNotes.Release!);
                Assert.Equal(Visibility.Visible, window.AppUpdateNotesPanel.Visibility);

                AppUpdateCheckResult releaseWithoutNotes = releaseWithNotes with
                {
                    Release = releaseWithNotes.Release! with { ReleaseNotes = "   " },
                };
                window.ShowAppUpdateRelease(releaseWithoutNotes, releaseWithoutNotes.Release!);
                Assert.Equal("No release notes were provided.", window.AppUpdateNotes.Markdown);

                window.AppUpdateSkipButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("9.9.9", settings.Current.LastAppUpdateAlertedVersion);
                Assert.Equal(Visibility.Collapsed, window.AppUpdateOverlay.Visibility);
                Assert.Equal(Visibility.Visible, window.AppUpdateSettingsStatus.Visibility);

                window.ShowAppUpdateRelease(releaseWithNotes, releaseWithNotes.Release!);
                window.InvokeAppUpdateClose();
                Assert.Equal(Visibility.Collapsed, window.AppUpdateOverlay.Visibility);
            }
            finally { window.ForceClose(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void EscapeKey_ClosesUpdateOverlay()
    {
        RunStaAsync(() =>
        {
            EnsureApplication();
            var repo = CreateRepository();
            using var config = new ConfigStore(_cfg);
            var settings = new UserSettingsStore(_settings);
            var window = new MainWindow(repo, config, settings);
            try
            {
                AppUpdateCheckResult result = UpdateResult();
                window.ShowAppUpdateRelease(result, result.Release!);
                window.CancelAppUpdateOperations();
                Assert.False(window.HandleAppUpdateOverlayKey(System.Windows.Input.Key.A));
                Assert.Equal(Visibility.Visible, window.AppUpdateOverlay.Visibility);

                Assert.True(window.HandleAppUpdateOverlayKey(System.Windows.Input.Key.Escape));
                Assert.Equal(Visibility.Collapsed, window.AppUpdateOverlay.Visibility);
            }
            finally { window.ForceClose(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void OpenFolderButton_DoesNothingWhenNoDownload()
    {
        RunStaAsync(() =>
        {
            EnsureApplication();
            var repo = CreateRepository();
            using var config = new ConfigStore(_cfg);
            var settings = new UserSettingsStore(_settings);
            var window = new MainWindow(repo, config, settings);
            try
            {
                window.AppUpdateOpenFolderButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(true);
            }
            finally { window.ForceClose(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void PrimaryButton_DoesNothingBeforeAReleaseIsPresented()
    {
        RunStaAsync(async () =>
        {
            EnsureApplication();
            var repo = CreateRepository();
            using var config = new ConfigStore(_cfg);
            var settings = new UserSettingsStore(_settings);
            var window = new MainWindow(repo, config, settings);
            try
            {
                window.AppUpdatePrimaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(50);
                Assert.Equal("Download update", window.AppUpdatePrimaryButton.Content);
            }
            finally { window.ForceClose(); }
        });
    }

    [Fact]
    public void PrimaryButton_IgnoresASecondClickWhileDownloadIsActive()
    {
        RunStaAsync(async () =>
        {
            EnsureApplication();
            var repo = CreateRepository();
            using var config = new ConfigStore(_cfg);
            var settings = new UserSettingsStore(_settings);
            settings.Save(new UserSettings { AppUpdateCheckMode = AppUpdateCheckMode.Manual });
            var window = new MainWindow(repo, config, settings);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int downloads = 0;
            window.AppUpdateDownload = async (_, _, _, _, cancellationToken) =>
            {
                downloads++;
                started.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return _installer;
                }
                finally
                {
                    stopped.SetResult();
                }
            };
            try
            {
                AppUpdateCheckResult result = UpdateResult();
                window.ShowAppUpdateRelease(result, result.Release!);
                window.AppUpdatePrimaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await started.Task;

                window.AppUpdatePrimaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, downloads);

                window.CancelAppUpdateOperations();
                await stopped.Task;
            }
            finally { window.ForceClose(); }
        });
    }

    [Fact]
    public void RunManualCheck_CoversNullNoUpdateMissingReleaseAndCancellation()
    {
        RunStaAsync(async () =>
        {
            EnsureApplication();
            var repo = CreateRepository();
            using var config = new ConfigStore(_cfg);
            var settings = new UserSettingsStore(_settings);
            settings.Save(new UserSettings { AppUpdateCheckMode = AppUpdateCheckMode.Manual });
            var window = new MainWindow(repo, config, settings);
            try
            {
                window.AppUpdateCheck = (_, _) => Task.FromResult<AppUpdateCheckResult?>(null);
                await window.RunManualAppUpdateCheckAsync();
                Assert.Equal("Update check did not complete", window.AppUpdateTitle.Text);

                window.AppUpdateCheck = (_, _) => Task.FromResult<AppUpdateCheckResult?>(new AppUpdateCheckResult(new Version(1, 4, 12), new Version(1, 4, 12), null));
                await window.RunManualAppUpdateCheckAsync();
                Assert.Equal("Disk Activity Monitor is up to date", window.AppUpdateTitle.Text);

                window.AppUpdateCheck = (_, _) => Task.FromResult<AppUpdateCheckResult?>(new AppUpdateCheckResult(new Version(1, 4, 12), new Version(1, 4, 13), null));
                await window.RunManualAppUpdateCheckAsync();
                Assert.Equal("Update installer could not be verified", window.AppUpdateTitle.Text);

                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.AppUpdateCheck = async (_, cancellationToken) =>
                {
                    started.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return null;
                };
                Task runTask = window.RunManualAppUpdateCheckAsync();
                await started.Task;
                window.CancelAppUpdateOperations();
                await runTask;
                Assert.Equal(Visibility.Collapsed, window.AppUpdateOverlay.Visibility);
            }
            finally { window.ForceClose(); }
        });
    }

    [Fact]
    public void InstallFlow_CoversVerifyFailureLauncherFalseAndLauncherException()
    {
        RunStaAsync(async () =>
        {
            EnsureApplication();
            var repo = CreateRepository();
            using var config = new ConfigStore(_cfg);
            var settings = new UserSettingsStore(_settings);
            settings.Save(new UserSettings { AppUpdateCheckMode = AppUpdateCheckMode.Manual });
            var window = new MainWindow(repo, config, settings);
            byte[] payload = Encoding.UTF8.GetBytes("verified installer");
            await File.WriteAllBytesAsync(_installer, payload);
            AppUpdateCheckResult result = UpdateResult(payload);
            window.AppUpdateDownload = (_, _, _, progress, _) =>
            {
                progress?.Report(new AppUpdateDownloadProgress(payload.Length, payload.Length));
                return Task.FromResult(_installer);
            };
            try
            {
                window.ShowAppUpdateRelease(result, result.Release!);
                window.AppUpdatePrimaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);

                window.AppUpdateOpenVerifiedInstaller = (_, _, _) => Task.FromResult<VerifiedAppUpdateInstaller?>(null);
                window.AppUpdatePrimaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);
                Assert.Contains("no longer matches GitHub", window.AppUpdateStatus.Text);
                Assert.Equal("Download update", window.AppUpdatePrimaryButton.Content);

                await File.WriteAllBytesAsync(_installer, payload);
                window.AppUpdateOpenVerifiedInstaller = AppUpdateChecker.OpenVerifiedAssetAsync;
                window.AppUpdatePrimaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);
                window.AppUpdateInstallerLauncher = _ => false;
                window.AppUpdatePrimaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);
                Assert.Contains("did not start. Disk Activity Monitor remains open", window.AppUpdateStatus.Text);

                window.AppUpdatePrimaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);
                window.AppUpdateInstallerLauncher = _ => throw new InvalidOperationException("boom");
                window.AppUpdatePrimaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);
                Assert.Contains("did not start: boom", window.AppUpdateStatus.Text);
            }
            finally { window.ForceClose(); }
        });
    }

    [Fact]
    public void DownloadFlow_CoversFailedVerificationCancellationAndOrdinaryFailure()
    {
        RunStaAsync(async () =>
        {
            EnsureApplication();
            var repo = CreateRepository();
            using var config = new ConfigStore(_cfg);
            var settings = new UserSettingsStore(_settings);
            settings.Save(new UserSettings { AppUpdateCheckMode = AppUpdateCheckMode.Manual });
            var window = new MainWindow(repo, config, settings);
            byte[] actual = Encoding.UTF8.GetBytes("different installer");
            try
            {
                await File.WriteAllBytesAsync(_installer, actual);
                AppUpdateCheckResult expectedOtherPayload = UpdateResult(Encoding.UTF8.GetBytes("expected installer"));
                window.AppUpdateDownload = (_, _, _, _, _) => Task.FromResult(_installer);
                window.ShowAppUpdateRelease(expectedOtherPayload, expectedOtherPayload.Release!);
                window.AppUpdatePrimaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);
                Assert.False(File.Exists(_installer));
                Assert.Contains("failed its final size or SHA-256 verification", window.AppUpdateStatus.Text);

                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.AppUpdateDownload = async (_, _, _, _, cancellationToken) =>
                {
                    started.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return _installer;
                };
                window.ShowAppUpdateRelease(UpdateResult(), UpdateResult().Release!);
                window.AppUpdatePrimaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await started.Task;
                window.CancelAppUpdateOperations();
                await Task.Delay(100);
                Assert.Equal("Download canceled.", window.AppUpdateStatus.Text);

                window.AppUpdateDownload = (_, _, _, _, _) => throw new InvalidOperationException("download failed");
                window.ShowAppUpdateRelease(UpdateResult(), UpdateResult().Release!);
                window.AppUpdatePrimaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);
                Assert.Equal("download failed", window.AppUpdateStatus.Text);
            }
            finally { window.ForceClose(); }
        });
    }

    [Fact]
    public void UpdateShellActions_AreCapturedAndShellFailuresAreContained()
    {
        RunStaAsync(async () =>
        {
            EnsureApplication();
            var repo = CreateRepository();
            using var config = new ConfigStore(_cfg);
            var settings = new UserSettingsStore(_settings);
            settings.Save(new UserSettings { AppUpdateCheckMode = AppUpdateCheckMode.Manual });
            var window = new MainWindow(repo, config, settings);
            byte[] payload = Encoding.UTF8.GetBytes("verified installer");
            await File.WriteAllBytesAsync(_installer, payload);
            AppUpdateCheckResult result = UpdateResult(payload);
            var launches = new List<ProcessStartInfo>();
            window.AppUpdateShellLauncher = startInfo =>
            {
                launches.Add(startInfo);
                return null;
            };
            window.AppUpdateDownload = (_, _, _, _, _) => Task.FromResult(_installer);
            try
            {
                window.ShowAppUpdateRelease(result, result.Release!);
                window.AppUpdateOpenReleaseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.AppUpdatePrimaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);
                window.AppUpdateOpenFolderButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Contains(launches, item => item.FileName == result.Release!.ReleasePage.AbsoluteUri);
                Assert.Contains(launches, item => item.FileName == "explorer.exe" && item.Arguments.Contains(_installer));

                window.AppUpdateShellLauncher = _ => throw new InvalidOperationException("shell unavailable");
                window.AppUpdateOpenReleaseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.AppUpdateOpenFolderButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                window.InvokeAppUpdatePreviewKeyDown(System.Windows.Input.Key.Escape);
                Assert.Equal(Visibility.Collapsed, window.AppUpdateOverlay.Visibility);
            }
            finally { window.ForceClose(); }
        });
    }

    [Fact]
    public void ReleaseAndModeFallbacks_HandleMissingOrMalformedUiState()
    {
        RunStaAsync(() =>
        {
            EnsureApplication();
            var repo = CreateRepository();
            using var config = new ConfigStore(_cfg);
            var settings = new UserSettingsStore(_settings);
            settings.Save(new UserSettings { AppUpdateCheckMode = AppUpdateCheckMode.Manual });
            var window = new MainWindow(repo, config, settings);
            var launches = new List<ProcessStartInfo>();
            window.AppUpdateShellLauncher = startInfo =>
            {
                launches.Add(startInfo);
                return null;
            };
            try
            {
                window.AppUpdateOpenReleaseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(AppUpdateChecker.LatestReleasePage.AbsoluteUri, Assert.Single(launches).FileName);

                window.AppUpdateModeSelector.Items.Insert(0, "not a combo-box item");
                window.AppUpdateModeSelector.Items.Insert(1, new ComboBoxItem { Tag = null });
                window.LoadAppUpdateSettings(new UserSettings { AppUpdateCheckMode = AppUpdateCheckMode.Manual });
                Assert.Equal("Manual", ((ComboBoxItem)window.AppUpdateModeSelector.SelectedItem).Tag);

                window.AppUpdateModeSelector.SelectedItem = null;
                window.LoadAppUpdateSettings(new UserSettings { AppUpdateCheckMode = (AppUpdateCheckMode)999 });
                Assert.Null(window.AppUpdateModeSelector.SelectedItem);

                window.AppUpdateModeSelector.SelectedItem = "not a combo-box item";
                Assert.Equal(AppUpdateCheckMode.Manual, window.SelectedAppUpdateMode());

                window.AppUpdateModeSelector.SelectedItem = window.AppUpdateModeSelector.Items[1];
                Assert.Equal(AppUpdateCheckMode.Manual, window.SelectedAppUpdateMode());
            }
            finally { window.ForceClose(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void CheckForUpdatesButton_StartsManualCheckForAnEnabledMode()
    {
        RunStaAsync(async () =>
        {
            EnsureApplication();
            var repo = CreateRepository();
            using var config = new ConfigStore(_cfg);
            var settings = new UserSettingsStore(_settings);
            settings.Save(new UserSettings { AppUpdateCheckMode = AppUpdateCheckMode.Manual });
            var window = new MainWindow(repo, config, settings);
            var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.AppUpdateCheck = (_, _) =>
            {
                requested.SetResult();
                return Task.FromResult<AppUpdateCheckResult?>(null);
            };
            try
            {
                SelectUpdateMode(window, AppUpdateCheckMode.Manual);
                window.InvokeCheckForUpdatesClick();
                await requested.Task;
                Assert.Equal(Visibility.Visible, window.AppUpdateOverlay.Visibility);
            }
            finally { window.ForceClose(); }
        });
    }

    [Fact]
    public void ReleaseOverlay_DownloadsVerifiesAndLaunchesOnlyAfterSecondConfirmation()
    {
        RunStaAsync(async () =>
        {
            EnsureApplication();
            var repo = CreateRepository();
            using var config = new ConfigStore(_cfg);
            var settings = new UserSettingsStore(_settings);
            settings.Save(new UserSettings { AppUpdateCheckMode = AppUpdateCheckMode.Manual });
            var window = new MainWindow(repo, config, settings);
            byte[] payload = Encoding.UTF8.GetBytes("verified installer");
            await File.WriteAllBytesAsync(_installer, payload);
            AppUpdateCheckResult result = UpdateResult(payload);
            bool launched = false;
            bool lockedDuringLaunch = false;
            bool exited = false;
            window.AppUpdateDownload = (_, _, _, progress, _) =>
            {
                progress?.Report(new AppUpdateDownloadProgress(payload.Length, payload.Length));
                return Task.FromResult(_installer);
            };
            window.AppUpdateInstallerLauncher = path =>
            {
                try { File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose(); }
                catch (IOException) { lockedDuringLaunch = true; }
                return launched = path == _installer;
            };
            window.AppUpdateExit = () => exited = true;
            try
            {
                window.ShowAppUpdateRelease(result, result.Release!);
                Assert.Equal(Visibility.Visible, window.AppUpdateOverlay.Visibility);
                Assert.Equal(Visibility.Visible, window.AppUpdateNotesPanel.Visibility);
                Assert.Contains("release notes", window.AppUpdateNotes.Markdown);

                window.AppUpdatePrimaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);
                Assert.Equal("Install and exit", window.AppUpdatePrimaryButton.Content);
                Assert.Contains("SHA-256 verification passed", window.AppUpdateStatus.Text);
                Assert.False(launched);

                window.AppUpdatePrimaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);
                Assert.True(launched);
                Assert.True(lockedDuringLaunch);
                Assert.True(exited);
            }
            finally { window.ForceClose(); }
        });
    }

    [Fact]
    public void ClosingDuringFinalVerification_CancelsAndNeverLaunches()
    {
        RunStaAsync(async () =>
        {
            EnsureApplication();
            var repo = CreateRepository();
            using var config = new ConfigStore(_cfg);
            var settings = new UserSettingsStore(_settings);
            settings.Save(new UserSettings { AppUpdateCheckMode = AppUpdateCheckMode.Manual });
            var window = new MainWindow(repo, config, settings);
            byte[] payload = Encoding.UTF8.GetBytes("verified installer");
            await File.WriteAllBytesAsync(_installer, payload);
            AppUpdateCheckResult result = UpdateResult(payload);
            var verificationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool cancellationObserved = false;
            bool launched = false;
            window.AppUpdateDownload = (_, _, _, _, _) => Task.FromResult(_installer);
            window.AppUpdateOpenVerifiedInstaller = async (_, _, cancellationToken) =>
            {
                verificationStarted.SetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException)
                {
                    cancellationObserved = true;
                    throw;
                }
                return null;
            };
            window.AppUpdateInstallerLauncher = _ => launched = true;
            try
            {
                window.ShowAppUpdateRelease(result, result.Release!);
                window.AppUpdatePrimaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);
                window.AppUpdatePrimaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await verificationStarted.Task;

                window.AppUpdateLaterButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);

                Assert.True(cancellationObserved);
                Assert.False(launched);
                Assert.Equal(Visibility.Collapsed, window.AppUpdateOverlay.Visibility);
            }
            finally { window.ForceClose(); }
        });
    }

    [Fact]
    public void WindowCloseDuringFinalVerification_CancelsAndNeverLaunches()
    {
        RunStaAsync(async () =>
        {
            EnsureApplication();
            var repo = CreateRepository();
            using var config = new ConfigStore(_cfg);
            var settings = new UserSettingsStore(_settings);
            settings.Save(new UserSettings { AppUpdateCheckMode = AppUpdateCheckMode.Manual });
            var window = new MainWindow(repo, config, settings);
            byte[] payload = Encoding.UTF8.GetBytes("verified installer");
            await File.WriteAllBytesAsync(_installer, payload);
            AppUpdateCheckResult result = UpdateResult(payload);
            var verificationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool cancellationObserved = false;
            bool launched = false;
            window.AppUpdateDownload = (_, _, _, _, _) => Task.FromResult(_installer);
            window.AppUpdateOpenVerifiedInstaller = async (_, _, cancellationToken) =>
            {
                verificationStarted.SetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException)
                {
                    cancellationObserved = true;
                    throw;
                }
                return null;
            };
            window.AppUpdateInstallerLauncher = _ => launched = true;
            try
            {
                window.Show();
                window.ShowAppUpdateRelease(result, result.Release!);
                window.AppUpdatePrimaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);
                window.AppUpdatePrimaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await verificationStarted.Task;

                window.Close();
                await Task.Delay(100);

                Assert.True(cancellationObserved);
                Assert.False(launched);
                Assert.False(window.IsVisible);
            }
            finally { window.ForceClose(); }
        });
    }

    [Fact]
    public void PromptDetectionAndMaximumSizeValidation_AreExplicit()
    {
        Assert.True(MainWindow.ShouldPromptAppUpdateConsent(new UserSettings()));
        Assert.False(MainWindow.ShouldPromptAppUpdateConsent(new UserSettings
        {
            AppUpdateCheckMode = AppUpdateCheckMode.Manual,
        }));
        Assert.True(MainWindow.TryParseMaximumInstallerSize("256", out int size));
        Assert.Equal(256, size);
        Assert.False(MainWindow.TryParseMaximumInstallerSize("0", out _));
        Assert.False(MainWindow.TryParseMaximumInstallerSize("1.5", out _));
    }

    private MonitorRepository CreateRepository()
    {
        var repo = new MonitorRepository(_db);
        repo.EnsureSchema();
        repo.UpsertDisks(
        [
            new DiskInfo
            {
                DiskId = "2",
                InstanceName = "2 F:",
                FriendlyName = "Test HDD",
                Volumes = "F:",
                MediaType = DiskMediaType.Hdd,
            },
        ]);
        return repo;
    }

    private static AppUpdateCheckResult UpdateResult(byte[]? payload = null)
    {
        payload ??= [1, 2, 3];
        string version = "9.9.9";
        string name = $"DiskActivityMonitor-Setup-{version}-x64.exe";
        var asset = new AppReleaseAsset(
            name,
            new Uri($"https://github.com/andrewtheart/DiskActivityMonitor/releases/download/v{version}/{name}"),
            payload.Length,
            Convert.ToHexString(SHA256.HashData(payload)));
        var release = new AppReleaseInfo(
            new Version(version),
            "v" + version,
            "Disk Activity Monitor " + version,
            "release notes",
            new Uri($"https://github.com/andrewtheart/DiskActivityMonitor/releases/tag/v{version}"),
            DateTimeOffset.UtcNow,
            asset);
        return new AppUpdateCheckResult(new Version(1, 4, 12), release.Version, release);
    }

    private static void SelectUpdateMode(MainWindow window, AppUpdateCheckMode mode)
    {
        window.AppUpdateModeSelector.SelectedItem = window.AppUpdateModeSelector.Items
            .Cast<ComboBoxItem>()
            .Single(item => string.Equals(item.Tag?.ToString(), mode.ToString(), StringComparison.Ordinal));
    }

    private static void EnsureApplication()
    {
        if (Application.Current is null)
        {
            var app = new DiskActivityMonitor.Tray.App();
            app.InitializeComponent();
        }
        var current = Application.Current!;
        current.Resources["TextPrimary"] = new SolidColorBrush(Colors.White);
        current.Resources["Caption"] = new Style(typeof(TextBlock));
        current.Resources["ToolButton"] = new Style(typeof(Button));
    }

    private static void RunStaAsync(Func<Task> action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
                Task task = action();
                var frame = new System.Windows.Threading.DispatcherFrame();
                task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                task.GetAwaiter().GetResult();
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw new TargetInvocationException(error);
    }
}

internal static class AppUpdateUiTestExtensions
{
    public static Button AppUpdateConsentManualButton(this MainWindow window)
        => FindButton(window.AppUpdateConsentOverlay, "Only when I ask");

    public static Button AppUpdateConsentAutomaticButton(this MainWindow window)
        => FindButton(window.AppUpdateConsentOverlay, "Automatic");

    public static Button AppUpdateConsentOffButton(this MainWindow window)
        => FindButton(window.AppUpdateConsentOverlay, "Off");

    public static void InvokeAppUpdateConsentClose(this MainWindow window)
        => InvokeClickHandler(window, "AppUpdateConsentClose_Click");

    public static void InvokeAppUpdateClose(this MainWindow window)
        => InvokeClickHandler(window, "AppUpdateClose_Click");

    public static void InvokeCheckForUpdatesClick(this MainWindow window)
        => InvokeClickHandler(window, "CheckForUpdates_Click");

    public static void InvokeAppUpdatePreviewKeyDown(this MainWindow window, System.Windows.Input.Key key)
    {
        using var source = new HwndSource(new HwndSourceParameters("AppUpdateUiTests")
        {
            Width = 1,
            Height = 1,
            WindowStyle = 0,
        });
        var args = new System.Windows.Input.KeyEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice,
                source,
                Environment.TickCount,
                key)
            {
                RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent,
            };
        typeof(MainWindow)
            .GetMethod("AppUpdateOverlay_PreviewKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [window, args]);
        Assert.True(args.Handled);
    }

    private static void InvokeClickHandler(MainWindow window, string methodName)
    {
        typeof(MainWindow)
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [window, new RoutedEventArgs(Button.ClickEvent)]);
    }

    private static Button FindButton(DependencyObject root, string content)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is Button button && string.Equals(button.Content?.ToString(), content, StringComparison.Ordinal))
                return button;
            try { return FindButton(child, content); }
            catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException($"Button '{content}' was not found.");
    }
}