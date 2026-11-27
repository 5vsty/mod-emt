using FSDEV.Common.Logger;
using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Emt.Emt.Validation;
using FSDEV.MOD.Emt.Emt_Server.Catalog;
using FSDEV.MOD.Emt.Emt_Server.Locations;
using FSDEV.MOD.Emt.Emt_Server.Notifications;
using FSDEV.MOD.Emt.Emt_Server.Security;
using FSDEV.MOD.Emt.Emt_Server.Storage;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Constants = FSDEV.MOD.Emt.Emt.Constants;

namespace FSDEV.MOD.Emt.Emt_Server.Errors;

public record SubmitResult(ErrorRecord Error, bool Duplicate, bool NoManagersConfigured);

public record ErrorDetailResult(ErrorRecord Error, List<ErrorAction> AllowedActions);

public record ReportFormData(List<ErrorCode> Codes, IReadOnlyList<LocationOption> Locations, bool NoActiveCodes, bool LocationEnabled = true);

/// <summary>
/// The business logic of the errors. Every operation checks the permission of the calling user itself, the user always comes from
/// the session and never from the payload (BR-06). An error is only changed after the new state was written to ILP successfully.
/// </summary>
public class ErrorService : INotificationStateStore
{
    private const string LOG_PREFIX = $"[{nameof(ErrorService)}]";

    private readonly IErrorStore Store;
    private readonly ErrorIndex Index;
    private readonly IErrorNumberSource Sequence;
    private readonly CatalogStore Catalog;
    private readonly LocationProvider Locations;
    private readonly EmtPermissionService Permissions;
    private readonly IClock Clock;
    private readonly List<IErrorObserver> Observers = [];

    private readonly object CreateLock = new();
    private readonly ConcurrentDictionary<Guid, object> ErrorLocks = new();

    private readonly bool LocationEnabled;

    /// <param name="locationEnabled">false = the facility structure is never read and a report needs no location (config Location.Enabled).</param>
    public ErrorService(IErrorStore store, ErrorIndex index, IErrorNumberSource sequence, CatalogStore catalog, LocationProvider locations, EmtPermissionService permissions, IClock clock, bool locationEnabled = true)
    {
        LocationEnabled = locationEnabled;
        Store = store;
        Index = index;
        Sequence = sequence;
        Catalog = catalog;
        Locations = locations;
        Permissions = permissions;
        Clock = clock;
    }

    public void AddObserver(IErrorObserver observer) => Observers.Add(observer);

    public bool TryGet(Guid errorId, [NotNullWhen(true)] out ErrorRecord? error)
    {
        if (Index.TryGet(errorId, out var current))
        {
            error = current.Clone();
            return true;
        }

        error = null;
        return false;
    }

    /// <summary>
    /// Remembers the notification of an error (FR-24). Written under the lock of the error like every change, but it is no change of the error:
    /// no version, no event, no message to the tables.
    /// </summary>
    public bool TrySetNotification(Guid errorId, string notificationId, string state)
    {
        lock (ErrorLocks.GetOrAdd(errorId, _ => new object()))
        {
            if (!Index.TryGet(errorId, out var current))
            {
                return false;
            }

            var updated = current.Clone();
            updated.NotificationId = notificationId;
            updated.NotificationState = state;
            if (!Store.TryUpdate(updated))
            {
                return false;
            }

            Index.Set(updated);
            return true;
        }
    }

    public OperationResult<ReportFormData> GetReportFormData(Guid userId)
    {
        if (!Permissions.HasPermission(userId, Constants.Permissions.REPORT_ERRORS))
        {
            return OperationResult<ReportFormData>.Fail(Reasons.FORBIDDEN);
        }

        IReadOnlyList<LocationOption> locations = [];
        if (LocationEnabled && !Locations.TryGetAll(out locations!))
        {
            return OperationResult<ReportFormData>.Fail(Reasons.UNAVAILABLE);
        }

        var codes = Catalog.GetActiveForReport();
        return OperationResult<ReportFormData>.Ok(new ReportFormData(codes, locations, codes.Count == 0, LocationEnabled));
    }

