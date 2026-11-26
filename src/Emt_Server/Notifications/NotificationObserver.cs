using FSDEV.Common.Logger;
using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Emt.Emt_Server.Errors;
using System.Threading.Channels;

namespace FSDEV.MOD.Emt.Emt_Server.Notifications;

/// <summary>
/// Creates one notification for every new error (FR-24) and moves it to the history when the error is resolved or finished (FR-25).
/// Re-opening a resolved error does not create a new notification (BR-17). The work runs one job at a time on its own thread, in the order
/// of the changes, so a slow or missing Notifications module never delays a report and a failure never undoes a change (NFR-07).
/// </summary>
public class NotificationObserver : IErrorObserver
{
    private const string LOG_PREFIX = $"[{nameof(NotificationObserver)}]";

    private readonly INotificationPublisher Publisher;
    private readonly INotificationStateStore Store;
    private readonly Channel<Action>? Queue;

    /// <param name="runInline">true = run each job at once on the calling thread (tests).</param>
    public NotificationObserver(INotificationPublisher publisher, INotificationStateStore store, CancellationToken stoppingToken, bool runInline = false)
    {
        Publisher = publisher;
        Store = store;

        if (!runInline)
        {
            Queue = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
            _ = Task.Run(() => RunAsync(stoppingToken), CancellationToken.None);
        }
    }

    public void OnErrorChanged(ErrorRecord error, ErrorChangeType changeType)
    {
        var errorId = error.Id;
        if (changeType == ErrorChangeType.Created)
        {
            Enqueue(() => Publish(errorId));
        }
        else if (changeType == ErrorChangeType.Updated && IsReleased(error.Status))
        {
            Enqueue(() => Archive(errorId));
        }
    }

    private static bool IsReleased(ErrorStatus status) => status is ErrorStatus.Resolved or ErrorStatus.Closed or ErrorStatus.Rejected or ErrorStatus.Cancelled;

    private void Enqueue(Action job)
    {
        if (Queue is null)
        {
            RunJob(job);
            return;
        }

        _ = Queue.Writer.TryWrite(job);
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in Queue!.Reader.ReadAllAsync(stoppingToken))
            {
                RunJob(job);
            }
        }
        catch (OperationCanceledException)
        {
            Logging.Log(LogLevel.Debug, $"{LOG_PREFIX} Stopped");
        }
    }

    private static void RunJob(Action job)
    {
        try
        {
            job();
        }
        catch (Exception exception)
        {
            Logging.Log(LogLevel.Error, exception, $"{LOG_PREFIX} A notification job failed");
        }
    }

    private void Publish(Guid errorId)
    {
        if (!Store.TryGet(errorId, out var error) || !string.Equals(error.NotificationState, ErrorRecord.NOTIFICATION_NONE, StringComparison.Ordinal))
        {
            return;
        }

        if (Publisher.TryPublish(error, out var notificationId))
        {
            _ = Store.TrySetNotification(errorId, notificationId, ErrorRecord.NOTIFICATION_ACTIVE);
            return;
        }

        Logging.Log(LogLevel.Warning, $"{LOG_PREFIX} The notification of error {error.ErrorNumber} was not created");
        _ = Store.TrySetNotification(errorId, string.Empty, ErrorRecord.NOTIFICATION_FAILED);
    }

    private void Archive(Guid errorId)
    {
        // read again: the error can have been re-opened or its notification can still be on its way when this job runs
        if (!Store.TryGet(errorId, out var error) || !IsReleased(error.Status) || !string.Equals(error.NotificationState, ErrorRecord.NOTIFICATION_ACTIVE, StringComparison.Ordinal))
        {
            return;
        }

        if (Publisher.TryArchive(error.NotificationId))
        {
            _ = Store.TrySetNotification(errorId, error.NotificationId, ErrorRecord.NOTIFICATION_ARCHIVED);
            return;
        }

        Logging.Log(LogLevel.Warning, $"{LOG_PREFIX} The notification {error.NotificationId} of error {error.ErrorNumber} was not archived, it stays active");
    }
}