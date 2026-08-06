using DiskActivityMonitor.Core;

namespace DiskActivityMonitor.Tests;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void ComponentMutexNames_AreDistinctAndExplicitlyScoped()
    {
        string[] names =
        [
            SingleInstanceGuard.ServiceMutexName,
            SingleInstanceGuard.TrayMutexName,
            SingleInstanceGuard.ToastActivationMutexName,
            SingleInstanceGuard.CliMutexName,
        ];

        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.StartsWith("Global\\", SingleInstanceGuard.ServiceMutexName);
        Assert.All(names.Skip(1), name => Assert.StartsWith("Local\\", name));
    }

    [Fact]
    public void TryAcquire_AllowsOneOwnerAndThenAllowsReacquisitionAfterDispose()
    {
        string name = "Local\\DiskActivityMonitor.Tests." + Guid.NewGuid().ToString("N");

        Assert.True(SingleInstanceGuard.TryAcquire(name, out var first));
        SingleInstanceGuard? duplicate = null;
        bool duplicateAcquired = true;
        var duplicateThread = new Thread(() =>
            duplicateAcquired = SingleInstanceGuard.TryAcquire(name, out duplicate));
        duplicateThread.Start();
        duplicateThread.Join();

        Assert.False(duplicateAcquired);
        Assert.Null(duplicate);

        first!.Dispose();

        Assert.True(SingleInstanceGuard.TryAcquire(name, out var next));
        next!.Dispose();
    }

    [Fact]
    public void TryAcquire_SucceedsWhenMutexWasAbandoned()
    {
        string name = "Local\\DiskActivityMonitor.Tests.Abandoned." + Guid.NewGuid().ToString("N");

        var ownerReady = new ManualResetEventSlim(false);
        var releaseOwner = new ManualResetEventSlim(false);
        var owner = new Thread(() =>
        {
            var held = new Mutex(initiallyOwned: true, name);
            ownerReady.Set();
            releaseOwner.Wait();
            // Intentionally exit thread without releasing to abandon the mutex.
            GC.KeepAlive(held);
        });
        owner.Start();
        ownerReady.Wait();
        releaseOwner.Set();
        owner.Join();

        Assert.True(SingleInstanceGuard.TryAcquire(name, out var guard));
        guard!.Dispose();
    }
}