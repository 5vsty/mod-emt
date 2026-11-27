using FSDEV.Common.Logger;
using FSDEV.ILP.Intralogistikplattform.SystemState.Client;
using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Notifications.Notifications;
using FSDEV.MOD.Notifications.Notifications_Client;
using System.Diagnostics.CodeAnalysis;
using NotificationConstants = FSDEV.MOD.Notifications.Notifications.Constants;

namespace FSDEV.MOD.Emt.Emt_Server.Notifications;

/// <summary>
/// Writes the notifications of EMT with the client of the Notifications module. The client is created when it is first needed and again
/// after a failure, so EMT starts and works without the Notifications module (NFR-07). A notification is only created if the Notifications
/// module can show it to its recipient alone; an older module would show it to everybody (FR-47).
/// </summary>
public class IlpNotificationPublisher : INotificationPublisher
{
    private const string LOG_PREFIX = $"[{nameof(IlpNotificationPublisher)}]";
    private const string TYPE_PREFIX = "EMT_NOTIF_";
    private const string INSTRUCTIONS_KEY = "EMT_NOTIFICATION_INSTRUCTIONS";
    private const string OK_KEY = "OK";
    private const string OK_LABEL = "EMT_NOTIFICATION_OK";
    private const string OPEN_KEY = "OPEN";
    private const string OPEN_LABEL = "EMT_NOTIFICATION_OPEN";
    private const string SEVERITY_INFO = "INFO";
    private const string SEVERITY_WARNING = "WARN";
    private const string SEVERITY_ERROR = "ERR";

    private static readonly string[] CustomFieldNames = ["ErrorNumber", "ErrorCode", "CodeTitle", "CodeTitle_de", "CodeTitle_en", "LocationPath", "ActorName", "Detail", "Detail_de", "Detail_en", "Severity"];

    private readonly IClientController Ilp;
    private readonly string ApplicationName;
    private readonly Guid FacilityId;
    private readonly object ClientLock = new();
    private readonly HashSet<string> KnownTypes = [];

    private Notifications_Client? Client;

    public IlpNotificationPublisher(IClientController ilp, string applicationName, Guid facilityId)
    {
        Ilp = ilp;
        ApplicationName = applicationName;
        FacilityId = facilityId;
    }

    /// <summary>Called with the error and the notification when a recipient pressed "OK" and the notification was moved to the history.</summary>
    public Action<Guid, Guid>? Dismissed { get; set; }

    /// <summary>Called with the user and the error when a recipient pressed "Open": the page of the user opens the error.</summary>
    public Action<Guid, Guid>? OpenRequested { get; set; }

    /// <summary>
    /// Where the work of a pressed button runs. The ILP client calls the buttons on its own main thread, the one that applies the changes of the
    /// server; a change request sent from there waits for that thread and never returns, and EMT could not write to ILP any more. So the work is
    /// handed to the thread pool; tests run it inline.
    /// </summary>
    public Action<Action> Background { get; set; } = job => _ = Task.Run(job);

    public bool TryPublish(ErrorRecord error, NotificationMessage message, string actorName, [NotNullWhen(true)] out string? notificationId)
    {
        notificationId = null;

        if (!TryGetClient(out var client))
        {
            return false;
        }

        if (!client.SupportsRecipients)
        {
            Logging.Log(LogLevel.Warning, $"{LOG_PREFIX} The Notifications module does not support recipients, no notification is created");
            return false;
        }

        var typeIdentifier = GetTypeIdentifier(message.Kind, error.Severity);
        if (!TryEnsureType(client, typeIdentifier, message.Kind, error.Severity))
        {
            return false;
        }

        var customFields = new Dictionary<string, object>
        {
            ["ErrorNumber"] = error.ErrorNumber,
            ["ErrorCode"] = error.ErrorCode,
            ["CodeTitle"] = error.CodeTitle,
            ["CodeTitle_de"] = error.CodeTitle,
            ["CodeTitle_en"] = error.CodeTitle,
            ["LocationPath"] = error.LocationPath.Length == 0 ? "-" : error.LocationPath,
            ["ActorName"] = actorName,
            ["Detail"] = message.Detail,
            ["Detail_de"] = NotificationTexts.Detail(message.Kind, message.Detail, true),
            ["Detail_en"] = NotificationTexts.Detail(message.Kind, message.Detail, false),
            ["Severity"] = error.Severity.ToWire(),
        };

        if (!client.TryCreateNotification(typeIdentifier, error.Id, FacilityId, message.UserId, customFields, out var id))
        {
            ResetClient();
            return false;
        }

        notificationId = id.ToString();
        return true;
    }

    /// <summary>
    /// Gives the buttons of every notification type of EMT that already exists (created by an earlier run) their actions again. The actions only live
    /// in the memory of the application that owns them, so after a restart a click on a notification did nothing until a new notification of the same
    /// type happened to be created. Called when EMT starts; false when the Notifications module is not there yet (it is tried again).
    /// </summary>
    public bool TryRegisterExistingTypes()
    {
        if (!TryGetClient(out var client))
        {
            return false;
        }

        lock (ClientLock)
        {
            var identifiers = new List<string>();
            foreach (var severity in Enum.GetValues<Severity>())
            {
                identifiers.Add(GetTypeIdentifier(NotificationKinds.NEW_ERROR, severity));
            }

            foreach (var kind in new[] { NotificationKinds.ASSIGNED, NotificationKinds.ASSIGNEE_CHANGED, NotificationKinds.STATUS_CHANGED, NotificationKinds.NOTE_ADDED, NotificationKinds.SEVERITY_CHANGED })
            {
                identifiers.Add(GetTypeIdentifier(kind, Severity.Info));
            }

            var registered = 0;
            foreach (var identifier in identifiers.Distinct())
            {
                if (client.NotificationTypeExists(identifier))
                {
                    client.TryRegisterButtons(identifier, CreateButtons());
                    _ = KnownTypes.Add(identifier);
                    registered++;
                }
            }

            Logging.Log(LogLevel.Info, $"{LOG_PREFIX} The buttons of {registered} existing notification types are active");
            return true;
        }
    }

