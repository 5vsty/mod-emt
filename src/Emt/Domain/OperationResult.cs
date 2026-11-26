using FSDEV.MOD.Emt.Emt.Validation;

namespace FSDEV.MOD.Emt.Emt.Domain;

/// <summary>The Reason values of the frontend contract (07 §2.3). The frontend translates them as EMT_REASON_&lt;Reason&gt;.</summary>
public static class Reasons
{
    public const string FORBIDDEN = "Forbidden";
    public const string NOT_FOUND = "NotFound";
    public const string VALIDATION_FAILED = "ValidationFailed";
    public const string INVALID_TRANSITION = "InvalidTransition";
    public const string FINAL_STATE = "FinalState";
    public const string CONFLICT = "Conflict";
    public const string NOT_REPORTER = "NotReporter";
    public const string INVALID_ASSIGNEE = "InvalidAssignee";
    public const string ERROR_CODE_INACTIVE = "ErrorCodeInactive";
    public const string LOCATION_NOT_FOUND = "LocationNotFound";
    public const string CODE_EXISTS = "CodeExists";
    public const string CODE_LOCKED = "CodeLocked";
    public const string CODE_IN_USE = "CodeInUse";
    public const string OTHER_CODE_PROTECTED = "OtherCodeProtected";
    public const string UNAVAILABLE = "Unavailable";
    public const string INTERNAL = "Internal";
}

/// <summary>
/// Result of a service operation: either a value or a Reason for the frontend.
/// </summary>
public class OperationResult<T>
{
    public bool Successful => Reason is null;
    public string? Reason { get; private init; }
    public T? Value { get; private init; }
    public List<ValidationError> ValidationErrors { get; private init; } = [];

    /// <summary>Name of the user whose change caused a conflict (07 ConflictByName).</summary>
    public string? ConflictByName { get; private init; }

    public static OperationResult<T> Ok(T value) => new() { Value = value };

    public static OperationResult<T> Fail(string reason) => new() { Reason = reason };

    public static OperationResult<T> Invalid(List<ValidationError> errors) => new() { Reason = Reasons.VALIDATION_FAILED, ValidationErrors = errors };

    public static OperationResult<T> Conflicted(string? byName, T? current = default) => new() { Reason = Reasons.CONFLICT, ConflictByName = byName, Value = current };
}
