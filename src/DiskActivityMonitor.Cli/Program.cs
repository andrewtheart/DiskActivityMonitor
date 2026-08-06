// Disk Activity Monitor command-line interface (dam).
// Thin client over the shared SQLite database and config that the collector service writes
// and the tray app reads. Provides status, stats, trends, alerts, snoozes and config control.
return CliProgramEntry.Run(args, CliProgramEntry.EntryPointTryAcquire);

internal static class CliProgramEntry
{
	internal delegate bool TryAcquireDelegate(string name, out DiskActivityMonitor.Core.SingleInstanceGuard? guard);
	internal static TryAcquireDelegate EntryPointTryAcquire = DiskActivityMonitor.Core.SingleInstanceGuard.TryAcquire;

	internal static int Run(
		string[] args,
		TryAcquireDelegate tryAcquire,
		Func<string[], int>? runCli = null,
		Action<string>? writeErrorLine = null)
	{
		runCli ??= DiskActivityMonitor.Cli.CliRunner.Run;
		writeErrorLine ??= Console.Error.WriteLine;

		if (!tryAcquire(
				DiskActivityMonitor.Core.SingleInstanceGuard.CliMutexName,
				out var instanceGuard))
		{
			writeErrorLine("Another Disk Activity Monitor CLI command is already running.");
			return 2;
		}

		using (instanceGuard)
			return runCli(args);
	}
}