    public bool TryArchive(string notificationId)
    {
        if (!Guid.TryParse(notificationId, out var id) || !TryGetClient(out var client))
        {
            return false;
        }

        // a notification that is not active any more (archived, or removed by an administrator) is done: it must not stay in the list of the error
        if (!Ilp.TryGetState($"{NotificationConstants.Ilp.Paths.NOTIFICATIONS}/{notificationId}", out _))
        {
            Logging.Log(LogLevel.Debug, $"{LOG_PREFIX} Notification {notificationId} is not active any more, nothing to archive");
            return true;
        }

        if (client.TryArchiveNotification(id))
        {
            return true;
        }

        ResetClient();
        return false;
    }

    /// <summary>The type of a kind; a new error has one type for each severity, so the notification shows the icon of the severity.</summary>
    internal static string GetTypeIdentifier(string kind, Severity severity) =>
        kind == NotificationKinds.NEW_ERROR ? $"{TYPE_PREFIX}{kind}_{severity.ToString().ToUpperInvariant()}" : TYPE_PREFIX + kind;

    private bool TryGetClient([NotNullWhen(true)] out Notifications_Client? client)
    {
        lock (ClientLock)
        {
            if (Client is null)
            {
                // skipInitialCleanUp: EMT must not clean up the notifications of other modules
                if (!Notifications_Client.TryCreateNotificationClient(ApplicationName, Ilp, out var created, true))
                {
                    Logging.Log(LogLevel.Warning, $"{LOG_PREFIX} The Notifications module is not available, no notification is created");
                    client = null;
                    return false;
                }

                Client = created;
            }

            client = Client;
            return true;
        }
    }

    private void ResetClient()
    {
        lock (ClientLock)
        {
            Client = null;
            KnownTypes.Clear();
        }
    }

    private bool TryEnsureType(Notifications_Client client, string identifier, string kind, Severity severity)
    {
        lock (ClientLock)
        {
            // the type can be deleted while EMT runs (an administrator, a reset of the site): it is looked at every time, the registered buttons are remembered
            var exists = client.NotificationTypeExists(identifier);
            if (exists && KnownTypes.Contains(identifier))
            {
                return true;
            }

            var buttons = CreateButtons();

            if (!exists)
            {
                var customFields = CustomFieldNames.ToDictionary(name => name, _ => (object)string.Empty);
                if (!client.TryCreateNotificationType(identifier, TYPE_PREFIX + kind, INSTRUCTIONS_KEY, ToSeverityLevel(kind, severity), customFields, buttons))
                {
                    Logging.Log(LogLevel.Error, $"{LOG_PREFIX} The notification type {identifier} could not be created");
                    return false;
                }
            }
            else
            {
                // the type of an earlier run: the actions of its buttons are only known to the application that is running now
                client.TryRegisterButtons(identifier, buttons);
            }

            _ = KnownTypes.Add(identifier);
            return true;
        }
    }

    private List<ActionButton> CreateButtons() => [new(OPEN_KEY, OPEN_LABEL, HandleOpen), new(OK_KEY, OK_LABEL, OnOkPressed)];

    /// <summary>
    /// "Open" (also what a click on the row presses, config ActiveNotifications.RowClickButton) asks the page of the recipient to open the error and moves the
    /// notification to the history: the notification has done its job. Nobody can open an error with the notification of somebody else.
    /// </summary>
    public void HandleOpen(Guid userId, Notification notification)
    {
        if (notification.RecipientUserId != userId)
        {
            Logging.Log(LogLevel.Warning, $"{LOG_PREFIX} User {userId} pressed Open on a notification of another user, ignored");
            return;
        }

        Background(() =>
        {
            try
            {
                OpenRequested?.Invoke(userId, notification.ReferencedId);

                if (TryArchive(notification.Id.ToString()))
                {
                    Dismissed?.Invoke(notification.ReferencedId, notification.Id);
                }
            }
            catch (Exception exception)
            {
                Logging.Log(LogLevel.Error, exception, $"{LOG_PREFIX} Opening the error of notification {notification.Id} failed");
            }
        });
    }

    /// <summary>"OK" moves the notification of the user to the history; the error is told so it forgets the notification.</summary>
    private void OnOkPressed(Guid userId, Notification notification)
    {
        if (notification.RecipientUserId != userId)
        {
            Logging.Log(LogLevel.Warning, $"{LOG_PREFIX} User {userId} pressed OK on a notification of another user, ignored");
            return;
        }

        Background(() =>
        {
            try
            {
                if (TryArchive(notification.Id.ToString()))
                {
                    Dismissed?.Invoke(notification.ReferencedId, notification.Id);
                }
            }
            catch (Exception exception)
            {
                Logging.Log(LogLevel.Error, exception, $"{LOG_PREFIX} Archiving notification {notification.Id} failed");
            }
        });
    }

    private static string ToSeverityLevel(string kind, Severity severity)
    {
        if (kind != NotificationKinds.NEW_ERROR)
        {
            return SEVERITY_INFO;
        }

        return severity switch
        {
            Severity.Info => SEVERITY_INFO,
            Severity.Warning => SEVERITY_WARNING,
            _ => SEVERITY_ERROR,
        };
    }
}
