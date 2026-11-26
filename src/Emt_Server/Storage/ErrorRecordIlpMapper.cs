using FSDEV.Common.Logger;
using FSDEV.ILP.Intralogistikplattform.SystemState.States;
using FSDEV.ILP.Intralogistikplattform.SystemState.States.CollectionStates;
using FSDEV.ILP.Intralogistikplattform.SystemState.States.PrimitiveStates;
using FSDEV.ILP.Intralogistikplattform.SystemState_Client.Helpers;
using FSDEV.MOD.Emt.Emt.Domain;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Constants = FSDEV.MOD.Emt.Emt.Constants;

namespace FSDEV.MOD.Emt.Emt_Server.Storage;

/// <summary>
/// Maps an <see cref="ErrorRecord"/> to the ILP schema of 07 §3 and back.
/// </summary>
internal static class ErrorRecordIlpMapper
{
    private const string LOG_PREFIX = $"[{nameof(ErrorRecordIlpMapper)}]";

    internal static class Keys
    {
        public const string ID = "Id";
        public const string NUMBER = "Number";
        public const string ERROR_NUMBER = "ErrorNumber";
        public const string CLIENT_REQUEST_ID = "ClientRequestId";
        public const string STATUS = "Status";
        public const string SEVERITY = "Severity";
        public const string ERROR_CODE_ID = "ErrorCodeId";
        public const string ERROR_CODE = "ErrorCode";
        public const string CODE_TITLE = "CodeTitle";
        public const string LOCATION_ID = "LocationId";
        public const string LOCATION_PATH = "LocationPath";
        public const string DESCRIPTION = "Description";
        public const string REPORTER_USER_ID = "ReporterUserId";
        public const string REPORTER_NAME = "ReporterName";
        public const string ASSIGNEE_USER_ID = "AssigneeUserId";
        public const string ASSIGNEE_NAME = "AssigneeName";
        public const string REPORTED_AT = "ReportedAt";
        public const string LAST_CHANGED_AT = "LastChangedAt";
        public const string LAST_CHANGED_BY_NAME = "LastChangedByName";
        public const string FINALIZED_AT = "FinalizedAt";
        public const string NOTIFICATION_ID = "NotificationId";
        public const string NOTIFICATION_STATE = "NotificationState";
        public const string VERSION = "Version";
        public const string EVENTS = "Events";
    }

