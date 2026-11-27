using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Emt.Emt_Server.Errors;
using Constants = FSDEV.MOD.Emt.Emt.Constants;

namespace FSDEV.MOD.Emt.Emt_Server.Notifications;

/// <summary>The kinds of notifications of EMT (BR-45). Each kind is a notification type of the Notifications module.</summary>
public static class NotificationKinds
{
    public const string NEW_ERROR = "NEW_ERROR";
    public const string ASSIGNED = "ASSIGNED";
    public const string ASSIGNEE_CHANGED = "ASSIGNEE_CHANGED";
    public const string STATUS_CHANGED = "STATUS_CHANGED";
    public const string NOTE_ADDED = "NOTE_ADDED";
    public const string SEVERITY_CHANGED = "SEVERITY_CHANGED";
}

/// <summary>One notification to create: the recipient and what it says.</summary>
public readonly record struct NotificationMessage(Guid UserId, string Kind, string Detail);

/// <summary>
/// Who is told about a change of an error (BR-44…BR-46, FR-27). A pure function of the error, so the rules are easy to test.
/// </summary>
public static class NotificationRecipients
{
    private const int MAX_NOTE_DETAIL_LENGTH = 120;

    /// <param name="hasPermission">user, permission → true when the user has the permission now.</param>
    /// <param name="getManagers">all users with the permission Manage-Errors.</param>
    public static List<NotificationMessage> Resolve(ErrorRecord error, ErrorChangeType changeType, Func<Guid, string, bool> hasPermission, Func<IReadOnlyCollection<Guid>> getManagers)
    {
        if (changeType == ErrorChangeType.Created)
        {
            // the reporter just reported it, so even a reporter who is a manager is not told
            return ForManagers(getManagers, error.ReporterUserId, NotificationKinds.NEW_ERROR, string.Empty);
        }

        if (changeType != ErrorChangeType.Updated || error.Events.Count == 0)
        {
            return [];
        }

        var change = error.Events[^1];
        if (!TryDescribe(error, change, out var kindForAssignee, out var kindForReporter, out var detail))
        {
            return [];
        }

        var actor = change.UserId;

        // the reporter withdrawing an error nobody works on yet: the managers have to know
        if (actor == error.ReporterUserId && error.AssigneeUserId is null)
        {
            return ForManagers(getManagers, actor, kindForAssignee, detail);
        }

        var messages = new List<NotificationMessage>();

        if (error.AssigneeUserId is Guid assignee && assignee != actor && hasPermission(assignee, Constants.Permissions.MANAGE_ERRORS))
        {
            messages.Add(new NotificationMessage(assignee, kindForAssignee, detail));
        }

        var reporter = error.ReporterUserId;
        var reporterMayRead = hasPermission(reporter, Constants.Permissions.REPORT_ERRORS) || hasPermission(reporter, Constants.Permissions.MANAGE_ERRORS);
        if (reporter != actor && reporterMayRead && messages.TrueForAll(message => message.UserId != reporter))
        {
            messages.Add(new NotificationMessage(reporter, kindForReporter, detail));
        }

        return messages;
    }

    private static List<NotificationMessage> ForManagers(Func<IReadOnlyCollection<Guid>> getManagers, Guid actor, string kind, string detail) =>
        getManagers().Where(user => user != actor).Distinct().Select(user => new NotificationMessage(user, kind, detail)).ToList();

    private static bool TryDescribe(ErrorRecord error, ErrorEvent change, out string kindForAssignee, out string kindForReporter, out string detail)
    {
        switch (change.EventType)
        {
            case ErrorEventTypes.ASSIGNED:
                // to the new assignee an assignment reads "assigned to you", to the reporter "the handler is ..."
                kindForAssignee = NotificationKinds.ASSIGNED;
                kindForReporter = NotificationKinds.ASSIGNEE_CHANGED;
                detail = error.AssigneeName;
                return true;
            case ErrorEventTypes.STATUS_CHANGED:
                kindForAssignee = kindForReporter = NotificationKinds.STATUS_CHANGED;
                detail = error.Status.ToWire();
                return true;
            case ErrorEventTypes.SEVERITY_CHANGED:
                kindForAssignee = kindForReporter = NotificationKinds.SEVERITY_CHANGED;
                detail = error.Severity.ToWire();
                return true;
            case ErrorEventTypes.NOTE_ADDED:
                var text = change.Text ?? string.Empty;
                kindForAssignee = kindForReporter = NotificationKinds.NOTE_ADDED;
                detail = text.Length <= MAX_NOTE_DETAIL_LENGTH ? text : text[..MAX_NOTE_DETAIL_LENGTH];
                return true;
            default:
                kindForAssignee = kindForReporter = detail = string.Empty;
                return false;
        }
    }
}

/// <summary>The words of a notification the Notifications module cannot translate itself: it only puts the values of the fields into the text.</summary>
public static class NotificationTexts
{
    /// <summary>The value of a notification as text in German (<paramref name="german"/>) or English.</summary>
    public static string Detail(string kind, string detail, bool german)
    {
        if (kind == NotificationKinds.STATUS_CHANGED)
        {
            return detail switch
            {
                "NEW" => german ? "Neu" : "New",
                "ACKNOWLEDGED" => german ? "Bestätigt" : "Acknowledged",
                "IN_PROGRESS" => german ? "In Bearbeitung" : "In progress",
                "RESOLVED" => german ? "Gelöst" : "Resolved",
                "CLOSED" => german ? "Geschlossen" : "Closed",
                "REJECTED" => german ? "Abgelehnt" : "Rejected",
                "CANCELLED" => german ? "Storniert" : "Cancelled",
                _ => detail,
            };
        }

        if (kind == NotificationKinds.SEVERITY_CHANGED)
        {
            return detail switch
            {
                "Info" => "Info",
                "Warning" => german ? "Warnung" : "Warning",
                "Error" => german ? "Fehler" : "Error",
                "Critical" => german ? "Kritisch" : "Critical",
                _ => detail,
            };
        }

        return detail;
    }
}
