namespace DiskActivityMonitor.Core;

/// <summary>Owns a named mutex for one independently launchable application component.</summary>
public sealed class SingleInstanceGuard : IDisposable
{
    public const string ServiceMutexName = "Global\\DiskActivityMonitor.Service.SingleInstance";
    public const string TrayMutexName = "Local\\DiskActivityMonitor.Tray.SingleInstance";
    public const string ToastActivationMutexName = "Local\\DiskActivityMonitor.ToastActivation.SingleInstance";
    public const string CliMutexName = "Local\\DiskActivityMonitor.Cli.SingleInstance";

    private readonly Mutex _mutex;
    private bool _ownsMutex;

    private SingleInstanceGuard(Mutex mutex, bool ownsMutex)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
    }

    public static bool TryAcquire(string name, out SingleInstanceGuard? guard)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var mutex = new Mutex(initiallyOwned: false, name);
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            mutex.Dispose();
            guard = null;
            return false;
        }

        guard = new SingleInstanceGuard(mutex, ownsMutex: true);
        return true;
    }

    public void Dispose()
    {
        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
            _ownsMutex = false;
        }
        _mutex.Dispose();
    }
}