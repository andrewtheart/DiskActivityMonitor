using DiskActivityMonitor.Core.Configuration;
using DiskActivityMonitor.Core.Data;
using DiskActivityMonitor.Core.Models;
using DiskActivityMonitor.Service;
using Microsoft.Extensions.Logging;

namespace DiskActivityMonitor.Tests;

public sealed class CollectorWorkerControllerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"dam_worker_{Guid.NewGuid():N}.db");
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"dam_worker_cfg_{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string path in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_dbPath) + "*"))
            try { File.Delete(path); } catch { }
        try { File.Delete(_configPath); } catch { }
        try { File.Delete(_configPath + ".tmp"); } catch { }
    }

    [Fact]
    public void MonitorControllerErrors_Disabled_DoesNotRead()
    {
        var reader = new FakeReader([]);
        using var harness = Create(reader);
        harness.Worker.MonitorControllerErrors(new AppConfig { EnableControllerErrorAlerts = false }, DateTime.UtcNow);
        Assert.Equal(0, reader.Calls);
    }

    [Theory]
    [InlineData(-50, 1)]
    [InlineData(999, 365)]
    public void MonitorControllerErrors_ClampsWindowAndPersistsAlert(int configuredDays, int expectedDays)
    {
        var now = new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
        var reader = new FakeReader([new DiskControllerErrorSummary
        {
            DiskId = "2", DevicePath = @"\Device\Harddisk2\DR2", Count = 4,
            FirstUtc = now.AddDays(-2), LatestUtc = now,
        }]);
        using var harness = Create(reader);
        var cfg = new AppConfig
        {
            ControllerErrorWindowDays = configuredDays,
            ControllerErrorWarnCount = 3,
            ControllerErrorCriticalCount = 10,
        };

        harness.Worker.MonitorControllerErrors(cfg, now);

        Assert.Equal(now.AddDays(-expectedDays), reader.LastSinceUtc);
        Assert.Contains(harness.Repo.GetRecentAlerts(10), a => a.RuleKey == "disk-controller:2");
        Assert.Contains(harness.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Storage controller errors"));
    }

    [Fact]
    public void MonitorControllerErrors_BelowThreshold_DoesNotPersist()
    {
        var reader = new FakeReader([new DiskControllerErrorSummary
        {
            DiskId = "2", DevicePath = @"\Device\Harddisk2\DR2", Count = 1,
            FirstUtc = DateTime.UtcNow, LatestUtc = DateTime.UtcNow,
        }]);
        using var harness = Create(reader);
        harness.Worker.MonitorControllerErrors(new AppConfig { ControllerErrorWarnCount = 3 }, DateTime.UtcNow);
        Assert.Empty(harness.Repo.GetRecentAlerts(10));
    }

    [Fact]
    public void MonitorControllerErrors_ReaderFailure_IsLoggedAndContained()
    {
        var reader = new FakeReader(new InvalidOperationException("event log unavailable"));
        using var harness = Create(reader);
        harness.Worker.MonitorControllerErrors(new AppConfig(), DateTime.UtcNow);
        Assert.Contains(harness.Logger.Entries, e => e.Level == LogLevel.Warning && e.Exception is InvalidOperationException);
    }

    [Fact]
    public void RunStartupMonitoring_RunsImmediateControllerCheck()
    {
        var now = DateTime.UtcNow;
        var reader = new FakeReader([new DiskControllerErrorSummary
        {
            DiskId = "3", DevicePath = @"\Device\Harddisk3\DR3", Count = 12,
            FirstUtc = now.AddDays(-1), LatestUtc = now,
        }]);
        using var harness = Create(reader);

        harness.Worker.RunStartupMonitoring(now);

        Assert.True(reader.Calls > 0);
    }

    [Fact]
    public async Task ExecuteAsync_UsesImmediateMonitoringAndStopsCleanly()
    {
        var reader = new FakeReader([]);
        using var harness = Create(reader);

        await harness.Worker.StartAsync(CancellationToken.None);
        await reader.Called.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await harness.Worker.StopAsync(CancellationToken.None);

        Assert.True(reader.Calls > 0);
    }

    [Fact]
    public void PublicConstructorAndPeriodicCoordinator_AreUsable()
    {
        using var harness = Create(new FakeReader([]));
        using var publicConfig = new ConfigStore(_configPath + ".public");
        var publicWorker = new CollectorWorker(harness.Logger, publicConfig, harness.Repo);
        Assert.NotNull(publicWorker);
        harness.Worker.RunPeriodicTasks(new AppConfig { EnableControllerErrorAlerts = false }, DateTime.UtcNow);
        try { File.Delete(_configPath + ".public"); } catch { }
    }

    [Fact]
    public void InternalConstructor_NullEtwFactory_UsesFallback()
    {
        var reader = new FakeReader([]);
        using var harness = Create(reader);
        using var config = new ConfigStore(_configPath + ".null");
        var worker = new CollectorWorker(harness.Logger, config, harness.Repo, reader, null);
        Assert.NotNull(worker);
        try { File.Delete(_configPath + ".null"); } catch { }
    }

    private Harness Create(FakeReader reader)
    {
        var repo = new MonitorRepository(_dbPath);
        repo.EnsureSchema();
        var config = new ConfigStore(_configPath);
        var logger = new ListLogger<CollectorWorker>();
        return new Harness(new CollectorWorker(logger, config, repo, reader, _ => null), repo, config, logger);
    }

    private sealed class FakeReader : IDiskControllerErrorReader
    {
        private readonly IReadOnlyList<DiskControllerErrorSummary>? _result;
        private readonly Exception? _error;
        public int Calls { get; private set; }
        public DateTime LastSinceUtc { get; private set; }
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeReader(IReadOnlyList<DiskControllerErrorSummary> result) => _result = result;
        public FakeReader(Exception error) => _error = error;

        public IReadOnlyList<DiskControllerErrorSummary> ReadSince(DateTime sinceUtc)
        {
            Calls++;
            LastSinceUtc = sinceUtc;
            Called.TrySetResult();
            if (_error is not null) throw _error;
            return _result!;
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        private sealed class NullScope : IDisposable { public static NullScope Instance { get; } = new(); public void Dispose() { } }
    }

    private sealed class Harness(CollectorWorker worker, MonitorRepository repo, ConfigStore config, ListLogger<CollectorWorker> logger) : IDisposable
    {
        public CollectorWorker Worker { get; } = worker;
        public MonitorRepository Repo { get; } = repo;
        public ListLogger<CollectorWorker> Logger { get; } = logger;
        public void Dispose() => config.Dispose();
    }
}