    public OperationResult<SubmitResult> Submit(Guid userId, Guid clientRequestId, string? locationId, Guid errorCodeId, string? description)
    {
        if (!Permissions.HasPermission(userId, Constants.Permissions.REPORT_ERRORS))
        {
            return OperationResult<SubmitResult>.Fail(Reasons.FORBIDDEN);
        }

        if (clientRequestId == Guid.Empty)
        {
            return OperationResult<SubmitResult>.Invalid([new ValidationError("ClientRequestId", ValidationKeys.REQUIRED)]);
        }

        // a resent request returns the error that was already created (NFR-05)
        if (Index.TryGetByClientRequestId(clientRequestId, out var existing))
        {
            return existing.ReporterUserId == userId
                ? OperationResult<SubmitResult>.Ok(new SubmitResult(existing, true, !Permissions.HasAnyManager()))
                : OperationResult<SubmitResult>.Fail(Reasons.FORBIDDEN);
        }

        var codeFound = Catalog.TryGet(errorCodeId, out var code);
        var errors = ErrorReportValidator.Validate(locationId, errorCodeId, codeFound && code.IsOther, description, LocationEnabled);
        if (errorCodeId != Guid.Empty && !codeFound)
        {
            errors.Add(new ValidationError("ErrorCodeId", ValidationKeys.INVALID));
        }

        if (errors.Count > 0)
        {
            return OperationResult<SubmitResult>.Invalid(errors);
        }

        if (!code.Active)
        {
            return OperationResult<SubmitResult>.Fail(Reasons.ERROR_CODE_INACTIVE);
        }

        // without the location switch no structure is read, the error just has no location
        var location = new LocationOption(string.Empty, string.Empty, string.Empty);
        if (LocationEnabled)
        {
            if (!Locations.TryGetAll(out _))
            {
                return OperationResult<SubmitResult>.Fail(Reasons.UNAVAILABLE);
            }

            if (!Locations.TryGet(locationId!, out location))
            {
                return OperationResult<SubmitResult>.Fail(Reasons.LOCATION_NOT_FOUND);
            }
        }

        var now = Clock.UtcNowMs;
        var reporterName = GetUserName(userId);

        ErrorRecord record;
        lock (CreateLock)
        {
            // checked again under the lock, so two parallel requests with the same id create one error only
            if (Index.TryGetByClientRequestId(clientRequestId, out existing))
            {
                return OperationResult<SubmitResult>.Ok(new SubmitResult(existing, true, !Permissions.HasAnyManager()));
            }

            if (!Sequence.TryReserve(out var number, out var errorNumber))
            {
                Logging.Log(LogLevel.Error, $"{LOG_PREFIX} No number for a new error (the IdManager did not answer)");
                return OperationResult<SubmitResult>.Fail(Reasons.UNAVAILABLE);
            }

            record = new ErrorRecord
            {
                Id = Guid.NewGuid(),
                Number = number,
                ErrorNumber = errorNumber,
                ClientRequestId = clientRequestId,
                Status = ErrorStatus.New,
                Severity = code.DefaultSeverity,
                ErrorCodeId = code.Id,
                ErrorCode = code.Code,
                CodeTitle = code.Title,
                LocationId = location.LocationId,
                LocationPath = location.Path,
                Description = description?.Trim() ?? string.Empty,
                ReporterUserId = userId,
                ReporterName = reporterName,
                ReportedAt = now,
                LastChangedAt = now,
                LastChangedByName = reporterName,
                Version = 1,
            };
            record.AddEvent(ErrorEventTypes.CREATED, userId, reporterName, now);

            if (!Store.TryCreate(record, number))
            {
                return OperationResult<SubmitResult>.Fail(Reasons.UNAVAILABLE);
            }

            Sequence.Commit(number);
            Index.Set(record);
        }

        if (!Catalog.TryMarkUsed(code.Id, now))
        {
            Logging.Log(LogLevel.Warning, $"{LOG_PREFIX} Could not mark error code {code.Code} as used");
        }

        Notify(record, ErrorChangeType.Created);

        return OperationResult<SubmitResult>.Ok(new SubmitResult(record, false, !Permissions.HasAnyManager()));
    }

