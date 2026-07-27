using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DiskActivityMonitor.Core.Ai;

/// <summary>
/// Detects local accelerators (discrete GPU, NPU) and their memory by walking the Windows device
/// class registry keys. Adapted from the approach used in Yagu: no WMI/DXGI, no NuGet dependency.
/// Used to pick the right Foundry Local model variant (cuda/gpu vs npu vs cpu) and to decide whether
/// a GPU is actually usable for inference (integrated GPUs are excluded).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardwareCapabilityDetector
{
    // Display adapters (GPUs — discrete and integrated).
    private const string DisplayClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    // Compute Accelerators / "Neural processors" (NPUs).
    private const string ComputeAcceleratorClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{f01a9d53-3ff6-48d2-9f97-c8a7004be10c}";

    /// <summary>Integrated GPUs below this dedicated VRAM crash the DirectML/WebGPU path; require a real discrete GPU.</summary>
    public const long MinDedicatedVramBytesForGpu = 1024L * 1024L * 1024L; // 1 GB

    private static readonly string[] SoftwareAdapterMarkers =
    [
        "Microsoft Basic Render Driver", "Microsoft Basic Display Adapter",
        "Microsoft Remote Display Adapter", "Microsoft Hyper-V Video", "Remote Desktop",
        "Standard VGA Graphics Adapter", "VMware SVGA", "VirtualBox Graphics Adapter",
        "Red Hat QXL", "Parsec Virtual Display Adapter", "Citrix Indirect Display Adapter",
    ];

    private readonly record struct DeviceClassEntry(string DriverDesc, string MatchingDeviceId, long DedicatedMemoryBytes);

    /// <summary>A snapshot of the machine's inference-relevant hardware.</summary>
    /// <param name="HasGpu">A hardware GPU (discrete or integrated) is present.</param>
    /// <param name="HasNpu">A neural processing unit is present.</param>
    /// <param name="MaxDedicatedVramBytes">Largest dedicated VRAM across GPUs.</param>
    public readonly record struct HardwareCapabilities(bool HasGpu, bool HasNpu, long MaxDedicatedVramBytes)
    {
        /// <summary>True when a discrete GPU with enough dedicated VRAM is available for inference.</summary>
        public bool CanUseGpu => HasGpu && MaxDedicatedVramBytes >= MinDedicatedVramBytesForGpu;

        /// <summary>Preferred Foundry Local variant device fragment order for this machine.</summary>
        public IReadOnlyList<string> PreferredDeviceFragments =>
            CanUseGpu ? new[] { "gpu", "npu", "cpu" }
            : HasNpu ? new[] { "npu", "cpu" }
            : new[] { "cpu" };
    }

    /// <summary>Enumerates hardware once and returns a capability snapshot.</summary>
    public HardwareCapabilities Detect()
    {
        bool gpu = false, npu = false;
        long maxVram = 0;
        try
        {
            foreach (var e in ReadClass(DisplayClassKey))
            {
                if (!IsHardwareAccelerator(e.DriverDesc, e.MatchingDeviceId)) continue;
                gpu = true;
                if (e.DedicatedMemoryBytes > maxVram) maxVram = e.DedicatedMemoryBytes;
            }
        }
        catch { /* registry unavailable -> assume no GPU */ }

        try
        {
            npu = ReadClass(ComputeAcceleratorClassKey).Any(e => IsHardwareAccelerator(e.DriverDesc, e.MatchingDeviceId));
        }
        catch { /* ignore */ }

        return new HardwareCapabilities(gpu, npu, maxVram);
    }

    /// <summary>Pure classifier (testable): true when a device sits on a physical bus and is not a virtual/software adapter.</summary>
    public static bool IsHardwareAccelerator(string driverDesc, string matchingDeviceId)
    {
        if (string.IsNullOrWhiteSpace(matchingDeviceId)) return false;
        string id = matchingDeviceId.Trim();
        bool onPhysicalBus =
            id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase);
        if (!onPhysicalBus) return false;
        foreach (string marker in SoftwareAdapterMarkers)
            if (driverDesc.Contains(marker, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static IEnumerable<DeviceClassEntry> ReadClass(string classKeyPath)
    {
        var entries = new List<DeviceClassEntry>();
        using RegistryKey? classKey = Registry.LocalMachine.OpenSubKey(classKeyPath);
        if (classKey is null) return entries;
        foreach (string subName in classKey.GetSubKeyNames())
        {
            if (subName.Length != 4 || !subName.All(char.IsDigit)) continue; // "0000", "0001"...
            using RegistryKey? instance = classKey.OpenSubKey(subName);
            if (instance is null) continue;
            string driverDesc = instance.GetValue("DriverDesc") as string ?? string.Empty;
            string matchingDeviceId = instance.GetValue("MatchingDeviceId") as string ?? string.Empty;
            entries.Add(new DeviceClassEntry(driverDesc, matchingDeviceId, ReadDedicatedMemoryBytes(instance)));
        }
        return entries;
    }

    private static long ReadDedicatedMemoryBytes(RegistryKey instance)
    {
        object? value = instance.GetValue("HardwareInformation.qwMemorySize");
        return value switch
        {
            long l => l,
            int i => i,
            byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
            byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
            _ => 0,
        };
    }
}
