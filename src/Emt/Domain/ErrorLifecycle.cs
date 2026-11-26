namespace FSDEV.MOD.Emt.Emt.Domain;

/// <summary>
/// The only place that knows which status changes are allowed (05 § Vòng đời, BR-11..BR-14).
/// </summary>
public static class ErrorLifecycle
{
    public static bool IsFinal(ErrorStatus status) => status is ErrorStatus.Closed or ErrorStatus.Rejected or ErrorStatus.Cancelled;

    /// <summary>Notes and severity changes are possible in every status except the final ones.</summary>
    public static bool CanAddNoteOrChangeSeverity(ErrorStatus status) => !IsFinal(status);

    /// <summary>Assignment is only possible while the error is still being worked on.</summary>
    public static bool CanAssign(ErrorStatus status) => status is ErrorStatus.New or ErrorStatus.Acknowledged or ErrorStatus.InProgress;

    public static bool RequiresText(ErrorAction action) => action is ErrorAction.Resolve or ErrorAction.Reopen or ErrorAction.Reject or ErrorAction.Cancel;

    /// <summary>Cancel is the action of the reporter, all others are actions of an error manager.</summary>
    public static bool IsReporterAction(ErrorAction action) => action == ErrorAction.Cancel;

    public static bool TryGetTarget(ErrorStatus from, ErrorAction action, out ErrorStatus to)
    {
        to = default;
        switch (action, from)
        {
            case (ErrorAction.Acknowledge, ErrorStatus.New):
                to = ErrorStatus.Acknowledged;
                return true;
            case (ErrorAction.StartProgress, ErrorStatus.New or ErrorStatus.Acknowledged):
                to = ErrorStatus.InProgress;
                return true;
            case (ErrorAction.Resolve, ErrorStatus.New or ErrorStatus.Acknowledged or ErrorStatus.InProgress):
                to = ErrorStatus.Resolved;
                return true;
            case (ErrorAction.Reopen, ErrorStatus.Resolved):
                to = ErrorStatus.InProgress;
                return true;
            case (ErrorAction.Close, ErrorStatus.Resolved):
                to = ErrorStatus.Closed;
                return true;
            case (ErrorAction.Reject, ErrorStatus.New or ErrorStatus.Acknowledged):
                to = ErrorStatus.Rejected;
                return true;
            case (ErrorAction.Cancel, ErrorStatus.New):
                to = ErrorStatus.Cancelled;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The actions the given user may execute on the error right now (used for AllowedActions in the detail response).
    /// </summary>
    public static List<ErrorAction> GetAllowedActions(ErrorStatus status, bool isManager, bool isReporter)
    {
        var result = new List<ErrorAction>();
        foreach (var action in Enum.GetValues<ErrorAction>())
        {
            if (!TryGetTarget(status, action, out _))
            {
                continue;
            }

            var allowed = IsReporterAction(action) ? isReporter : isManager;
            if (allowed)
            {
                result.Add(action);
            }
        }

        return result;
    }
}