    public OperationResult<List<ErrorRecord>> GetMyErrors(Guid userId)
    {
        if (!Permissions.HasPermission(userId, Constants.Permissions.REPORT_ERRORS))
        {
            return OperationResult<List<ErrorRecord>>.Fail(Reasons.FORBIDDEN);
        }

        return OperationResult<List<ErrorRecord>>.Ok(Index.GetOfReporter(userId, Clock.UtcNowMs, Constants.Limits.MY_ERRORS_RECENT_DAYS, Constants.Limits.MAX_MY_ERRORS));
    }

    public OperationResult<ErrorDetailResult> GetDetail(Guid userId, Guid errorId)
    {
        var isManager = Permissions.HasPermission(userId, Constants.Permissions.MANAGE_ERRORS);
        var isReportPermitted = Permissions.HasPermission(userId, Constants.Permissions.REPORT_ERRORS);
        if (!isManager && !isReportPermitted)
        {
            return OperationResult<ErrorDetailResult>.Fail(Reasons.FORBIDDEN);
        }

        if (!Index.TryGet(errorId, out var error))
        {
            return OperationResult<ErrorDetailResult>.Fail(Reasons.NOT_FOUND);
        }

        // a reporter without the manage permission only sees the own errors, read only
        var isReporter = error.ReporterUserId == userId;
        if (!isManager && !isReporter)
        {
            return OperationResult<ErrorDetailResult>.Fail(Reasons.FORBIDDEN);
        }

        var allowed = ErrorLifecycle.GetAllowedActions(error.Status, isManager, isReporter && isReportPermitted);
        return OperationResult<ErrorDetailResult>.Ok(new ErrorDetailResult(error, allowed));
    }

    public OperationResult<ErrorRecord> Cancel(Guid userId, Guid errorId, int expectedVersion, string? text)
    {
        if (!Permissions.HasPermission(userId, Constants.Permissions.REPORT_ERRORS))
        {
            return OperationResult<ErrorRecord>.Fail(Reasons.FORBIDDEN);
        }

        var textErrors = ValidateActionText(ErrorAction.Cancel, text);
        if (textErrors.Count > 0)
        {
            return OperationResult<ErrorRecord>.Invalid(textErrors);
        }

        return Change(userId, errorId, expectedVersion, (error, now) =>
        {
            if (error.ReporterUserId != userId)
            {
                return Reasons.NOT_REPORTER;
            }

            return ApplyStatusAction(error, ErrorAction.Cancel, text, userId, now);
        }, true);
    }

    /// <summary>
    /// Removes the error with its history for good (ILP, index, tables); only error managers may do this. Unlike the retention job
    /// it does not wait for a final status.
    /// </summary>
    public OperationResult<bool> Delete(Guid userId, Guid errorId)
    {
        if (!Permissions.HasPermission(userId, Constants.Permissions.MANAGE_ERRORS))
        {
            return OperationResult<bool>.Fail(Reasons.FORBIDDEN);
        }

        lock (ErrorLocks.GetOrAdd(errorId, _ => new object()))
        {
            if (!Index.TryGet(errorId, out var error))
            {
                return OperationResult<bool>.Fail(Reasons.NOT_FOUND);
            }

            if (!Store.TryDelete(error))
            {
                return OperationResult<bool>.Fail(Reasons.UNAVAILABLE);
            }

            _ = Index.Remove(errorId);
            Notify(error, ErrorChangeType.Deleted);
        }

        _ = ErrorLocks.TryRemove(errorId, out _);
        return OperationResult<bool>.Ok(true);
    }

