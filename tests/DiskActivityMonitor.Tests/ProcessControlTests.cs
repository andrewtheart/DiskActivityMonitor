using System.Diagnostics;
using DiskActivityMonitor.Core.Collection;
using DiskActivityMonitor.Core.Configuration;
using DiskActivityMonitor.Core.Data;
using DiskActivityMonitor.Tray;

namespace DiskActivityMonitor.Tests;

public sealed class ProcessControlTests
{
    [Fact]
    public void SuspendAndResume_UseExactExecutableIdentity()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dam_process_{Guid.NewGuid():N}");
        string targetDirectory = Path.Combine(root, "target");
        string otherDirectory = Path.Combine(root, "other");
        Directory.CreateDirectory(targetDirectory);
        Directory.CreateDirectory(otherDirectory);
        string executableName = $"dam-probe-{Guid.NewGuid():N}.exe";
        string targetPath = Path.Combine(targetDirectory, executableName);
        string otherPath = Path.Combine(otherDirectory, executableName);
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), targetPath);
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), otherPath);

        using var target = StartProbe(targetPath);
        using var other = StartProbe(otherPath);
        IReadOnlyList<ProcessControl.ProcessIdentity> suspended = [];
        try
        {
            string processName = Path.GetFileNameWithoutExtension(executableName);
            Assert.True(SpinWait.SpinUntil(
                () => ProcessControl.IsRunning(processName, targetPath),
                TimeSpan.FromSeconds(5)));

            var suspendResult = ProcessControl.Suspend(processName, targetPath);
            suspended = suspendResult.Processes;

            Assert.Equal(1, suspendResult.Matched);
            Assert.Equal(1, suspendResult.Affected);
            var identity = Assert.Single(suspended);
            Assert.Equal(target.Id, identity.ProcessId);
            Assert.Equal(Path.GetFullPath(targetPath), identity.ExecutablePath, ignoreCase: true);
            Assert.False(other.HasExited);

            var forged = identity with { CreationTimeFileTimeUtc = identity.CreationTimeFileTimeUtc + 1 };
            var forgedResult = ProcessControl.Resume([forged]);
            Assert.Equal(0, forgedResult.Affected);
            Assert.True(forgedResult.IdentityUnavailable);
            Assert.Equal(forged, Assert.Single(forgedResult.Unresolved));

            var resumeResult = ProcessControl.Resume(suspended);
            Assert.Equal(1, resumeResult.Matched);
            Assert.Equal(1, resumeResult.Affected);
            suspended = [];
        }
        finally
        {
            if (suspended.Count > 0)
                ProcessControl.Resume(suspended);
            StopProbe(target);
            StopProbe(other);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ResumeTracked_WithoutExactIdentitiesFailsClosed()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"dam_resume_{Guid.NewGuid():N}.db");
        try
        {
            var repo = new MonitorRepository(databasePath);
            repo.EnsureSchema();
            repo.AddSuspendedProcess("legacy", DateTime.UtcNow, null, []);

            var result = AutoSuspendManager.ResumeTracked(repo, "legacy");

            Assert.True(result.IdentityUnavailable);
            Assert.NotNull(repo.GetSuspendedProcessState("legacy"));
        }
        finally
        {
            foreach (string file in Directory.GetFiles(
                         Path.GetDirectoryName(databasePath)!,
                         Path.GetFileName(databasePath) + "*"))
                try { File.Delete(file); } catch { }
        }
    }

    [Fact]
    public void AutoSuspend_RequiresAnExactExecutablePath()
    {
        Assert.False(AutoSuspendManager.CanAutoSuspend(new AutoSuspendRule
        {
            ProcessName = "writer",
            Mode = SuspendMode.Auto,
        }));
        Assert.True(AutoSuspendManager.CanAutoSuspend(new AutoSuspendRule
        {
            ProcessName = "writer",
            ExecutablePath = @"C:\Apps\writer.exe",
            Mode = SuspendMode.Auto,
        }));
    }

    private static Process StartProbe(string path)
        => Process.Start(new ProcessStartInfo
        {
            FileName = path,
            Arguments = "127.0.0.1 -n 30",
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Could not start process-control probe.");

    private static void StopProbe(Process process)
    {
        if (process.HasExited)
            return;
        process.Kill(entireProcessTree: true);
        process.WaitForExit(5000);
    }
}