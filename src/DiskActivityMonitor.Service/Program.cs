using DiskActivityMonitor.Core;
using DiskActivityMonitor.Core.Configuration;
using DiskActivityMonitor.Core.Data;
using DiskActivityMonitor.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

Paths.EnsureCreated();

var builder = Host.CreateApplicationBuilder(args);

// Run as a Windows Service when installed via sc.exe; otherwise behaves as a console app.
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "DiskActivityMonitor";
});

builder.Services.AddSingleton(_ => new ConfigStore());
builder.Services.AddSingleton(_ => new MonitorRepository());
builder.Services.AddHostedService<CollectorWorker>();

builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

var host = builder.Build();
host.Run();
