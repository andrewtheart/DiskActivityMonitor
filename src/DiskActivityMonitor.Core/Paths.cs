using System.IO;

namespace DiskActivityMonitor.Core;

/// <summary>
/// Well-known file-system locations shared by the collector service and the tray app.
/// Everything lives under %ProgramData%\DiskActivityMonitor so both a service running as
/// LocalSystem and an interactive tray app can reach the same data.
/// </summary>
public static class Paths
{
    public static string BaseDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "DiskActivityMonitor");

    public static string DatabasePath { get; } = Path.Combine(BaseDirectory, "diskactivity.db");

    public static string ConfigPath { get; } = Path.Combine(BaseDirectory, "config.json");

    public static string LogDirectory { get; } = Path.Combine(BaseDirectory, "logs");

    /// <summary>Ensures the base directories exist. Safe to call repeatedly.</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(BaseDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
