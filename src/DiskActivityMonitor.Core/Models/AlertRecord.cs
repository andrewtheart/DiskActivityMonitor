namespace DiskActivityMonitor.Core.Models;

public enum AlertSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2,
}

/// <summary>A persisted alert raised by the <see cref="Alerts.AlertEngine"/>.</summary>
public sealed class AlertRecord
{
    public long Id { get; set; }

    public required DateTime TimestampUtc { get; init; }

    public AlertSeverity Severity { get; init; }

    /// <summary>Stable key identifying the rule + scope, used to throttle duplicates.</summary>
    public required string RuleKey { get; init; }

    public required string Title { get; init; }

    public required string Message { get; init; }

    /// <summary>Observed numeric value that tripped the rule (for example bytes or an event count).</summary>
    public double Value { get; init; }

    /// <summary>Configured numeric threshold the value exceeded.</summary>
    public double Threshold { get; init; }

    /// <summary>True when the alert was dismissed from the main Alert center.</summary>
    public bool Acknowledged { get; set; }
}
