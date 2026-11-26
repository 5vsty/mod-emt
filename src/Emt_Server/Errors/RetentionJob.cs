using FSDEV.Common.Logger;
using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Emt.Emt_Server.Catalog;
using FSDEV.MOD.Emt.Emt_Server.Storage;

namespace FSDEV.MOD.Emt.Emt_Server.Errors;

/// <summary>
/// Removes errors that reached a final status longer ago than the retention time (FR-32, BR-21, D-11: default 365 days).
/// Errors that are not in a final status are never removed, however old they are - this includes RESOLVED (BR-21, AC-83).
/// </summary>
public class RetentionJob
{
    private const string LOG_PREFIX = $"[{nameof(RetentionJob)}]";
    private const long MS_PER_DAY = 24L * 60 * 60 * 1000;

    private readonly IErrorStore Store;
    private readonly ErrorIndex Index;
    private readonly SettingsStore Settings;
    private readonly IClock Clock;
    private readonly Action<ErrorRecord> OnRemoved;

    public long LastRunAt { get; private set; }

    /// <param name="onRemoved">Called for every removed error (removes the table row).</param>
    public RetentionJob(IErrorStore store, ErrorIndex index, SettingsStore settings, IClock clock, Action<ErrorRecord> onRemoved)
    {
        Store = store;
        Index = index;
        Settings = settings;
        Clock = clock;
        OnRemoved = onRemoved;
    }

    /// <summary>Returns the number of removed errors.</summary>
    public int RunOnce()
    {
        var now = Clock.UtcNowMs;
        var cutoff = now - (Settings.Current.RetentionDays * MS_PER_DAY);
        var removed = 0;

        foreach (var error in Index.GetAll())
        {
            if (!ErrorLifecycle.IsFinal(error.Status) || error.FinalizedAt <= 0 || error.FinalizedAt >= cutoff)
            {
                continue;
            }

            // an error that could not be deleted on ILP stays and is tried again in the next run
            if (!Store.TryDelete(error))
            {
                Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Could not remove error {error.ErrorNumber}, will try again in the next run");
                continue;
            }

            _ = Index.Remove(error.Id);
            OnRemoved(error);
            removed++;
        }

        LastRunAt = now;
        Logging.Log(LogLevel.Info, $"{LOG_PREFIX} Removed {removed} errors that were final for more than {Settings.Current.RetentionDays} days");
        return removed;
    }

    public async Task RunAsync(TimeSpan interval, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _ = RunOnce();
            }
            catch (Exception exception)
            {
                // the job runs in the background, a failed run must not stop the following runs
                Logging.Log(LogLevel.Error, exception, $"{LOG_PREFIX} Run failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // the module is stopping
                return;
            }
        }
    }
}