    /// <summary>The errors of a month are stored below one dictionary, the month is the one of the report time (UTC).</summary>
    public static string GetMonthKey(long reportedAtMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(reportedAtMs).UtcDateTime.ToString("yyyyMM", CultureInfo.InvariantCulture);

    public static string GetMonthPath(ErrorRecord record) => $"{Constants.Ilp.Paths.ERRORS}/{GetMonthKey(record.ReportedAt)}";

    public static string GetPath(ErrorRecord record) => $"{GetMonthPath(record)}/{record.Id}";

    public static DictionaryState<StringState, IBaseState> ToIlpState(ErrorRecord record)
    {
        var state = new DictionaryState<StringState, IBaseState>(GetPath(record))
        {
            [new StringState(Keys.ID)] = new StringState(record.Id.ToString()),
            [new StringState(Keys.NUMBER)] = new LongState(record.Number),
            [new StringState(Keys.ERROR_NUMBER)] = new StringState(record.ErrorNumber),
            [new StringState(Keys.CLIENT_REQUEST_ID)] = new StringState(record.ClientRequestId.ToString()),
            [new StringState(Keys.ERROR_CODE_ID)] = new StringState(record.ErrorCodeId.ToString()),
            [new StringState(Keys.ERROR_CODE)] = new StringState(record.ErrorCode),
            [new StringState(Keys.LOCATION_ID)] = new StringState(record.LocationId),
            [new StringState(Keys.LOCATION_PATH)] = new StringState(record.LocationPath),
            [new StringState(Keys.DESCRIPTION)] = new StringState(record.Description),
            [new StringState(Keys.REPORTER_USER_ID)] = new StringState(record.ReporterUserId.ToString()),
            [new StringState(Keys.REPORTER_NAME)] = new StringState(record.ReporterName),
            [new StringState(Keys.REPORTED_AT)] = new LongState(record.ReportedAt),
            [new StringState(Keys.CODE_TITLE)] = new StringState(record.CodeTitle),
        };

        ApplyMutableFields(state, record);

        return state;
    }

    /// <summary>
    /// Writes the fields that can change after the creation into the state (existing keys are replaced, which records the change).
    /// </summary>
    public static void ApplyMutableFields(DictionaryState<StringState, IBaseState> state, ErrorRecord record)
    {
        SetIfChanged(state, Keys.STATUS, new StringState(record.Status.ToWire()));
        SetIfChanged(state, Keys.SEVERITY, new StringState(record.Severity.ToWire()));
        SetIfChanged(state, Keys.ASSIGNEE_USER_ID, new StringState(record.AssigneeUserId?.ToString() ?? string.Empty));
        SetIfChanged(state, Keys.ASSIGNEE_NAME, new StringState(record.AssigneeName));
        SetIfChanged(state, Keys.LAST_CHANGED_AT, new LongState(record.LastChangedAt));
        SetIfChanged(state, Keys.LAST_CHANGED_BY_NAME, new StringState(record.LastChangedByName));
        SetIfChanged(state, Keys.FINALIZED_AT, new LongState(record.FinalizedAt));
        SetIfChanged(state, Keys.NOTIFICATION_ID, new StringState(record.NotificationId));
        SetIfChanged(state, Keys.NOTIFICATION_STATE, new StringState(record.NotificationState));
        SetIfChanged(state, Keys.VERSION, new IntState(record.Version));
        SetIfChanged(state, Keys.EVENTS, new StringState(ErrorRecord.SerializeEvents(record.Events)));
    }

    /// <summary>Only a value that really differs is written, so a change request contains only the changes.</summary>
    private static void SetIfChanged(DictionaryState<StringState, IBaseState> state, string key, IPrimitiveState value)
    {
        var stateKey = new StringState(key);
        if (state.TryGetValue(stateKey, out var existing) && existing is IPrimitiveState existingPrimitive && existingPrimitive.GetType() == value.GetType() && existingPrimitive.GetValue.Equals(value.GetValue))
        {
            return;
        }

        state[stateKey] = value;
    }

    public static bool TryParse(IBaseState state, [NotNullWhen(true)] out ErrorRecord? record)
    {
        record = null;

        if (state is not DictionaryState<StringState, IBaseState> dict)
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Cannot parse error, expecting dictState: {state.Key}");
            return false;
        }

        var allPresent = true;
        allPresent &= dict.TryGetString(Keys.ID, out var id);
        allPresent &= dict.TryGetLong(Keys.NUMBER, out var number);
        allPresent &= dict.TryGetString(Keys.ERROR_NUMBER, out var errorNumber);
        allPresent &= dict.TryGetString(Keys.CLIENT_REQUEST_ID, out var clientRequestId);
        allPresent &= dict.TryGetString(Keys.STATUS, out var status);
        allPresent &= dict.TryGetString(Keys.SEVERITY, out var severity);
        allPresent &= dict.TryGetString(Keys.ERROR_CODE_ID, out var errorCodeId);
        allPresent &= dict.TryGetString(Keys.ERROR_CODE, out var errorCode);
        allPresent &= dict.TryGetString(Keys.LOCATION_ID, out var locationId);
        allPresent &= dict.TryGetString(Keys.LOCATION_PATH, out var locationPath);
        allPresent &= dict.TryGetString(Keys.DESCRIPTION, out var description);
        allPresent &= dict.TryGetString(Keys.REPORTER_USER_ID, out var reporterUserId);
        allPresent &= dict.TryGetString(Keys.REPORTER_NAME, out var reporterName);
        allPresent &= dict.TryGetString(Keys.ASSIGNEE_USER_ID, out var assigneeUserId);
        allPresent &= dict.TryGetString(Keys.ASSIGNEE_NAME, out var assigneeName);
        allPresent &= dict.TryGetLong(Keys.REPORTED_AT, out var reportedAt);
        allPresent &= dict.TryGetLong(Keys.LAST_CHANGED_AT, out var lastChangedAt);
        allPresent &= dict.TryGetString(Keys.LAST_CHANGED_BY_NAME, out var lastChangedByName);
        allPresent &= dict.TryGetLong(Keys.FINALIZED_AT, out var finalizedAt);
        allPresent &= dict.TryGetString(Keys.NOTIFICATION_ID, out var notificationId);
        allPresent &= dict.TryGetString(Keys.NOTIFICATION_STATE, out var notificationState);
        allPresent &= dict.TryGetInt(Keys.VERSION, out var version);
        allPresent &= dict.TryGetString(Keys.EVENTS, out var eventsJson);
        allPresent &= TryGetCodeTitle(dict, out var codeTitle);

        if (!allPresent)
        {
            return false;
        }

        if (!Guid.TryParse(id, out var errorId) || !Guid.TryParse(clientRequestId, out var requestId) || !Guid.TryParse(errorCodeId, out var codeId) || !Guid.TryParse(reporterUserId, out var reporterId))
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Cannot parse error {state.Key}, one of the id fields is not a guid");
            return false;
        }

