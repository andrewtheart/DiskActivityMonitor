using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskActivityMonitor.Core;
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
    private readonly string _userSettings = Path.Combine(Path.GetTempPath(), $"dam_wpf_user_{Guid.NewGuid():N}.json");
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
        try { File.Delete(_userSettings); } catch { }
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
            var userSettings = new UserSettingsStore(_userSettings);
            UpdateUserSettings(userSettings, settings => settings.EnableNotifications = false);
            var now = DateTime.UtcNow;
            long ordinaryId = repo.InsertAlert(Alert(now.AddMinutes(-3), AlertSeverity.Info, "ordinary", 0));
            repo.InsertAlert(Alert(now.AddMinutes(-2), AlertSeverity.Warning, "disk-controller:2", 4));
            repo.InsertAlert(Alert(now.AddMinutes(-1), AlertSeverity.Critical, "disk-controller:2", 5));

            var window = new MainWindow(repo, config, userSettings);
            window.Resources["TextPrimary"] = new SolidColorBrush(Colors.White);
            window.Resources["Caption"] = new Style(typeof(TextBlock));
            window.Resources["ToolButton"] = new Style(typeof(Button));
            object hddChoice = ((IEnumerable)window.DiskSelector.ItemsSource).Cast<object>()
                .First(choice => ((DiskInfo)choice.GetType().GetProperty("Disk")!.GetValue(choice)!).DiskId == "2");
            window.DiskSelector.SelectedItem = hddChoice;
            try
            {
                Assert.Equal("Segoe UI", window.FontFamily.Source);
                Assert.NotNull(window.HeaderAppIcon.Source);
                Assert.Equal(42, window.HeaderAppIcon.Width);
                Assert.Equal(42, window.HeaderAppIcon.Height);
                var headerPanel = Assert.IsType<StackPanel>(window.HeaderAppIcon.Parent);
                Assert.Equal(Orientation.Horizontal, headerPanel.Orientation);
                Assert.Same(window.HeaderAppIcon, headerPanel.Children[0]);
                var titlePanel = Assert.IsType<StackPanel>(headerPanel.Children[1]);
                Assert.Equal("Disk Activity Monitor", Assert.IsType<TextBlock>(titlePanel.Children[0]).Text);

                Invoke(window, "SaveRules_Click", window, new RoutedEventArgs());
                Assert.False(userSettings.Current.EnableNotifications);

                using (var defaultPresenterController = new TrayController(repo, config, userSettings))
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
                var alertTemplateRoot = Assert.IsAssignableFrom<FrameworkElement>(window.AlertList.ItemTemplate.LoadContent());
                var alertDismissButton = Assert.IsType<Button>(alertTemplateRoot.FindName("AlertDismissButton"));
                Assert.Equal(28, alertDismissButton.Width);
                Assert.Equal(28, alertDismissButton.Height);
                Assert.Equal(new Thickness(1), alertDismissButton.BorderThickness);
                Assert.Equal(Color.FromRgb(0x3B, 0x24, 0x28), Assert.IsType<SolidColorBrush>(alertDismissButton.Background).Color);
                Assert.Equal("\u2715", alertDismissButton.Content);
                Assert.Equal("Segoe UI", alertDismissButton.FontFamily.Source);
                Assert.Equal("\u2715", window.AlertSearchClearButton.Content);
                Assert.Equal("Segoe UI", window.AlertSearchClearButton.FontFamily.Source);
                Assert.Equal(LocalTimeDisplay.ZoneLabel(), window.TrendTimeZoneText.Text);
                Assert.All(rows, row => Assert.EndsWith($"({LocalTimeDisplay.ZoneId()})",
                    (string)row.GetType().GetProperty("TimeText")!.GetValue(row)!));

                window.AlertSearchBox.Text = "ORDINARY";
                object searchResult = Assert.Single(((IEnumerable)window.AlertList.ItemsSource).Cast<object>());
                Assert.Equal("ordinary", searchResult.GetType().GetProperty("Title")!.GetValue(searchResult));
                Assert.Equal(Visibility.Visible, window.AlertSearchClearButton.Visibility);
                window.UpdateAlerts();
                Assert.Single(((IEnumerable)window.AlertList.ItemsSource).Cast<object>());
                window.AlertSearchBox.Text = "MESSAGE";
                Assert.Equal(2, ((IEnumerable)window.AlertList.ItemsSource).Cast<object>().Count());
                window.AlertSearchBox.Text = "no matching alert";
                Assert.Empty(((IEnumerable)window.AlertList.ItemsSource).Cast<object>());
                Assert.Equal(Visibility.Visible, window.AlertEmpty.Visibility);
                Assert.Equal("No alerts match your search.", window.AlertEmpty.Text);
                Invoke(window, "AlertSearchClear_Click", window.AlertSearchClearButton, new RoutedEventArgs());
                Assert.Equal("", window.AlertSearchBox.Text);
                Assert.Equal(2, ((IEnumerable)window.AlertList.ItemsSource).Cast<object>().Count());
                Assert.Equal(Visibility.Collapsed, window.AlertSearchClearButton.Visibility);

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
                Assert.All(historyRows, row => Assert.EndsWith($"({LocalTimeDisplay.ZoneId()})",
                    (string)row.GetType().GetProperty("TimeText")!.GetValue(row)!));
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
                    Assert.EndsWith($"({LocalTimeDisplay.ZoneId()})", window.SmartScanTimeValue.Text);
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
                window.RadTbwRange.IsChecked = true;
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
                Assert.Equal("Enter a minimum TBW greater than 0.", window.SaveStatus.Text);
                Assert.Equal(900, config.Current.DiskTbwRatings["2"]);
                Assert.Equal(1200, config.Current.DiskTbwRatingsUpper["2"]);

                window.RadTbwSingle.IsChecked = true;
                window.TxtTbw.Text = "900";
                window.Save_Click(window, new RoutedEventArgs());
                Assert.Equal(900, config.Current.DiskTbwRatings["2"]);
                Assert.False(config.Current.DiskTbwRatingsUpper.ContainsKey("2"));
                Assert.Equal(Visibility.Collapsed, window.TbwUpperPanel.Visibility);

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
                Assert.False(window.HandleTbwReadiness(new TbwReadiness(
                    false,
                    "Install the official package.",
                    false,
                    null,
                    false,
                    NeedsFoundryInstall: true)));
                Assert.Equal("Foundry Local required", window.TbwLookupHeadline.Text);
                Assert.Equal("Install Foundry Local", window.TbwLookupAction.Content);
                Assert.Equal("install-foundry", window.TbwLookupAction.Tag);
                Assert.Equal(Visibility.Visible, window.TbwLookupAction.Visibility);
                Assert.Equal(Visibility.Collapsed, window.TbwLookupAgainButton.Visibility);
                Assert.False(window.HandleTbwReadiness(new TbwReadiness(false, "configured reason", false, null, false)));
                Assert.Equal("configured reason", window.TbwLookupStatus.Text);
                Assert.False(window.HandleTbwReadiness(new TbwReadiness(false, null, false, null, false)));
                Assert.Equal("Web TBW lookup is unavailable.", window.TbwLookupStatus.Text);

                UpdateUserSettings(userSettings, settings => settings.EnableTbwWebLookup = false);
                await window.StartTbwLookupAsync(knownSsd);
                UpdateUserSettings(userSettings, settings => settings.EnableTbwWebLookup = true);
                config.Update(settings => settings.DiskTbwRatings[knownSsd.DiskId] = 600);
                await window.StartTbwLookupAsync(knownSsd);
                config.Update(settings => settings.DiskTbwRatings.Remove(knownSsd.DiskId));
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

                const string searchResponse = "{\"organic\":[{\"title\":\"Drive result\"}]}";
                const string modelResponse =
                    "{\"choices\":[{\"message\":{\"content\":\"<think>returned reasoning</think>\\n[]\"}}]}";
                window.RenderTbwResult(knownSsd, new TbwLookupResult(
                    knownSsd.FriendlyName,
                    [],
                    DateTime.UtcNow,
                    Diagnostics: new TbwLookupDiagnostics("Serper.dev", searchResponse, "qwen3-8b", modelResponse)));
                Assert.Equal(Visibility.Visible, window.TbwLookupRawResultsButton.Visibility);
                Invoke(window, "TbwLookupRawResults_Click", window.TbwLookupRawResultsButton, new RoutedEventArgs());
                Assert.Equal(Visibility.Visible, window.TbwRawResultsOverlay.Visibility);
                Assert.Same(window.TbwRawSearchTab, window.TbwRawResultsTabs.SelectedItem);
                Assert.Contains(Environment.NewLine, window.TbwRawSearchText.Text);
                Assert.Contains("Drive result", window.TbwRawSearchText.Text);
                window.TbwRawResultsTabs.SelectedItem = window.TbwRawModelTab;
                Assert.Contains("<think>returned reasoning</think>", window.TbwRawModelText.Text);
                Assert.Contains("qwen3-8b", window.TbwRawModelMeta.Text);
                Invoke(window, "TbwRawResultsClose_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Collapsed, window.TbwRawResultsOverlay.Visibility);
                Assert.Empty(window.TbwRawSearchText.Text);
                Assert.Empty(window.TbwRawModelText.Text);

                window.RenderTbwResult(knownSsd, new TbwLookupResult(
                    knownSsd.FriendlyName,
                    [],
                    DateTime.UtcNow,
                    Diagnostics: new TbwLookupDiagnostics("Serper.dev", searchResponse)));
                Invoke(window, "TbwLookupRawResults_Click", window.TbwLookupRawResultsButton, new RoutedEventArgs());
                Assert.Contains("Serper-only lookups do not call a model", window.TbwRawModelText.Text);
                Invoke(window, "TbwRawResultsClose_Click", window, new RoutedEventArgs());

                window.RenderTbwResult(knownSsd, new TbwLookupResult(knownSsd.FriendlyName, [], DateTime.UtcNow));
                Assert.Equal(Visibility.Collapsed, window.TbwLookupRawResultsButton.Visibility);
                window.RenderTbwResult(knownSsd, new TbwLookupResult(knownSsd.FriendlyName,
                    [
                        new TbwCandidate(600, .75, 4, ["a.com", "b.com", "c.com", "d.com"], "https://a.com"),
                        new TbwCandidate(1200, .25, 2, ["e.com", "f.com"], "https://e.com"),
                    ], DateTime.UtcNow, LookupMethod: TbwLookupMethod.SerperOnly));
                Assert.Equal("Evidence candidates found", window.TbwLookupHeadline.Text);
                Assert.Equal("Serper evidence only", window.TbwLookupAnalysisTitle.Text);
                Assert.Contains("No local AI verification", window.TbwLookupStatus.Text);
                Assert.Equal(3, window.TbwCandidateList.Children.Count);

                var rangeBorder = Assert.IsType<Border>(window.TbwCandidateList.Children[0]);
                var rangeGrid = Assert.IsType<Grid>(rangeBorder.Child);
                var rangeButton = Assert.IsType<Button>(rangeGrid.Children[1]);
                Assert.Equal("Apply range", rangeButton.Content);
                rangeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(600, config.Current.DiskTbwRatings[knownSsd.DiskId]);
                Assert.Equal(1200, config.Current.DiskTbwRatingsUpper[knownSsd.DiskId]);
                Assert.StartsWith("Applied 600 to 1200 TBW", window.TbwLookupHeadline.Text);

                window.RenderTbwResult(knownSsd, new TbwLookupResult(knownSsd.FriendlyName,
                    [new TbwCandidate(600, 1, 2, ["a.com", "b.com"], "https://a.com")],
                    DateTime.UtcNow,
                    LookupMethod: TbwLookupMethod.SerperOnly));
                var singleBorder = Assert.IsType<Border>(Assert.Single(window.TbwCandidateList.Children));
                var singleGrid = Assert.IsType<Grid>(singleBorder.Child);
                var singleButton = Assert.IsType<Button>(singleGrid.Children[1]);
                Assert.Equal("Apply single", singleButton.Content);
                singleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(config.Current.DiskTbwRatingsUpper.ContainsKey(knownSsd.DiskId));

                UpdateUserSettings(userSettings, settings => settings.WebSearchProvider = "google");
                window.LoadSettingsFields();
                window.Save_Click(window, new RoutedEventArgs());
                await window.StartTbwLookupAsync(knownSsd, force: true, userInitiated: true);
                Assert.Equal("Lookup unavailable", window.TbwLookupHeadline.Text);

                UpdateUserSettings(userSettings, settings => settings.WebSearchProvider = "serper");
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

                bool foundryInstallRetried = false;
                window.FoundryLocalInstaller = (progress, _) =>
                {
                    progress?.Report("Installing test package...");
                    return Task.CompletedTask;
                };
                window.TbwPostInstallLookup = disk =>
                {
                    Assert.Same(knownSsd, disk);
                    foundryInstallRetried = true;
                    return Task.CompletedTask;
                };
                await window.InstallFoundryLocalAndRetryAsync(knownSsd);
                Assert.True(foundryInstallRetried);
                Assert.Equal("Foundry Local installed", window.TbwLookupHeadline.Text);

                window.FoundryLocalInstaller = (_, _) =>
                    Task.FromException(new InvalidOperationException("installer failed"));
                foundryInstallRetried = false;
                await window.InstallFoundryLocalAndRetryAsync(knownSsd);
                Assert.False(foundryInstallRetried);
                Assert.Equal("Foundry Local installation failed", window.TbwLookupHeadline.Text);
                Assert.Contains("installer failed", window.TbwLookupStatus.Text);
                Assert.Equal("Try install again", window.TbwLookupAction.Content);
                Assert.Equal(Visibility.Visible, window.TbwLookupAction.Visibility);

                Assert.True(MainWindow.ShouldPromptTbwOnlineSetup(new UserSettings(), new AiSecrets()));
                Assert.False(MainWindow.ShouldPromptTbwOnlineSetup(
                    new UserSettings { SuppressTbwOnlineSetupPrompt = true }, new AiSecrets()));
                Assert.False(MainWindow.ShouldPromptTbwOnlineSetup(
                    new UserSettings(), new AiSecrets { SerperApiKey = "configured" }));

                window.ShowTbwOnlineSetup();
                Assert.Equal(Visibility.Visible, window.TbwSetupOverlay.Visibility);
                Assert.Equal(Visibility.Visible, window.TbwSetupIntroPanel.Visibility);
                Invoke(window, "TbwSetupConfigure_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Visible, window.TbwSetupKeyPanel.Visibility);
                window.TbwSetupSerperKey.Password = "synthetic-hidden-key";
                window.TbwSetupSerperKeyRevealButton.IsChecked = true;
                Assert.Equal(Visibility.Collapsed, window.TbwSetupSerperKey.Visibility);
                Assert.Equal(Visibility.Visible, window.TbwSetupSerperKeyReveal.Visibility);
                Assert.Equal("synthetic-hidden-key", window.TbwSetupSerperKeyReveal.Text);
                Assert.Equal("Hide API key", window.TbwSetupSerperKeyRevealButton.ToolTip);
                window.TbwSetupSerperKeyReveal.Text = "synthetic-edited-key";
                window.TbwSetupSerperKeyRevealButton.IsChecked = false;
                Assert.Equal(Visibility.Visible, window.TbwSetupSerperKey.Visibility);
                Assert.Equal(Visibility.Collapsed, window.TbwSetupSerperKeyReveal.Visibility);
                Assert.Empty(window.TbwSetupSerperKeyReveal.Text);
                Assert.Equal("synthetic-edited-key", window.TbwSetupSerperKey.Password);
                Assert.Equal("Show API key", window.TbwSetupSerperKeyRevealButton.ToolTip);
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
                window.TbwSetupSerperKey.Password = "stale-before-reveal";
                window.TbwSetupSerperKeyRevealButton.IsChecked = true;
                window.TbwSetupSerperKeyReveal.Text = onboardingKey;
                window.TbwSetupDontShowAgain.IsChecked = true;
                Invoke(window, "TbwSetupSave_Click", window, new RoutedEventArgs());
                await Task.Delay(50);
                Assert.Equal(Visibility.Collapsed, window.TbwSetupOverlay.Visibility);
                Assert.False(window.TbwSetupSerperKeyRevealButton.IsChecked);
                Assert.Empty(window.TbwSetupSerperKeyReveal.Text);
                Assert.Empty(window.TbwSetupSerperKey.Password);
                Assert.Equal("serper", userSettings.Current.WebSearchProvider);
                Assert.True(userSettings.Current.EnableTbwWebLookup);
                Assert.True(userSettings.Current.SuppressTbwOnlineSetupPrompt);
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

                UpdateUserSettings(userSettings, settings => settings.SuppressTbwOnlineSetupPrompt = false);
                window.ShowTbwOnlineSetup();
                Invoke(window, "TbwSetupNotNow_Click", window, new RoutedEventArgs());
                Assert.False(userSettings.Current.SuppressTbwOnlineSetupPrompt);
                window.ShowTbwOnlineSetup();
                window.TbwSetupDontShowAgain.IsChecked = true;
                Invoke(window, "TbwSetupNotNow_Click", window, new RoutedEventArgs());
                Assert.True(userSettings.Current.SuppressTbwOnlineSetupPrompt);

                UpdateUserSettings(userSettings, settings => settings.SuppressTbwOnlineSetupPrompt = false);
                try { File.Delete(AiSecretsStore.FilePath); } catch { }
                using var controller = new TrayController(repo, config, userSettings);
                bool presented = false;
                controller.TbwSetupPresenter = () => { presented = true; window.ShowTbwOnlineSetup(); };
                Assert.True(controller.PromptTbwOnlineSetupIfNeeded());
                Assert.True(presented);
                Assert.Equal(Visibility.Visible, window.TbwSetupOverlay.Visibility);
                UpdateUserSettings(userSettings, settings => settings.SuppressTbwOnlineSetupPrompt = true);
                Assert.False(controller.PromptTbwOnlineSetupIfNeeded());

                UpdateUserSettings(userSettings, settings => settings.SuppressTbwOnlineSetupPrompt = true);
                controller.Initialize();
                var trayMenu = Assert.IsType<DarkTrayContextMenu>(
                    typeof(TrayController).GetField("_trayMenu", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .GetValue(controller));
                Assert.IsType<DarkTrayMenuRenderer>(trayMenu.Renderer);
                Assert.Equal(System.Drawing.Color.FromArgb(20, 22, 25), trayMenu.BackColor);
                Assert.Equal(System.Drawing.Color.FromArgb(242, 244, 247), trayMenu.ForeColor);
                Assert.False(trayMenu.ShowImageMargin);
                Assert.False(trayMenu.ShowCheckMargin);
                Assert.Collection(trayMenu.Items.Cast<System.Windows.Forms.ToolStripItem>(),
                    item => Assert.Equal("Open dashboard", item.Text),
                    item => Assert.Equal("Open data folder", item.Text),
                    item => Assert.IsType<System.Windows.Forms.ToolStripSeparator>(item),
                    item => Assert.Equal("Exit", item.Text));
                _ = trayMenu.Handle;
                Assert.NotNull(trayMenu.Region);
                Assert.False(trayMenu.Region.IsVisible(0, 0));
                Assert.True(trayMenu.Region.IsVisible(trayMenu.Width / 2, trayMenu.Height / 2));

                // Item text is inset from both edges instead of hugging the menu border.
                Assert.Equal(System.Drawing.SystemFonts.MenuFont!.Name, trayMenu.Font.Name);
                var textRect = DarkTrayContextMenu.GetTextRectangle(200, 30);
                Assert.Equal(DarkTrayContextMenu.TextInsetLeft, textRect.X);
                Assert.Equal(200 - DarkTrayContextMenu.TextInsetLeft - DarkTrayContextMenu.TextInsetRight, textRect.Width);
                Assert.Equal(30, textRect.Height);
                Assert.Equal(0, DarkTrayContextMenu.GetTextRectangle(4, 30).Width);
                var command = Assert.IsType<System.Windows.Forms.ToolStripMenuItem>(trayMenu.Items[0]);
                Assert.Equal(DarkTrayContextMenu.TextInsetLeft, command.Padding.Left);
                Assert.Equal(DarkTrayContextMenu.TextInsetRight, command.Padding.Right);
                Assert.True(command.Width > DarkTrayContextMenu.TextInsetLeft + DarkTrayContextMenu.TextInsetRight);

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

    [Theory]
    [InlineData("https://example.test/help", true)]
    [InlineData("http://127.0.0.1/help", true)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("custom-protocol:payload", false)]
    [InlineData("not a uri", false)]
    public void ExternalHelpUriPolicy_AllowsOnlyHttpAndHttps(string value, bool expected)
        => Assert.Equal(expected, MainWindow.IsAllowedExternalHelpUri(value));

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
            config.Update(settings => settings.DefaultSsdTbw = 750);

            var window = new MainWindow(repo, config, new UserSettingsStore(_userSettings));
            try
            {
                Assert.Equal("~9.44%", window.SmartWearValue.Text);
                Assert.Contains("drive SMART reports 2% used", window.SmartWearText.Text);

                config.Update(settings => settings.DefaultSsdTbwUpper = 1_000);
                Invoke(window, "UpdateEndurance", repo.GetDisks().Single(), DateTime.UtcNow, 0L);
                Assert.Equal("~7.08% to 9.44%", window.SmartWearValue.Text);

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

    [Fact]
    public void Settings_UnchangedUnknownTbw_RemainsEstimatedAndModesAreExplicit()
    {
        RunStaAsync(() =>
        {
            EnsureApplication();
            var repo = new MonitorRepository(_db); repo.EnsureSchema();
            repo.UpsertDisks([new DiskInfo
            {
                DiskId = "estimate", InstanceName = "estimate C:", FriendlyName = "Unknown SSD", Volumes = "C:",
                MediaType = DiskMediaType.Ssd,
            }]);
            using var config = new ConfigStore(_cfg);
            var window = new MainWindow(repo, config, new UserSettingsStore(_userSettings));
            try
            {
                Assert.Equal("150 to 600 TBW estimated", window.EnduranceRatedText.Text);
                Assert.True(window.RadTbwRange.IsChecked);
                Assert.Equal("Minimum TBW (TB)", window.TbwLowerLabel.Text);
                Assert.Equal(Visibility.Visible, window.TbwUpperPanel.Visibility);

                window.RadTbwSingle.IsChecked = true;
                Assert.Equal("TBW rating (TB)", window.TbwLowerLabel.Text);
                Assert.Equal(Visibility.Collapsed, window.TbwUpperPanel.Visibility);
                window.RadTbwRange.IsChecked = true;

                window.RadTbwLookupSerperOnly.IsChecked = true;
                Assert.False(window.TbwProviderSelector.IsEnabled);
                Assert.Contains("not installed or started", window.TbwLookupMethodHint.Text);
                window.RadTbwLookupFoundry.IsChecked = true;
                Assert.True(window.TbwProviderSelector.IsEnabled);

                window.Save_Click(window, new RoutedEventArgs());

                Assert.False(config.Current.DiskTbwRatings.ContainsKey("estimate"));
                Assert.False(config.Current.DiskTbwRatingsUpper.ContainsKey("estimate"));
                Assert.Equal("150 to 600 TBW estimated", window.EnduranceRatedText.Text);
            }
            finally { window.ForceClose(); }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public void RatedTbwPill_EditorSavesRangeAndSingleAndRefreshesCalculations()
    {
        RunStaAsync(() =>
        {
            EnsureApplication();
            var repo = new MonitorRepository(_db); repo.EnsureSchema();
            repo.UpsertDisks([new DiskInfo
            {
                DiskId = "editable", InstanceName = "editable C:", FriendlyName = "Editable SSD", Volumes = "C:",
                MediaType = DiskMediaType.Ssd, LifetimeBytesWritten = 60_000_000_000_000,
            }]);
            using var config = new ConfigStore(_cfg);
            var window = new MainWindow(repo, config, new UserSettingsStore(_userSettings));
            try
            {
                Assert.Equal("150 to 600 TBW estimated", window.EnduranceRatedText.Text);

                window.EnduranceRatedBadge.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Visibility.Visible, window.TbwEditOverlay.Visibility);
                Assert.True(window.TbwEditRange.IsChecked);
                Assert.Contains("default estimate", window.TbwEditCurrentText.Text);

                window.TbwEditLowerText.Text = "600";
                window.TbwEditUpperText.Text = "1200";
                Assert.Contains("600 to 1200 TBW", window.TbwEditPreviewText.Text);
                window.TbwEditSaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(Visibility.Collapsed, window.TbwEditOverlay.Visibility);
                Assert.Equal(600, config.Current.DiskTbwRatings["editable"]);
                Assert.Equal(1200, config.Current.DiskTbwRatingsUpper["editable"]);
                Assert.Equal("600 to 1200 TBW rated", window.EnduranceRatedText.Text);
                Assert.Equal("~5% to 10%", window.SmartWearValue.Text);
                Assert.Equal("600", window.TxtTbw.Text);
                Assert.Equal("1200", window.TxtTbwUpper.Text);

                window.EnduranceRatedBadge.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains("saved for this drive", window.TbwEditCurrentText.Text);
                window.TbwEditSingle.IsChecked = true;
                Assert.Equal(Visibility.Collapsed, window.TbwEditUpperPanel.Visibility);
                window.TbwEditLowerText.Text = "800";
                window.TbwEditSaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(800, config.Current.DiskTbwRatings["editable"]);
                Assert.False(config.Current.DiskTbwRatingsUpper.ContainsKey("editable"));
                Assert.Equal("800 TBW rated", window.EnduranceRatedText.Text);
                Assert.Equal("~7.5%", window.SmartWearValue.Text);
                Assert.True(window.RadTbwSingle.IsChecked);

                window.EnduranceRatedBadge.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.TbwEditRange.IsChecked = true;
                window.TbwEditLowerText.Text = "900";
                window.TbwEditUpperText.Text = "800";
                window.TbwEditSaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Visibility.Visible, window.TbwEditOverlay.Visibility);
                Assert.Contains("greater than minimum", window.TbwEditErrorText.Text);
                Assert.Equal(800, config.Current.DiskTbwRatings["editable"]);
                Invoke(window, "TbwEditClose_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Collapsed, window.TbwEditOverlay.Visibility);
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

    private static void UpdateUserSettings(UserSettingsStore store, Action<UserSettings> update)
    {
        var settings = store.Current;
        update(settings);
        store.Save(settings);
    }

    [Fact]
    public void AlertHistory_RendersEmptyAndSingularStates()
    {
        RunStaAsync(() =>
        {
            EnsureApplication();
            var repo = new MonitorRepository(_db); repo.EnsureSchema();
            repo.UpsertDisks([Disk()]);
            using var config = new ConfigStore(_cfg);
            var window = new MainWindow(repo, config, new UserSettingsStore(_userSettings));
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

    [Fact]
    public void SettingsHeader_StaysPinnedOutsideTheScrollingContent()
    {
        RunStaAsync(() =>
        {
            EnsureApplication();
            var repo = new MonitorRepository(_db); repo.EnsureSchema();
            repo.UpsertDisks([Disk()]);
            using var config = new ConfigStore(_cfg);
            var window = new MainWindow(repo, config, new UserSettingsStore(_userSettings));
            try
            {
                Assert.Equal(Visibility.Collapsed, window.SettingsHeader.Visibility);

                Invoke(window, "Gear_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Visible, window.SettingsPanel.Visibility);
                Assert.Equal(Visibility.Visible, window.SettingsHeader.Visibility);

                // Scrolling the settings content must never move the back button out of view.
                Assert.DoesNotContain(Ancestors(window.SettingsHeader), element => ReferenceEquals(element, window.BodyScroller));
                Assert.Contains(Ancestors(window.SettingsPanel), element => ReferenceEquals(element, window.BodyScroller));

                Invoke(window, "Gear_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Collapsed, window.SettingsHeader.Visibility);
                Assert.Equal(Visibility.Visible, window.DashboardPanel.Visibility);
            }
            finally { window.ForceClose(); }
            return Task.CompletedTask;
        });
    }

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject element)
    {
        for (var parent = System.Windows.Media.VisualTreeHelper.GetParent(element)
                 ?? System.Windows.LogicalTreeHelper.GetParent(element);
             parent is not null;
             parent = System.Windows.Media.VisualTreeHelper.GetParent(parent)
                 ?? System.Windows.LogicalTreeHelper.GetParent(parent))
        {
            yield return parent;
        }
    }

    [Fact]
    public void FileTargets_ExplainAnOpaqueWriterFromTheProcessCard()
    {
        RunStaAsync(() =>
        {
            EnsureApplication();
            var repo = new MonitorRepository(_db); repo.EnsureSchema();
            repo.UpsertDisks([Disk()]);
            using var config = new ConfigStore(_cfg);
            var window = new MainWindow(repo, config, new UserSettingsStore(_userSettings));
            try
            {
                window.ShowFileTargets("System");
                Assert.Equal(Visibility.Visible, window.FileTargetsOverlay.Visibility);
                Assert.Equal("Files written by System", window.FileTargetsTitle.Text);
                Assert.Equal(Visibility.Visible, window.FileTargetsNoteBorder.Visibility);
                Assert.Contains("kernel", window.FileTargetsNote.Text, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(Visibility.Visible, window.FileTargetsEmpty.Visibility);

                var utcNow = DateTime.UtcNow;
                var minute = new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, utcNow.Minute, 0, DateTimeKind.Utc)
                    .AddMinutes(-2);
                repo.AddProcessSamples([new ProcessIoSample { TimestampUtc = minute, ProcessName = "System", WriteBytes = 4000 }]);
                repo.AddProcessFileSamples(
                [
                    new ProcessFileIoSample
                    {
                        TimestampUtc = minute, ProcessName = "System", Path = @"C:\$Mft",
                        Kind = FileTargetKind.NtfsMetadata, WriteBytes = 3000,
                    },
                    new ProcessFileIoSample
                    {
                        TimestampUtc = minute, ProcessName = "System", Path = @"C:\pagefile.sys",
                        Kind = FileTargetKind.PagingFile, WriteBytes = 1000,
                    },
                ]);

                window.ShowFileTargets("System");
                var rows = ((IEnumerable)window.FileTargetsList.ItemsSource).Cast<object>().ToList();
                Assert.Equal(2, rows.Count);
                Assert.Equal("$Mft", rows[0].GetType().GetProperty("FileName")!.GetValue(rows[0]));
                Assert.Equal("NTFS metadata", rows[0].GetType().GetProperty("KindLabel")!.GetValue(rows[0]));
                Assert.Equal(Visibility.Collapsed, window.FileTargetsEmpty.Visibility);
                Assert.Contains("100%", window.FileTargetsFooter.Text);

                // An ordinary application needs no kernel explanation.
                window.ShowFileTargets("chrome");
                Assert.Equal(Visibility.Collapsed, window.FileTargetsNoteBorder.Visibility);

                window.FileTargetsClose_Click(window, new RoutedEventArgs());
                Assert.Equal(Visibility.Collapsed, window.FileTargetsOverlay.Visibility);
            }
            finally { window.ForceClose(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void FileTargets_DescribeCoverageAndDisabledTracking()
    {
        Assert.Contains("turned off", MainWindow.FileTargetsEmptyText(trackingEnabled: false));
        Assert.Contains("ETW collector", MainWindow.FileTargetsEmptyText(trackingEnabled: true));
        Assert.Equal("Per-file history is kept for 7 day(s).", MainWindow.FileTargetsCoverage(0, 0, 7));
        Assert.Contains("50%", MainWindow.FileTargetsCoverage(1000, 500, 7));
        Assert.Contains("100%", MainWindow.FileTargetsCoverage(1000, 4000, 7));
    }

    [Fact]
    public void SuspendedProcesses_RenderAndResumeFromTheDashboard()    {
        RunStaAsync(() =>
        {
            EnsureApplication();
            var repo = new MonitorRepository(_db); repo.EnsureSchema();
            repo.UpsertDisks([Disk()]);
            using var config = new ConfigStore(_cfg);
            var userSettings = new UserSettingsStore(_userSettings);
            var window = new MainWindow(repo, config, userSettings);
            try
            {
                Invoke(window, "RefreshSuspended");
                Assert.Equal(Visibility.Visible, window.SuspendedProcessEmpty.Visibility);
                Assert.Equal(Visibility.Collapsed, window.ResumeAllSuspendedButton.Visibility);

                var now = DateTime.UtcNow;
                var identities = new[] { new ProcessControl.ProcessIdentity(4242, 1, @"C:\Apps\gone.exe") };
                repo.AddSuspendedProcess("auto", now, null, identities, now.AddMinutes(30), SuspendSource.AutoRule);
                repo.AddSuspendedProcess("manual", now, null, identities, null, SuspendSource.Manual);

                Invoke(window, "RefreshSuspended");
                Assert.Equal(Visibility.Collapsed, window.SuspendedProcessEmpty.Visibility);
                Assert.Equal(Visibility.Visible, window.ResumeAllSuspendedButton.Visibility);
                var rows = ((IEnumerable)window.SuspendedProcessList.ItemsSource).Cast<object>().ToList();
                Assert.Equal(2, rows.Count);
                Assert.Contains(rows, r => (string)r.GetType().GetProperty("SourceLabel")!.GetValue(r)! == "Auto-suspend rule");
                Assert.Contains(rows, r => (string)r.GetType().GetProperty("SourceLabel")!.GetValue(r)! == "Suspended by you");
                Assert.Contains(rows, r => (string)r.GetType().GetProperty("ResumeAutomationName")!.GetValue(r)! == "Resume manual");

                // The tracked identities no longer exist, so resuming clears the stale records.
                Invoke(window, "ResumeAllSuspended_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Visible, window.SuspendedProcessStatus.Visibility);
                Assert.Contains("Could not resume", window.SuspendedProcessStatus.Text);
                Assert.Empty(((IEnumerable)window.SuspendedProcessList.ItemsSource).Cast<object>());
                Assert.Equal(Visibility.Visible, window.SuspendedProcessEmpty.Visibility);

                repo.AddSuspendedProcess("manual", now, null, identities, null, SuspendSource.Manual);
                Invoke(window, "RefreshSuspended");
                Invoke(window, "ResumeSuspendedProcess_Click",
                    new Button { DataContext = ((IEnumerable)window.SuspendedProcessList.ItemsSource).Cast<object>().First() },
                    new RoutedEventArgs());
                Assert.Contains("no longer running", window.SuspendedProcessStatus.Text);
                Assert.Empty(repo.GetSuspendedProcessNames());
            }
            finally { window.ForceClose(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void SuspendSettings_PersistTheDefaultInterval()
    {
        RunStaAsync(() =>
        {
            EnsureApplication();
            var repo = new MonitorRepository(_db); repo.EnsureSchema();
            repo.UpsertDisks([Disk()]);
            using var config = new ConfigStore(_cfg);
            var userSettings = new UserSettingsStore(_userSettings);
            var window = new MainWindow(repo, config, userSettings);
            try
            {
                Invoke(window, "LoadSuspendRules");
                Assert.Equal("30", window.TxtSuspendMinutes.Text);

                window.TxtSuspendMinutes.Text = "15";
                Invoke(window, "SaveRules_Click", window, new RoutedEventArgs());
                Assert.Equal(15, userSettings.Current.DefaultSuspendMinutes);
                Assert.Equal("15", window.TxtSuspendMinutes.Text);
            }
            finally { window.ForceClose(); }
            return Task.CompletedTask;
        });
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