    public OperationResult<ErrorRecord> ChangeStatus(Guid userId, Guid errorId, string? action, int expectedVersion, string? text)
    {
        if (!Permissions.HasPermission(userId, Constants.Permissions.MANAGE_ERRORS))
        {
            return OperationResult<ErrorRecord>.Fail(Reasons.FORBIDDEN);
        }

        if (!ErrorEnumExtensions.TryParseAction(action, out var parsedAction))
        {
            return OperationResult<ErrorRecord>.Invalid([new ValidationError("Action", ValidationKeys.INVALID)]);
        }

        // cancelling is the action of the reporter and has its own message
        if (ErrorLifecycle.IsReporterAction(parsedAction))
        {
            return OperationResult<ErrorRecord>.Fail(Reasons.INVALID_TRANSITION);
        }

        var textErrors = ValidateActionText(parsedAction, text);
        if (textErrors.Count > 0)
        {
            return OperationResult<ErrorRecord>.Invalid(textErrors);
        }

        return Change(userId, errorId, expectedVersion, (error, now) => ApplyStatusAction(error, parsedAction, text, userId, now), true);
    }

    public OperationResult<ErrorRecord> Assign(Guid userId, Guid errorId, Guid? assigneeUserId, int expectedVersion)
    {
        if (!Permissions.HasPermission(userId, Constants.Permissions.MANAGE_ERRORS))
        {
            return OperationResult<ErrorRecord>.Fail(Reasons.FORBIDDEN);
        }

        return Change(userId, errorId, expectedVersion, (error, now) =>
        {
            if (!ErrorLifecycle.CanAssign(error.Status))
            {
                return Reasons.FINAL_STATE;
            }

            var assigneeName = string.Empty;
            if (assigneeUserId is not null)
            {
                // the assignee must be able to work on errors (AC-38)
                if (!Permissions.HasPermission(assigneeUserId.Value, Constants.Permissions.MANAGE_ERRORS))
                {
                    return Reasons.INVALID_ASSIGNEE;
                }

                assigneeName = GetUserName(assigneeUserId.Value);
            }

            var userName = GetUserName(userId);
            error.AddEvent(ErrorEventTypes.ASSIGNED, userId, userName, now, fromValue: NameOrNone(error.AssigneeName), toValue: NameOrNone(assigneeName));
            error.AssigneeUserId = assigneeUserId;
            error.AssigneeName = assigneeName;
            return null;
        }, true);
    }

    public OperationResult<ErrorRecord> ChangeSeverity(Guid userId, Guid errorId, string? severity, int expectedVersion)
    {
        if (!Permissions.HasPermission(userId, Constants.Permissions.MANAGE_ERRORS))
        {
            return OperationResult<ErrorRecord>.Fail(Reasons.FORBIDDEN);
        }

        if (!ErrorEnumExtensions.TryParseSeverity(severity, out var newSeverity))
        {
            return OperationResult<ErrorRecord>.Invalid([new ValidationError("Severity", ValidationKeys.INVALID)]);
        }

        return Change(userId, errorId, expectedVersion, (error, now) =>
        {
            if (!ErrorLifecycle.CanAddNoteOrChangeSeverity(error.Status))
            {
                return Reasons.FINAL_STATE;
            }

            error.AddEvent(ErrorEventTypes.SEVERITY_CHANGED, userId, GetUserName(userId), now, fromValue: error.Severity.ToWire(), toValue: newSeverity.ToWire());
            error.Severity = newSeverity;
            return null;
        }, true);
    }

    /// <summary>Adds a note. A note does not change the version of the error, so nobody gets a conflict because of a note.</summary>
    public OperationResult<ErrorRecord> AddNote(Guid userId, Guid errorId, string? text)
    {
        if (!Permissions.HasPermission(userId, Constants.Permissions.MANAGE_ERRORS))
        {
            return OperationResult<ErrorRecord>.Fail(Reasons.FORBIDDEN);
        }

        var errors = ErrorReportValidator.ValidateText(text, true, Constants.Limits.MAX_NOTE_LENGTH);
        if (errors.Count > 0)
        {
            return OperationResult<ErrorRecord>.Invalid(errors);
        }

        return Change(userId, errorId, null, (error, now) =>
        {
            if (!ErrorLifecycle.CanAddNoteOrChangeSeverity(error.Status))
            {
                return Reasons.FINAL_STATE;
            }

            error.AddEvent(ErrorEventTypes.NOTE_ADDED, userId, GetUserName(userId), now, text: text!.Trim());
            return null;
        }, false);
    }

