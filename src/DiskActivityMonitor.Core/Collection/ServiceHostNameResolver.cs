using System.Management;
using System.Runtime.Versioning;

namespace DiskActivityMonitor.Core.Collection;

/// <summary>
/// Resolves a generic <c>svchost</c> process + PID to the Windows service(s) hosted by that PID,
/// using a periodically refreshed Win32_Service snapshot. Resolution is an in-memory lookup on the
/// hot I/O-event path; WMI is never queried per event.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServiceHostNameResolver : IDisposable
{
    private sealed record ServiceEntry(string Name, string DisplayName);

    private volatile Dictionary<int, IReadOnlyList<ServiceEntry>> _servicesByPid = new();
    private readonly Timer _refreshTimer;

    /// <summary>Creates the resolver, loads the initial service/PID map, and refreshes it every five minutes.</summary>
    public ServiceHostNameResolver()
    {
        Refresh();
        _refreshTimer = new Timer(_ => Refresh(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Returns <paramref name="processName"/> unchanged unless it is svchost and the PID hosts one or
    /// more known services. A single service includes its friendly display name; multiple services
    /// use their short service names to keep the label compact.
    /// </summary>
    public string Resolve(string processName, int processId)
    {
        if (!string.Equals(processName, "svchost", StringComparison.OrdinalIgnoreCase) || processId <= 0)
            return processName;

        if (!_servicesByPid.TryGetValue(processId, out var services) || services.Count == 0)
            return processName;

        return FormatServiceHostName(
            processName,
            services.Select(s => (s.Name, s.DisplayName)).ToList());
    }

    /// <summary>Pure formatter used by <see cref="Resolve"/> and unit tests.</summary>
    public static string FormatServiceHostName(
        string processName,
        IReadOnlyList<(string Name, string DisplayName)> services)
    {
        if (services.Count == 0) return processName;

        var ordered = services
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ordered.Count == 0) return processName;

        if (ordered.Count == 1)
        {
            var service = ordered[0];
            string detail = !string.IsNullOrWhiteSpace(service.DisplayName) &&
                            !string.Equals(service.Name, service.DisplayName, StringComparison.OrdinalIgnoreCase)
                ? $"{service.Name}: {service.DisplayName}"
                : service.Name;
            return $"{processName} ({detail})";
        }

        return $"{processName} ({string.Join(", ", ordered.Select(s => s.Name))})";
    }

    private void Refresh()
    {
        try
        {
            var grouped = new Dictionary<int, List<ServiceEntry>>();
            using var searcher = new ManagementObjectSearcher(
                @"root\cimv2",
                "SELECT Name, DisplayName, ProcessId FROM Win32_Service WHERE ProcessId <> 0");
            using var results = searcher.Get();
            foreach (ManagementBaseObject service in results)
            {
                int pid = Convert.ToInt32(service["ProcessId"] ?? 0);
                string name = service["Name"]?.ToString()?.Trim() ?? "";
                string display = service["DisplayName"]?.ToString()?.Trim() ?? "";
                if (pid <= 0 || name.Length == 0) continue;
                if (!grouped.TryGetValue(pid, out var entries))
                    grouped[pid] = entries = new List<ServiceEntry>();
                entries.Add(new ServiceEntry(name, display));
            }

            _servicesByPid = grouped.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<ServiceEntry>)kv.Value);
        }
        catch
        {
            // Keep the previous snapshot. Failure to resolve service names must never affect I/O collection.
        }
    }

    public void Dispose() => _refreshTimer.Dispose();
}
