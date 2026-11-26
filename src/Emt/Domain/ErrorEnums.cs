namespace FSDEV.MOD.Emt.Emt.Domain;

public enum ErrorStatus
{
    New,
    Acknowledged,
    InProgress,
    Resolved,
    Closed,
    Rejected,
    Cancelled,
}

public enum Severity
{
    Info,
    Warning,
    Error,
    Critical,
}

public enum ErrorAction
{
    Acknowledge,
    StartProgress,
    Resolve,
    Reopen,
    Close,
    Reject,
    Cancel,
}

/// <summary>
/// Conversion between the enums and the strings used on ILP / in the frontend contract (07 §2.1).
/// </summary>
public static class ErrorEnumExtensions
{
    public static string ToWire(this ErrorStatus status) => status switch
    {
        ErrorStatus.New => "NEW",
        ErrorStatus.Acknowledged => "ACKNOWLEDGED",
        ErrorStatus.InProgress => "IN_PROGRESS",
        ErrorStatus.Resolved => "RESOLVED",
        ErrorStatus.Closed => "CLOSED",
        ErrorStatus.Rejected => "REJECTED",
        ErrorStatus.Cancelled => "CANCELLED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static bool TryParseStatus(string? value, out ErrorStatus status)
    {
        switch (value)
        {
            case "NEW": status = ErrorStatus.New; return true;
            case "ACKNOWLEDGED": status = ErrorStatus.Acknowledged; return true;
            case "IN_PROGRESS": status = ErrorStatus.InProgress; return true;
            case "RESOLVED": status = ErrorStatus.Resolved; return true;
            case "CLOSED": status = ErrorStatus.Closed; return true;
            case "REJECTED": status = ErrorStatus.Rejected; return true;
            case "CANCELLED": status = ErrorStatus.Cancelled; return true;
            default: status = default; return false;
        }
    }

    public static string ToWire(this Severity severity) => severity switch
    {
        Severity.Info => "Info",
        Severity.Warning => "Warning",
        Severity.Error => "Error",
        Severity.Critical => "Critical",
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, null),
    };

    public static bool TryParseSeverity(string? value, out Severity severity)
    {
        switch (value)
        {
            case "Info": severity = Severity.Info; return true;
            case "Warning": severity = Severity.Warning; return true;
            case "Error": severity = Severity.Error; return true;
            case "Critical": severity = Severity.Critical; return true;
            default: severity = default; return false;
        }
    }

    public static string ToWire(this ErrorAction action) => action.ToString();

    public static bool TryParseAction(string? value, out ErrorAction action)
    {
        foreach (var candidate in Enum.GetValues<ErrorAction>())
        {
            if (candidate.ToString() == value)
            {
                action = candidate;
                return true;
            }
        }

        action = default;
        return false;
    }
}
