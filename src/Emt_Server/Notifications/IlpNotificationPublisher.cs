using FSDEV.Common.Logger;
using FSDEV.ILP.Intralogistikplattform.SystemState.Client;
using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Notifications.Notifications_Client;
using System.Diagnostics.CodeAnalysis;

namespace FSDEV.MOD.Emt.Emt_Server.Notifications;

/// <summary>
/// Writes the notifications of EMT with the client of the Notifications module. The client is created when it is first needed and again
/// after a failure, so EMT starts and works without the Notifications module (NFR-07).
/// </summary>
public class IlpNotificationPublisher : INotificationPublisher
{
    private const string LOG_PREFIX = $"[{nameof(IlpNotificationPublisher)}]";
    private const string TYPE_PREFIX = "EMT_ERROR_";
    private const string MESSAGE_KEY = "EMT_NOTIFICATION_NEW_ERROR";
    private const string INSTRUCTIONS_KEY = "EMT_NOTIFICATION_INSTRUCTIONS";
    private const string SEVERITY_INFO = "INFO";
    private const string SEVERITY_WARNING = "WARN";
    private const string SEVERITY_ERROR = "ERR";

    private static readonly string[] CustomFieldNames = ["ErrorNumber", "ErrorCode", "CodeTitle", "CodeTitle_de", "CodeTitle_en", "LocationPath", "ReporterName", "Severity"];

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

    public bool TryPublish(ErrorRecord error, [NotNullWhen(true)] out string? notificationId)
    {
        notificationId = null;

        if (!TryGetClient(out var client))
        {
            return false;
        }

        var typeIdentifier = TYPE_PREFIX + error.Severity.ToString().ToUpperInvariant();
        if (!TryEnsureType(client, typeIdentifier, error.Severity))
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
            ["ReporterName"] = error.ReporterName,
            ["Severity"] = error.Severity.ToString(),
        };

        if (!client.TryCreateNotification(typeIdentifier, error.Id, FacilityId, customFields, out var id))
        {
            ResetClient();
            return false;
        }

        notificationId = id.ToString();
        return true;
    }

    public bool TryArchive(string notificationId)
    {
        if (!Guid.TryParse(notificationId, out var id) || !TryGetClient(out var client))
        {
            return false;
        }

        if (client.TryArchiveNotification(id))
        {
            return true;
        }

        ResetClient();
        return false;
    }

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

    private bool TryEnsureType(Notifications_Client client, string identifier, Severity severity)
    {
        lock (ClientLock)
        {
            if (KnownTypes.Contains(identifier))
            {
                return true;
            }

            if (!client.NotificationTypeExists(identifier))
            {
                var customFields = CustomFieldNames.ToDictionary(name => name, _ => (object)string.Empty);
                if (!client.TryCreateNotificationType(identifier, MESSAGE_KEY, INSTRUCTIONS_KEY, ToSeverityLevel(severity), customFields, []))
                {
                    Logging.Log(LogLevel.Error, $"{LOG_PREFIX} The notification type {identifier} could not be created");
                    return false;
                }
            }

            _ = KnownTypes.Add(identifier);
            return true;
        }
    }

    private static string ToSeverityLevel(Severity severity) => severity switch
    {
        Severity.Info => SEVERITY_INFO,
        Severity.Warning => SEVERITY_WARNING,
        _ => SEVERITY_ERROR,
    };
}