        if (!ErrorEnumExtensions.TryParseStatus(status, out var parsedStatus) || !ErrorEnumExtensions.TryParseSeverity(severity, out var parsedSeverity))
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Cannot parse error {state.Key}, unknown status '{status}' or severity '{severity}'");
            return false;
        }

        if (!ErrorRecord.TryParseEvents(eventsJson, out var events))
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Cannot parse error {state.Key}, events are no valid json");
            return false;
        }

        Guid? assignee = null;
        if (assigneeUserId.Length > 0)
        {
            if (!Guid.TryParse(assigneeUserId, out var assigneeGuid))
            {
                Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Cannot parse error {state.Key}, assignee is not a guid");
                return false;
            }

            assignee = assigneeGuid;
        }

        record = new ErrorRecord
        {
            Id = errorId,
            Number = number,
            ErrorNumber = errorNumber,
            ClientRequestId = requestId,
            Status = parsedStatus,
            Severity = parsedSeverity,
            ErrorCodeId = codeId,
            ErrorCode = errorCode,
            CodeTitle = codeTitle,
            LocationId = locationId,
            LocationPath = locationPath,
            Description = description,
            ReporterUserId = reporterId,
            ReporterName = reporterName,
            AssigneeUserId = assignee,
            AssigneeName = assigneeName,
            ReportedAt = reportedAt,
            LastChangedAt = lastChangedAt,
            LastChangedByName = lastChangedByName,
            FinalizedAt = finalizedAt,
            NotificationId = notificationId,
            NotificationState = notificationState,
            Version = version,
            Events = events,
        };

        return true;
    }

    /// <summary>The code title is a string; errors saved before titles became single-language hold a dictionary per language instead.</summary>
    private static bool TryGetCodeTitle(DictionaryState<StringState, IBaseState> dict, out string codeTitle)
    {
        if (dict.TryGetString(Keys.CODE_TITLE, out var title))
        {
            codeTitle = title;
            return true;
        }

        if (dict.TryGetDict(Keys.CODE_TITLE, out var perLanguage))
        {
            var texts = new Dictionary<string, string>();
            foreach (var (language, value) in perLanguage)
            {
                if (value is StringState text)
                {
                    texts[language.Value] = text.Value;
                }
            }

            codeTitle = LegacyLocalizedText.ToSingleText(texts);
            return true;
        }

        codeTitle = string.Empty;
        return false;
    }
}
