using FSDEV.Common.Logger;
using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Emt.Emt_Server.Errors;
using System.Threading.Channels;

namespace FSDEV.MOD.Emt.Emt_Server.Notifications;

/// <summary>
/// Creates the notifications of every change of an error, one for each person concerned (FR-24, FR-27, BR-44…BR-46), and moves them to the
/// history: all earlier notifications of an error when it is closed, rejected or cancelled, a single one when its recipient presses "OK" (FR-25,
/// BR-47). Re-opening a resolved error creates no new notification of the old kind (BR-17). The work runs one job at a time on its own thread, in
/// the order of the changes, so a slow or missing Notifications module never delays a report and a failure never undoes a change (NFR-07).
/// </summary>
public class NotificationObserver : IErrorObserver
{
    private const string LOG_PREFIX = $"[{nameof(NotificationObserver)}]";
    private const char SEPARATOR = ';';

    private readonly INotificationPublisher Publisher;
    private readonly INotificationStateStore Store;
    private readonly Func<Guid, string, bool> HasPermission;
    private readonly Func<IReadOnlyCollection<Guid>> GetManagers;
    private readonly Channel<Action>? Queue;

    /// <param name="runInline">true = run each job at once on the calling thread (tests).</param>
    public NotificationObserver(INotificationPublisher publisher, INotificationStateStore store, Func<Guid, string, bool> hasPermission, Func<IReadOnlyCollection<Guid>> getManagers, CancellationToken stoppingToken, bool runInline = false)
    {
        Publisher = publisher;
        Store = store;
        HasPermission = hasPermission;
        GetManagers = getManagers;

        if (!runInline)
        {
            Queue = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
            _ = Task.Run(() => RunAsync(stoppingToken), CancellationToken.None);
        }
    }

    public void OnErrorChanged(ErrorRecord error, ErrorChangeType changeType)
    {
        try
        {
            var snapshot = error.Clone();
            var messages = NotificationRecipients.Resolve(snapshot, changeType, HasPermission, GetManagers);
            var archiveEarlier = changeType == ErrorChangeType.Updated && IsFinished(snapshot.Status) && snapshot.Events.Count > 0 && snapshot.Events[^1].EventType == ErrorEventTypes.STATUS_CHANGED;
            if (messages.Count == 0 && !archiveEarlier)
            {
                return;
            }

            var actorName = changeType == ErrorChangeType.Created ? snapshot.ReporterName : snapshot.Events[^1].UserName;
            Enqueue(() =>
            {
                // the notifications of this change are created after the old ones are moved: they stay until their recipient presses "OK"
                if (archiveEarlier)
                {
                    ArchiveAll(snapshot.Id);
                }

                Publish(snapshot, actorName, messages);
            });
        }
        catch (Exception exception)
        {
            Logging.Log(LogLevel.Error, exception, $"{LOG_PREFIX} The notifications of error {error.ErrorNumber} could not be created");
        }
    }

    /// <summary>The recipient pressed "OK": the notification is already in the history, the error forgets it.</summary>
    public void NotificationDismissed(Guid errorId, Guid notificationId) => Enqueue(() => Forget(errorId, notificationId.ToString()));

    private static bool IsFinished(ErrorStatus status) => status is ErrorStatus.Closed or ErrorStatus.Rejected or ErrorStatus.Cancelled;

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

    private static List<string> Parse(string notificationIds) => notificationIds.Split(SEPARATOR, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private void Publish(ErrorRecord snapshot, string actorName, List<NotificationMessage> messages)
    {
        if (messages.Count == 0)
        {
            return;
        }

        var created = new List<string>();
        var failed = 0;
        foreach (var message in messages)
        {
            if (Publisher.TryPublish(snapshot, message, actorName, out var notificationId))
            {
                created.Add(notificationId);
            }
            else
            {
                failed++;
            }
        }

        if (failed > 0)
        {
            Logging.Log(LogLevel.Warning, $"{LOG_PREFIX} {failed} of {messages.Count} notifications of error {snapshot.ErrorNumber} were not created");
        }

        if (!Store.TryGet(snapshot.Id, out var current))
        {
            return;
        }

        var all = Parse(current.NotificationId);
        all.AddRange(created);
        var state = all.Count > 0 ? ErrorRecord.NOTIFICATION_ACTIVE : failed > 0 ? ErrorRecord.NOTIFICATION_FAILED : current.NotificationState;
        _ = Store.TrySetNotification(snapshot.Id, string.Join(SEPARATOR, all), state);
    }

    private void ArchiveAll(Guid errorId)
    {
        if (!Store.TryGet(errorId, out var error))
        {
            return;
        }

        var ids = Parse(error.NotificationId);
        if (ids.Count == 0)
        {
            return;
        }

        // one that cannot be moved now stays in the list and is tried again with the next change
        var remaining = ids.Where(id => !Publisher.TryArchive(id)).ToList();
        if (remaining.Count > 0)
        {
            Logging.Log(LogLevel.Warning, $"{LOG_PREFIX} {remaining.Count} notifications of error {error.ErrorNumber} were not archived, they stay active");
        }

        _ = Store.TrySetNotification(errorId, string.Join(SEPARATOR, remaining), remaining.Count == 0 ? ErrorRecord.NOTIFICATION_ARCHIVED : ErrorRecord.NOTIFICATION_ACTIVE);
    }

    private void Forget(Guid errorId, string notificationId)
    {
        if (!Store.TryGet(errorId, out var error))
        {
            return;
        }

        var ids = Parse(error.NotificationId);
        if (!ids.Remove(notificationId))
        {
            return;
        }

        _ = Store.TrySetNotification(errorId, string.Join(SEPARATOR, ids), ids.Count == 0 ? ErrorRecord.NOTIFICATION_ARCHIVED : ErrorRecord.NOTIFICATION_ACTIVE);
    }
}