    public OperationResult<List<EmtUser>> GetAssignableUsers(Guid userId)
    {
        if (!Permissions.HasPermission(userId, Constants.Permissions.MANAGE_ERRORS))
        {
            return OperationResult<List<EmtUser>>.Fail(Reasons.FORBIDDEN);
        }

        return OperationResult<List<EmtUser>>.Ok(Permissions.GetUsersWithPermissionDetails(Constants.Permissions.MANAGE_ERRORS));
    }

    /// <summary>The text is mandatory for resolve, reopen, reject and cancel (BR-12, BR-13); for every action it is limited in length.</summary>
    private static List<ValidationError> ValidateActionText(ErrorAction action, string? text) =>
        ErrorReportValidator.ValidateText(text, ErrorLifecycle.RequiresText(action), Constants.Limits.MAX_NOTE_LENGTH);

    private string? ApplyStatusAction(ErrorRecord error, ErrorAction action, string? text, Guid userId, long now)
    {
        if (!ErrorLifecycle.TryGetTarget(error.Status, action, out var target))
        {
            return Reasons.INVALID_TRANSITION;
        }

        error.AddEvent(ErrorEventTypes.STATUS_CHANGED, userId, GetUserName(userId), now, action.ToWire(), error.Status.ToWire(), target.ToWire(), string.IsNullOrWhiteSpace(text) ? null : text.Trim());
        error.Status = target;
        error.FinalizedAt = ErrorLifecycle.IsFinal(target) ? now : 0;
        return null;
    }

    /// <summary>
    /// The common path of every change of an existing error: lock the error, check the version, let <paramref name="mutate"/> change a copy
    /// (it returns a Reason to refuse), write the copy to ILP and only then replace the error in the index.
    /// </summary>
    private OperationResult<ErrorRecord> Change(Guid userId, Guid errorId, int? expectedVersion, Func<ErrorRecord, long, string?> mutate, bool incrementVersion)
    {
        lock (ErrorLocks.GetOrAdd(errorId, _ => new object()))
        {
            if (!Index.TryGet(errorId, out var current))
            {
                return OperationResult<ErrorRecord>.Fail(Reasons.NOT_FOUND);
            }

            if (expectedVersion is not null && expectedVersion != current.Version)
            {
                return OperationResult<ErrorRecord>.Conflicted(current.LastChangedByName, current);
            }

            var now = Clock.UtcNowMs;
            var updated = current.Clone();
            var refusal = mutate(updated, now);
            if (refusal is not null)
            {
                return OperationResult<ErrorRecord>.Fail(refusal);
            }

            if (incrementVersion)
            {
                updated.Version = current.Version + 1;
            }

            updated.LastChangedAt = now;
            updated.LastChangedByName = GetUserName(userId);

            if (!Store.TryUpdate(updated))
            {
                return OperationResult<ErrorRecord>.Fail(Reasons.UNAVAILABLE);
            }

            Index.Set(updated);
            Notify(updated, ErrorChangeType.Updated);
            return OperationResult<ErrorRecord>.Ok(updated);
        }
    }

    private string GetUserName(Guid userId) => Permissions.TryGetDisplayName(userId, out var name) ? name : userId.ToString();

    private static string NameOrNone(string name) => name.Length == 0 ? "-" : name;

    private void Notify(ErrorRecord error, ErrorChangeType changeType)
    {
        foreach (var observer in Observers)
        {
            try
            {
                observer.OnErrorChanged(error.Clone(), changeType);
            }
            catch (Exception exception)
            {
                // an observer (table, push) must never undo or fail a change that is already persisted
                Logging.Log(LogLevel.Error, exception, $"{LOG_PREFIX} Observer {observer.GetType().Name} failed for error {error.ErrorNumber}");
            }
        }
    }
}
