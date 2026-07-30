using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskActivityMonitor.Core.Ai;
using DiskActivityMonitor.Core.Collection;
using DiskActivityMonitor.Core.Configuration;
using DiskActivityMonitor.Core.Data;
using DiskActivityMonitor.Core.Models;
using DiskActivityMonitor.Tray;

namespace DiskActivityMonitor.Tests;

[CollectionDefinition("WPF", DisableParallelization = true)]
public sealed class WpfCollectionDefinition;

[Collection("WPF")]
public sealed class MainWindowCoverageTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dam_wpf_{Guid.NewGuid():N}.db");
    private readonly string _cfg = Path.Combine(Path.GetTempPath(), $"dam_wpf_{Guid.NewGuid():N}.json");
    private readonly string _secrets = AiSecretsStore.FilePath;
    private readonly string? _secretBackup;

    public MainWindowCoverageTests() => _secretBackup = File.Exists(_secrets) ? File.ReadAllText(_secrets) : null;

    public void Dispose()
    {
        if (_secretBackup is null) { try { File.Delete(_secrets); } catch { } }
        else { Directory.CreateDirectory(Path.GetDirectoryName(_secrets)!); File.WriteAllText(_secrets, _secretBackup); }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_db) + "*")) try { File.Delete(file); } catch { }
        try { File.Delete(_cfg); } catch { }
    }

    [Fact]
    public void DashboardChangedPaths_RenderAndPersistRealWpfState()
    {
        RunStaAsync(async () =>
        {
            EnsureApplication();
            var repo = new MonitorRepository(_db); repo.EnsureSchema();
            repo.UpsertDisks([new DiskInfo
            {
                DiskId = "2", InstanceName = "2 F:", FriendlyName = "Test HDD", Volumes = "F:", MediaType = DiskMediaType.Hdd,
            }, new DiskInfo
            {
                DiskId = "8", InstanceName = "8 G:", FriendlyName = "", Volumes = "G:", MediaType = DiskMediaType.Ssd,
            }]);
            using var config = new ConfigStore(_cfg);
            var now = DateTime.UtcNow;
            long ordinaryId = repo.InsertAlert(Alert(now.AddMinutes(-3), AlertSeverity.Info, "ordinary", 0));
            repo.InsertAlert(Alert(now.AddMinutes(-2), AlertSeverity.Warning, "disk-controller:2", 4));
            repo.InsertAlert(Alert(now.AddMinutes(-1), AlertSeverity.Critical, "disk-controller:2", 5));

            var window = new MainWindow(repo, config);
            window.Resources["TextPrimary"] = new SolidColorBrush(Colors.White);
            window.Resources["Caption"] = new Style(typeof(TextBlock));
            window.Resources["ToolButton"] = new Style(typeof(Button));
            object hddChoice = ((IEnumerable)window.DiskSelector.ItemsSource).Cast<object>()
                .First(choice => ((DiskInfo)choice.GetType().GetProperty("Disk")!.GetValue(choice)!).DiskId == "2");
            window.DiskSelector.SelectedItem = hddChoice;
            try
            {
                using (var defaultPresenterController = new TrayController(repo, config))
                {
                    typeof(TrayController).GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .SetValue(defaultPresenterController, window);
                    defaultPresenterController.TbwSetupPresenter();
                    Assert.Equal(Visibility.Visible, window.TbwSetupOverlay.Visibility);
                    window.TbwSetupOverlay.Visibility = Visibility.Collapsed;
                    typeof(TrayController).GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .SetValue(defaultPresenterController, null);
                }

                Invoke(window, "TbwLookupClose_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Collapsed, window.TbwLookupPanel.Visibility);

                typeof(MainWindow).GetField("_helpInitialized", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(window, true);
                Invoke(window, "Help_Click", window.HelpButton, new RoutedEventArgs());
                Assert.Equal(Visibility.Visible, window.HelpOverlay.Visibility);
                Assert.Equal("Help and troubleshooting", window.HelpButton.ToolTip);
                Invoke(window, "HelpClose_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Collapsed, window.HelpOverlay.Visibility);

                string fallbackMarkdown = Path.Combine(Path.GetTempPath(), $"dam_help_{Guid.NewGuid():N}.md");
                File.WriteAllText(fallbackMarkdown, "# Help\n\nUse **Refresh** and [open docs](https://example.test).\n\n```powershell\ndam status\n```");
                try
                {
                    window.ShowHelpFallback("fallback test", fallbackMarkdown);
                    Assert.Equal(Visibility.Visible, window.HelpFallbackPanel.Visibility);
                    Assert.Contains("Use Refresh and open docs", window.HelpFallbackText.Text);
                    Assert.Contains("dam status", window.HelpFallbackText.Text);
                }
                finally { File.Delete(fallbackMarkdown); }

                window.UpdateAlerts();
                var rows = ((IEnumerable)window.AlertList.ItemsSource).Cast<object>().ToList();
                Assert.Equal(2, rows.Count);
                foreach (object row in rows)
                    foreach (PropertyInfo property in row.GetType().GetProperties())
                        _ = property.GetValue(row);

                object ordinaryRow = rows.Single(row => !(bool)row.GetType().GetProperty("CanRunSmartScan")!.GetValue(row)!);
                Invoke(window, "DismissAlert_Click", new Button { CommandParameter = ordinaryRow }, new RoutedEventArgs());
                Assert.Single(((IEnumerable)window.AlertList.ItemsSource).Cast<object>());
                Assert.True(repo.GetRecentAlerts(10).Single(a => a.Id == ordinaryId).Acknowledged);

                Invoke(window, "ShowAllAlerts_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Visible, window.AlertHistoryOverlay.Visibility);
                var historyRows = ((IEnumerable)window.AlertHistoryList.ItemsSource).Cast<object>().ToList();
                Assert.Equal(3, historyRows.Count);
                foreach (object row in historyRows)
                    foreach (PropertyInfo property in row.GetType().GetProperties())
                        _ = property.GetValue(row);
                object dismissedRow = historyRows.Single(row => (long)row.GetType().GetProperty("Id")!.GetValue(row)! == ordinaryId);
                Assert.Equal("Dismissed", dismissedRow.GetType().GetProperty("StatusText")!.GetValue(dismissedRow));

                Invoke(window, "RestoreHistoryAlert_Click", new Button { CommandParameter = dismissedRow }, new RoutedEventArgs());
                Assert.Equal(2, ((IEnumerable)window.AlertList.ItemsSource).Cast<object>().Count());
                Assert.False(repo.GetRecentAlerts(10).Single(a => a.Id == ordinaryId).Acknowledged);

                Invoke(window, "DismissAlert_Click", new Button { DataContext = ordinaryRow }, new RoutedEventArgs());
                Assert.True(repo.GetRecentAlerts(10).Single(a => a.Id == ordinaryId).Acknowledged);
                dismissedRow = ((IEnumerable)window.AlertHistoryList.ItemsSource).Cast<object>()
                    .Single(row => (long)row.GetType().GetProperty("Id")!.GetValue(row)! == ordinaryId);
                Invoke(window, "RestoreHistoryAlert_Click", new Button { CommandParameter = dismissedRow }, new RoutedEventArgs());

                Invoke(window, "DismissAlert_Click", new MenuItem { CommandParameter = ordinaryRow }, new RoutedEventArgs());
                repo.RestoreAlerts([ordinaryId]); window.UpdateAlerts(); window.UpdateAlertHistory();
                Invoke(window, "DismissAlert_Click", new MenuItem { DataContext = ordinaryRow }, new RoutedEventArgs());
                repo.RestoreAlerts([ordinaryId]); window.UpdateAlerts(); window.UpdateAlertHistory();
                Invoke(window, "DismissAlert_Click", new Button { CommandParameter = CreateAlertRow("", false, []) }, new RoutedEventArgs());

                object visibleHistoryRow = ((IEnumerable)window.AlertHistoryList.ItemsSource).Cast<object>()
                    .Single(row => (long)row.GetType().GetProperty("Id")!.GetValue(row)! == ordinaryId);
                Invoke(window, "DismissHistoryAlert_Click", new Button { CommandParameter = visibleHistoryRow }, new RoutedEventArgs());
                Assert.Single(((IEnumerable)window.AlertList.ItemsSource).Cast<object>());
                dismissedRow = ((IEnumerable)window.AlertHistoryList.ItemsSource).Cast<object>()
                    .Single(row => (long)row.GetType().GetProperty("Id")!.GetValue(row)! == ordinaryId);
                Invoke(window, "RestoreHistoryAlert_Click", new Button { CommandParameter = dismissedRow }, new RoutedEventArgs());
                Invoke(window, "DismissHistoryAlert_Click", new object(), new RoutedEventArgs());
                Invoke(window, "RestoreHistoryAlert_Click", new object(), new RoutedEventArgs());
                Invoke(window, "DismissAlert_Click", new object(), new RoutedEventArgs());
                Invoke(window, "AlertHistoryClose_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Collapsed, window.AlertHistoryOverlay.Visibility);

                Assert.Equal("—", MainWindow.BlankAsDash(" "));
                Assert.Equal("x", MainWindow.BlankAsDash("x"));
                Assert.Equal("Not exposed", MainWindow.FormatLifetime(null, null));
                Assert.Contains("written", MainWindow.FormatLifetime(1, null));
                Assert.Contains("read", MainWindow.FormatLifetime(null, 1));
                Assert.Contains("·", MainWindow.FormatLifetime(1, 2));

                foreach (SmartScanGrade grade in Enum.GetValues<SmartScanGrade>())
                {
                    window.RenderSmartScanResult(Result(grade,
                        controllerErrors: grade == SmartScanGrade.Attention ? 2 : 0,
                        temperature: grade == SmartScanGrade.Critical ? 60 : grade == SmartScanGrade.Healthy ? 50 : null,
                        telemetry: grade != SmartScanGrade.Limited));
                    Assert.Equal(Visibility.Visible, window.SmartScanResultPanel.Visibility);
                }
                window.RenderSmartScanResult(Result(SmartScanGrade.Healthy, 0, 49, true, maximum: 70));
                window.RenderSmartScanFailure(Disk(), "boom");
                Assert.Contains("boom", window.SmartScanStatusText.Text);
                window.SmartScanOverlay.Visibility = Visibility.Collapsed;
                Assert.Equal(Visibility.Collapsed, window.SmartScanOverlay.Visibility);

                Invoke(window, "RunSmartScan_Click", new object(), new RoutedEventArgs());
                Invoke(window, "RunSmartScan_Click", new MenuItem { CommandParameter = CreateAlertRow(" ", true) }, new RoutedEventArgs());
                Invoke(window, "SmartScanAgain_Click", window, new RoutedEventArgs());
                window.SmartScanner = (disk, errors, progress) =>
                {
                    progress?.Report("fake progress");
                    return Result(SmartScanGrade.Attention, errors, 40, true);
                };
                await window.StartSmartScanAsync(Disk(), 3);
                Assert.Equal("fake progress", window.SmartScanProgressText.Text);
                var controllerRow = rows.First(row => (bool)row.GetType().GetProperty("CanRunSmartScan")!.GetValue(row)!);
                Invoke(window, "RunSmartScan_Click", new MenuItem { CommandParameter = controllerRow }, new RoutedEventArgs());
                await Task.Delay(50);
                Invoke(window, "RunSmartScan_Click", new MenuItem { DataContext = controllerRow }, new RoutedEventArgs());
                await Task.Delay(50);
                Invoke(window, "RunSmartScan_Click", new MenuItem { CommandParameter = CreateAlertRow("404", true) }, new RoutedEventArgs());
                await Task.Delay(50);
                Invoke(window, "SmartScanAgain_Click", window, new RoutedEventArgs());
                await Task.Delay(50);
                window.SmartScanner = (_, _, _) => throw new InvalidOperationException("scan failed");
                await window.StartSmartScanAsync(Disk(), 1);
                Assert.Contains("scan failed", window.SmartScanStatusText.Text);

                await ExerciseStaleScans(window);

                window.LoadSettingsFields();
                window.TxtWarnHour.Text = "invalid";
                window.TxtControllerWindow.Text = "0";
                window.TxtControllerWarn.Text = "8";
                window.TxtControllerCritical.Text = "2";
                window.TxtInterval.Text = "999";
                window.TxtRefresh.Text = "9999";
                window.TxtTbw.Text = "900";
                window.TxtTbwUpper.Text = "1200";
                window.ChkControllerErrors.IsChecked = false;
                window.Save_Click(window, new RoutedEventArgs());
                Assert.Equal(1, config.Current.ControllerErrorWindowDays);
                Assert.Equal(8, config.Current.ControllerErrorCriticalCount);
                Assert.Equal(60, config.Current.SampleIntervalSeconds);
                Assert.False(config.Current.EnableControllerErrorAlerts);
                Assert.Equal(900, config.Current.DiskTbwRatings["2"]);
                Assert.Equal(1200, config.Current.DiskTbwRatingsUpper["2"]);

                window.TxtTbw.Text = "bad";
                window.TxtTbwUpper.Text = "1";
                window.TxtControllerWarn.Text = "0";
                window.Save_Click(window, new RoutedEventArgs());
                Assert.False(config.Current.DiskTbwRatings.ContainsKey("2"));
                Assert.False(config.Current.DiskTbwRatingsUpper.ContainsKey("2"));

                await window.StartTbwLookupAsync(null);
                await window.StartTbwLookupAsync(Disk(), userInitiated: false);
                await window.StartTbwLookupAsync(Disk(), userInitiated: true);
                Assert.Contains("available for SSDs", window.TbwLookupStatus.Text);
                Assert.Equal(Visibility.Visible, window.TbwLookupPanel.Visibility);
                Assert.Equal("TBW lookup is not available for this drive", window.TbwLookupHeadline.Text);
                Assert.Equal(Visibility.Collapsed, window.TbwLookupProgressBar.Visibility);

                var blankSsd = new DiskInfo { DiskId = "8", InstanceName = "8", FriendlyName = "", MediaType = DiskMediaType.Ssd };
                var virtualSsd = new DiskInfo { DiskId = "9", InstanceName = "9", FriendlyName = "Virtual SSD", MediaType = DiskMediaType.Ssd };
                await window.StartTbwLookupAsync(blankSsd, userInitiated: false);
                await window.StartTbwLookupAsync(blankSsd, userInitiated: true);
                await window.StartTbwLookupAsync(virtualSsd, userInitiated: true);
                Assert.Contains("usable model name", window.TbwLookupStatus.Text);

                var knownSsd = new DiskInfo { DiskId = "7", InstanceName = "7", FriendlyName = "Samsung SSD 870 EVO 1TB", MediaType = DiskMediaType.Ssd };
                Assert.True(window.HandleTbwReadiness(new TbwReadiness(true, null, false, null, true)));
                Assert.False(window.HandleTbwReadiness(new TbwReadiness(false, null, true, "model-gpu", true)));
                Assert.Contains("GPU was detected", window.TbwLookupStatus.Text);
                Assert.False(window.HandleTbwReadiness(new TbwReadiness(false, null, true, "model-cpu", false)));
                Assert.Contains("CPU-only", window.TbwLookupStatus.Text);
                Assert.False(window.HandleTbwReadiness(new TbwReadiness(false, "configured reason", false, null, false)));
                Assert.Equal("configured reason", window.TbwLookupStatus.Text);
                Assert.False(window.HandleTbwReadiness(new TbwReadiness(false, null, false, null, false)));
                Assert.Equal("Web TBW lookup is unavailable.", window.TbwLookupStatus.Text);

                config.Current.EnableTbwWebLookup = false;
                await window.StartTbwLookupAsync(knownSsd);
                config.Current.EnableTbwWebLookup = true;
                config.Current.DiskTbwRatings[knownSsd.DiskId] = 600;
                await window.StartTbwLookupAsync(knownSsd);
                config.Current.DiskTbwRatings.Remove(knownSsd.DiskId);
                TbwLookupCache.Put(new TbwLookupResult(knownSsd.FriendlyName,
                    [new TbwCandidate(600, 1, 1, ["example.com"], "https://example.com")], DateTime.UtcNow));
                await window.StartTbwLookupAsync(knownSsd);
                Assert.NotEmpty(window.TbwCandidateList.Children);
                Assert.Equal("Verified candidate found", window.TbwLookupHeadline.Text);
                var candidateBorder = Assert.IsType<Border>(Assert.Single(window.TbwCandidateList.Children));
                var candidateGrid = Assert.IsType<Grid>(candidateBorder.Child);
                var applyButton = Assert.IsType<Button>(candidateGrid.Children[1]);
                applyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.StartsWith("Applied 600 TBW", window.TbwLookupHeadline.Text);

                window.RenderTbwResult(knownSsd, new TbwLookupResult(knownSsd.FriendlyName, [], DateTime.UtcNow));
                Assert.Equal("No verified TBW rating found", window.TbwLookupHeadline.Text);
                Assert.Equal("No TBW rating was found on the web for this drive.", window.TbwLookupStatus.Text);
                window.RenderTbwResult(knownSsd, new TbwLookupResult(knownSsd.FriendlyName, [], DateTime.UtcNow, "No verified evidence."));
                Assert.Equal("No verified evidence.", window.TbwLookupStatus.Text);
                window.RenderTbwResult(knownSsd, new TbwLookupResult(knownSsd.FriendlyName,
                    [
                        new TbwCandidate(600, .75, 4, ["a.com", "b.com", "c.com", "d.com"], "https://a.com"),
                        new TbwCandidate(1200, .25, 2, ["e.com", "f.com"], "https://e.com"),
                    ], DateTime.UtcNow));
                Assert.Equal("Verified candidates found", window.TbwLookupHeadline.Text);
                Assert.Equal(2, window.TbwCandidateList.Children.Count);

                config.Current.WebSearchProvider = "google";
                window.LoadSettingsFields();
                window.Save_Click(window, new RoutedEventArgs());
                await window.StartTbwLookupAsync(knownSsd, force: true, userInitiated: true);
                Assert.Equal("Lookup unavailable", window.TbwLookupHeadline.Text);

                config.Current.WebSearchProvider = "serper";
                window.LoadSettingsFields();
                window.TxtSerperKey.Password = AiSecretsStore.Load().SerperApiKey ?? "";
                window.Save_Click(window, new RoutedEventArgs());
                await window.StartTbwLookupAsync(knownSsd, force: true, userInitiated: true);
                Assert.DoesNotContain("not configured", window.TbwLookupStatus.Text, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(Visibility.Visible, window.TbwLookupPanel.Visibility);
                Assert.Equal(Visibility.Collapsed, window.TbwLookupProgressBar.Visibility);
                window.ShowTbwLookupPanel(false);
                window.ShowTbwLookupPanel(true);
                Invoke(window, "TbwLookupClose_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Collapsed, window.TbwLookupPanel.Visibility);
                Invoke(window, "TbwLookupAgain_Click", window, new RoutedEventArgs());
                await Task.Delay(50);
                Assert.Equal(Visibility.Visible, window.TbwLookupPanel.Visibility);

                Invoke(window, "LookupRatedTbw_Click", window, new RoutedEventArgs());
                var action = new Button { Tag = "not-download" };
                Invoke(window, "TbwLookupAction_Click", action, new RoutedEventArgs());
                window.TbwModelDownloader = (_, _) => Task.CompletedTask;
                Invoke(window, "TbwLookupAction_Click", new Button { Tag = "download" }, new RoutedEventArgs());
                await Task.Delay(50);
                window.TbwModelDownloader = (_, _) => Task.FromException(new OperationCanceledException());
                Invoke(window, "TbwLookupAction_Click", new Button { Tag = "download" }, new RoutedEventArgs());
                await Task.Delay(50);
                window.TbwModelDownloader = (_, _) => Task.FromException(new InvalidOperationException("download failed"));
                Invoke(window, "TbwLookupAction_Click", new Button { Tag = "download" }, new RoutedEventArgs());
                await Task.Delay(50);
                Assert.Contains("download failed", window.TbwLookupStatus.Text);

                Assert.True(MainWindow.ShouldPromptTbwOnlineSetup(new AppConfig(), new AiSecrets()));
                Assert.False(MainWindow.ShouldPromptTbwOnlineSetup(
                    new AppConfig { SuppressTbwOnlineSetupPrompt = true }, new AiSecrets()));
                Assert.False(MainWindow.ShouldPromptTbwOnlineSetup(
                    new AppConfig(), new AiSecrets { SerperApiKey = "configured" }));

                window.ShowTbwOnlineSetup();
                Assert.Equal(Visibility.Visible, window.TbwSetupOverlay.Visibility);
                Assert.Equal(Visibility.Visible, window.TbwSetupIntroPanel.Visibility);
                Invoke(window, "TbwSetupConfigure_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Visible, window.TbwSetupKeyPanel.Visibility);
                Invoke(window, "TbwSetupBack_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Visible, window.TbwSetupIntroPanel.Visibility);
                Invoke(window, "TbwSetupShowFromSettings_Click", window, new RoutedEventArgs());

                string? openedUrl = null;
                window.TbwSetupUrlLauncher = url => openedUrl = url;
                Invoke(window, "TbwSetupOpenSerper_Click", window, new RoutedEventArgs());
                Assert.Equal("https://serper.dev/signup", openedUrl);
                Assert.Equal(Visibility.Collapsed, window.TbwSetupErrorText.Visibility);
                window.TbwSetupUrlLauncher = _ => throw new InvalidOperationException("browser blocked");
                Invoke(window, "TbwSetupOpenSerper_Click", window, new RoutedEventArgs());
                Assert.Contains("browser blocked", window.TbwSetupErrorText.Text);

                Invoke(window, "TbwSetupConfigure_Click", window, new RoutedEventArgs());
                window.TbwSetupSerperKey.Password = "short";
                Invoke(window, "TbwSetupSave_Click", window, new RoutedEventArgs());
                Assert.Contains("Paste the API key", window.TbwSetupErrorText.Text);

                const string onboardingKey = "synthetic-serper-onboarding-test-key";
                object ssdChoice = ((IEnumerable)window.DiskSelector.ItemsSource).Cast<object>()
                    .First(choice => ((DiskInfo)choice.GetType().GetProperty("Disk")!.GetValue(choice)!).DiskId == "8");
                window.DiskSelector.SelectedItem = ssdChoice;
                window.TbwSetupSerperKey.Password = onboardingKey;
                window.TbwSetupDontShowAgain.IsChecked = true;
                Invoke(window, "TbwSetupSave_Click", window, new RoutedEventArgs());
                await Task.Delay(50);
                Assert.Equal(Visibility.Collapsed, window.TbwSetupOverlay.Visibility);
                Assert.Equal("serper", config.Current.WebSearchProvider);
                Assert.True(config.Current.EnableTbwWebLookup);
                Assert.True(config.Current.SuppressTbwOnlineSetupPrompt);
                string encryptedJson = File.ReadAllText(AiSecretsStore.FilePath);
                Assert.DoesNotContain(onboardingKey, encryptedJson);
                Assert.Equal(onboardingKey, AiSecretsStore.Load().SerperApiKey);

                window.ShowTbwOnlineSetup();
                Invoke(window, "TbwSetupConfigure_Click", window, new RoutedEventArgs());
                window.TbwSetupSerperKey.Password = onboardingKey;
                window.TbwSetupSecretsSaver = _ => throw new IOException("DPAPI unavailable");
                Invoke(window, "TbwSetupSave_Click", window, new RoutedEventArgs());
                await Task.Delay(50);
                Assert.Contains("DPAPI unavailable", window.TbwSetupErrorText.Text);
                window.TbwSetupSecretsSaver = AiSecretsStore.Save;

                config.Current.SuppressTbwOnlineSetupPrompt = false;
                config.Save(config.Current);
                window.ShowTbwOnlineSetup();
                Invoke(window, "TbwSetupNotNow_Click", window, new RoutedEventArgs());
                Assert.False(config.Current.SuppressTbwOnlineSetupPrompt);
                window.ShowTbwOnlineSetup();
                window.TbwSetupDontShowAgain.IsChecked = true;
                Invoke(window, "TbwSetupNotNow_Click", window, new RoutedEventArgs());
                Assert.True(config.Current.SuppressTbwOnlineSetupPrompt);

                config.Current.SuppressTbwOnlineSetupPrompt = false;
                config.Save(config.Current);
                try { File.Delete(AiSecretsStore.FilePath); } catch { }
                using var controller = new TrayController(repo, config);
                bool presented = false;
                controller.TbwSetupPresenter = () => { presented = true; window.ShowTbwOnlineSetup(); };
                Assert.True(controller.PromptTbwOnlineSetupIfNeeded());
                Assert.True(presented);
                Assert.Equal(Visibility.Visible, window.TbwSetupOverlay.Visibility);
                config.Current.SuppressTbwOnlineSetupPrompt = true;
                config.Save(config.Current);
                Assert.False(controller.PromptTbwOnlineSetupIfNeeded());

                config.Current.SuppressTbwOnlineSetupPrompt = true;
                config.Save(config.Current);
                controller.Initialize();
                repo.AcknowledgeAlerts();
                Invoke(controller, "Update");

                try { File.Delete(AiSecretsStore.FilePath); } catch { }
                window.LoadSettingsFields();
                Assert.Empty(window.TxtSerperKey.Password);
            }
            finally { window.ForceClose(); }
        });
    }

    [Theory]
    [InlineData(2, "2%")]
    [InlineData(2.5, "2.5%")]
    [InlineData(2.555, "2.56%")]
    public void FormatPercent_ShowsUpToTwoDecimalPlaces(double value, string expected)
        => Assert.Equal(expected, MainWindow.FormatPercent(value));

    [Fact]
    public void MarkdownToPlainText_RemovesPresentationSyntax()
    {
        string markdown = "## Heading\n\n> Note with `code` and ![alt](image.png).\n\n| A | B |\n|---|---|\n| 1 | 2 |";
        string text = MainWindow.MarkdownToPlainText(markdown);
        Assert.Contains("Heading", text);
        Assert.Contains("Note with code and alt", text);
        Assert.DoesNotContain("##", text);
        Assert.DoesNotContain("image.png", text);
        Assert.DoesNotContain("---", text);
    }

    [Fact]
    public void DriveLifeUsed_UsesPreciseLifetimeWritesPercentage()
    {
        RunStaAsync(() =>
        {
            EnsureApplication();
            var repo = new MonitorRepository(_db); repo.EnsureSchema();
            repo.UpsertDisks([new DiskInfo
            {
                DiskId = "0", InstanceName = "0 C:", FriendlyName = "Precise SSD", Volumes = "C:",
                MediaType = DiskMediaType.Ssd, WearPercent = 2,
                LifetimeBytesWritten = 70_800_000_000_000,
            }]);
            using var config = new ConfigStore(_cfg);
            config.Current.DefaultSsdTbw = 750;
            config.Save(config.Current);

            var window = new MainWindow(repo, config);
            try
            {
                Assert.Equal("~9.44%", window.SmartWearValue.Text);
                Assert.Contains("drive SMART reports 2% used", window.SmartWearText.Text);

                config.Current.DefaultSsdTbwUpper = 1_000;
                Invoke(window, "UpdateEndurance", repo.GetDisks().Single(), DateTime.UtcNow, 0L);
                Assert.Equal("~7.08%\u20139.44%", window.SmartWearValue.Text);

                Invoke(window, "UpdateEndurance", new DiskInfo
                {
                    DiskId = "0", InstanceName = "0 C:", MediaType = DiskMediaType.Ssd, WearPercent = 2,
                }, DateTime.UtcNow, 0L);
                Assert.Equal("2%", window.SmartWearValue.Text);
                Assert.Contains("whole-percent precision", window.SmartWearText.Text);

                Invoke(window, "UpdateEndurance", new DiskInfo
                {
                    DiskId = "0", InstanceName = "0 C:", MediaType = DiskMediaType.Ssd,
                    LifetimeBytesWritten = 70_800_000_000_000,
                }, DateTime.UtcNow, 0L);
                Assert.Contains("no SMART wear attribute", window.SmartWearText.Text);
            }
            finally { window.ForceClose(); }

            return Task.CompletedTask;
        });
    }

    private static AlertRecord Alert(DateTime time, AlertSeverity severity, string rule, double value) => new()
    {
        TimestampUtc = time, Severity = severity, RuleKey = rule, Title = rule, Message = "message", Value = value, Threshold = 1,
    };

    private static DiskInfo Disk(string id = "2") => new()
    {
        DiskId = id, InstanceName = $"{id} F:", FriendlyName = "Test HDD", Volumes = "F:", MediaType = DiskMediaType.Hdd,
    };

    private static SmartHealthScanResult Result(SmartScanGrade grade, int controllerErrors, int? temperature,
        bool telemetry, int? maximum = null) => new()
    {
        DiskId = "2", DevicePath = @"\\.\PhysicalDrive2", DisplayName = "F:", Model = "Model", SerialNumber = "Serial",
        FirmwareVersion = "FW", BusType = "USB", WindowsHealth = grade == SmartScanGrade.Critical ? "Unhealthy" : "Healthy",
        OperationalStatus = "OK", DeviceStatus = "OK", SmartAccess = telemetry ? "Direct" : "Limited", ScannedUtc = DateTime.UtcNow,
        DiskPresent = grade != SmartScanGrade.Unavailable, SmartTelemetryAvailable = telemetry, ControllerErrorCount = controllerErrors,
        TemperatureC = temperature, TemperatureMaxC = maximum, LifetimeBytesWritten = 1, LifetimeBytesRead = 2,
        Grade = grade, Headline = grade.ToString(), Summary = "summary", Findings = ["finding"],
    };

    private static void Invoke(object target, string method, params object[] args)
        => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    [Fact]
    public void AlertHistory_RendersEmptyAndSingularStates()
    {
        RunStaAsync(() =>
        {
            EnsureApplication();
            var repo = new MonitorRepository(_db); repo.EnsureSchema();
            repo.UpsertDisks([Disk()]);
            using var config = new ConfigStore(_cfg);
            var window = new MainWindow(repo, config);
            try
            {
                window.UpdateAlertHistory();
                Assert.Equal(Visibility.Visible, window.AlertHistoryEmpty.Visibility);
                Assert.Equal("0 alerts  ·  0 dismissed", window.AlertHistorySummary.Text);

                repo.InsertAlert(Alert(DateTime.UtcNow, AlertSeverity.Info, "single", 1));
                window.UpdateAlertHistory();
                Assert.Equal(Visibility.Collapsed, window.AlertHistoryEmpty.Visibility);
                Assert.Equal("1 alert  ·  0 dismissed", window.AlertHistorySummary.Text);
            }
            finally { window.ForceClose(); }
            return Task.CompletedTask;
        });
    }

    private static object CreateAlertRow(string diskId, bool canScan, long[]? alertIds = null)
    {
        Type type = typeof(MainWindow).GetNestedType("AlertRow", BindingFlags.NonPublic)!;
        return Activator.CreateInstance(type, "title", "message", "time", Brushes.Red,
            diskId, 2, canScan, canScan ? Visibility.Visible : Visibility.Collapsed, alertIds ?? new long[] { 1 })!;
    }

    private static async Task ExerciseStaleScans(MainWindow window)
    {
        using var successGate = new ManualResetEventSlim();
        var successStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.SmartScanner = (_, _, _) => { successStarted.TrySetResult(); successGate.Wait(); return Result(SmartScanGrade.Healthy, 0, 40, true); };
        Task staleSuccess = window.StartSmartScanAsync(Disk(), 0);
        await successStarted.Task;
        window.SmartScanClose_Click(window, new RoutedEventArgs());
        successGate.Set();
        await staleSuccess;

        using var failureGate = new ManualResetEventSlim();
        var failureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.SmartScanner = (_, _, _) => { failureStarted.TrySetResult(); failureGate.Wait(); throw new InvalidOperationException("stale"); };
        Task staleFailure = window.StartSmartScanAsync(Disk(), 0);
        await failureStarted.Task;
        window.SmartScanClose_Click(window, new RoutedEventArgs());
        failureGate.Set();
        await staleFailure;
    }

    private static void EnsureApplication()
    {
        if (Application.Current is null)
        {
            var app = new DiskActivityMonitor.Tray.App();
            app.InitializeComponent();
        }
        var current = Application.Current ?? throw new InvalidOperationException("WPF application was not initialized.");
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
        thread.Start(); thread.Join();
        if (error is not null) throw new TargetInvocationException(error);
    }
}
