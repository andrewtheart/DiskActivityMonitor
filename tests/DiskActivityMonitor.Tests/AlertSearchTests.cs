using DiskActivityMonitor.Tray;

namespace DiskActivityMonitor.Tests;

public sealed class AlertSearchTests
{
    [Theory]
    [InlineData("Disk controller warning", "Cable may be loose", "controller", true)]
    [InlineData("Disk controller warning", "Cable may be loose", "CABLE", true)]
    [InlineData("Disk controller warning", "Cable may be loose", "temperature", false)]
    [InlineData("Disk controller warning", "Cable may be loose", "   ", true)]
    public void AlertMatchesSearch_FiltersTitleAndMessageCaseInsensitively(
        string title,
        string message,
        string query,
        bool expected)
        => Assert.Equal(expected, MainWindow.AlertMatchesSearch(title, message, query));
}