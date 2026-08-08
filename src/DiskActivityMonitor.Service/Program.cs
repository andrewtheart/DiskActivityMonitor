using DiskActivityMonitor.Core;
using DiskActivityMonitor.Core.Configuration;
using DiskActivityMonitor.Core.Data;
using DiskActivityMonitor.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

return ServiceProgramEntry.Run(args, ServiceProgramEntry.EntryPointTryAcquire);

internal static class ServiceProgramEntry
{
    internal delegate bool TryAcquireDelegate(string name, out SingleInstanceGuard? guard);
    internal static TryAcquireDelegate EntryPointTryAcquire = SingleInstanceGuard.TryAcquire;

    internal static int Run(
        string[] args,
        TryAcquireDelegate tryAcquire,
        Action? ensurePaths = null,
        Func<string[], IHost>? buildHost = null,
        Action<IHost>? runHost = null)
    {
        ensurePaths ??= Paths.EnsureCreated;
        buildHost ??= BuildHost;
        runHost ??= host => host.Run();

        if (!tryAcquire(SingleInstanceGuard.ServiceMutexName, out var instanceGuard))
            return 0;

        using (instanceGuard)
        {
            ensurePaths();
            using var host = buildHost(args);
            runHost(host);
            return 0;
        }
    }

    internal static IHost BuildHost(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // Run as a Windows Service when installed via sc.exe; otherwise behaves as a console app.
        builder.Services.AddWindowsService(ConfigureWindowsService);

        builder.Services.AddSingleton(_ => new ConfigStore());
        builder.Services.AddSingleton(_ => new MonitorRepository());
        builder.Services.AddHostedService<CollectorWorker>();

        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "h:mm:ss tt ";
        });

        return builder.Build();
    }

    internal static void ConfigureWindowsService(WindowsServiceLifetimeOptions options)
        => options.ServiceName = "DiskActivityMonitor";
}
