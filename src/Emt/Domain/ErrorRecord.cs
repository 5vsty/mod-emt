using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Diagnostics.CodeAnalysis;

namespace FSDEV.MOD.Emt.Emt.Domain;

public static class ErrorEventTypes
{
    public const string CREATED = "Created";
    public const string STATUS_CHANGED = "StatusChanged";
    public const string ASSIGNED = "Assigned";
    public const string SEVERITY_CHANGED = "SeverityChanged";
    public const string NOTE_ADDED = "NoteAdded";
}

/// <summary>
/// One entry of the history of an error. Entries are only ever appended (NFR-06).
/// </summary>
public class ErrorEvent
{
    /// <summary>Sequence number inside the error (1, 2, 3, ...).</summary>
    public int Seq { get; set; }
    public string EventType { get; set; } = string.Empty;
    public long At { get; set; }
    public Guid UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string? Action { get; set; }
    public string? FromValue { get; set; }
    public string? ToValue { get; set; }
    public string? Text { get; set; }

    public JObject ToJson() => JObject.FromObject(this, JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
}

public class ErrorRecord
{
    public const string NOTIFICATION_NONE = "None";
    public const string NOTIFICATION_ACTIVE = "Active";
    public const string NOTIFICATION_ARCHIVED = "Archived";
    public const string NOTIFICATION_FAILED = "Failed";

    public Guid Id { get; set; }
    public long Number { get; set; }
    public string ErrorNumber { get; set; } = string.Empty;
    public Guid ClientRequestId { get; set; }
    public ErrorStatus Status { get; set; }
    public Severity Severity { get; set; }
    public Guid ErrorCodeId { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string CodeTitle { get; set; } = string.Empty;
    public string LocationId { get; set; } = string.Empty;
    public string LocationPath { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Guid ReporterUserId { get; set; }
    public string ReporterName { get; set; } = string.Empty;
    public Guid? AssigneeUserId { get; set; }
    public string AssigneeName { get; set; } = string.Empty;
    public long ReportedAt { get; set; }
    public long LastChangedAt { get; set; }
    public string LastChangedByName { get; set; } = string.Empty;

    /// <summary>0 as long as the error is not in a final status.</summary>
    public long FinalizedAt { get; set; }

    /// <summary>The notification of the error in the Notifications module (FR-24) and its state: None, Active, Archived or Failed.</summary>
    public string NotificationId { get; set; } = string.Empty;
    public string NotificationState { get; set; } = NOTIFICATION_NONE;

    public int Version { get; set; }
    public List<ErrorEvent> Events { get; set; } = [];

    public ErrorRecord Clone()
    {
        var clone = (ErrorRecord)MemberwiseClone();
        clone.Events = Events.Select(e => new ErrorEvent
        {
            Seq = e.Seq,
            EventType = e.EventType,
            At = e.At,
            UserId = e.UserId,
            UserName = e.UserName,
            Action = e.Action,
            FromValue = e.FromValue,
            ToValue = e.ToValue,
            Text = e.Text,
        }).ToList();
        return clone;
    }

    public void AddEvent(string eventType, Guid userId, string userName, long at, string? action = null, string? fromValue = null, string? toValue = null, string? text = null)
    {
        Events.Add(new ErrorEvent
        {
            Seq = Events.Count == 0 ? 1 : Events.Max(e => e.Seq) + 1,
            EventType = eventType,
            At = at,
            UserId = userId,
            UserName = userName,
            Action = action,
            FromValue = fromValue,
            ToValue = toValue,
            Text = text,
        });
    }

    public static string SerializeEvents(IEnumerable<ErrorEvent> events) => new JArray(events.Select(e => e.ToJson())).ToString(Formatting.None);

    public static bool TryParseEvents(string json, [NotNullWhen(true)] out List<ErrorEvent>? events)
    {
        events = null;
        try
        {
            events = JArray.Parse(json).Select(token => token.ToObject<ErrorEvent>()).Where(e => e is not null).Select(e => e!).ToList();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The ErrorSummary of the contract (07 §2.2). The description is only part of the detail view.
    /// </summary>
    public JObject ToSummaryJson(bool includeDescription = false)
    {
        var json = new JObject
        {
            ["ErrorId"] = Id.ToString(),
            ["ErrorNumber"] = ErrorNumber,
            ["Number"] = Number,
            ["Status"] = Status.ToWire(),
            ["Severity"] = Severity.ToWire(),
            ["ErrorCodeId"] = ErrorCodeId.ToString(),
            ["ErrorCode"] = ErrorCode,
            ["CodeTitle"] = CodeTitle,
            ["LocationId"] = LocationId,
            ["LocationPath"] = LocationPath,
            ["ReporterUserId"] = ReporterUserId.ToString(),
            ["ReporterName"] = ReporterName,
            ["AssigneeUserId"] = AssigneeUserId is null ? JValue.CreateNull() : AssigneeUserId.ToString(),
            ["AssigneeName"] = AssigneeName,
            ["ReportedAt"] = ReportedAt,
            ["LastChangedAt"] = LastChangedAt,
            ["LastChangedByName"] = LastChangedByName,
            ["FinalizedAt"] = FinalizedAt,
            ["Version"] = Version,
        };

        if (includeDescription)
        {
            json["Description"] = Description;
        }

        return json;
    }
}
