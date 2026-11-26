using Newtonsoft.Json.Linq;

namespace FSDEV.MOD.Emt.Emt.Domain;

/// <summary>
/// An entry of the error code catalog (own catalog of EMT, not NotificationTypes).
/// </summary>
public class ErrorCode
{
    public const string SCOPE_ALL = "All";

    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Instructions { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public Severity DefaultSeverity { get; set; } = Severity.Warning;
    public bool Active { get; set; } = true;
    public bool IsOther { get; set; }
    public string Scope { get; set; } = SCOPE_ALL;

    /// <summary>Time of the first use in an error, 0 = never used. A used code cannot be deleted and its code cannot be changed (BR-02, BR-08).</summary>
    public long UsedAt { get; set; }
    public int Version { get; set; } = 1;
    public long ChangedAt { get; set; }
    public string ChangedBy { get; set; } = string.Empty;

    public bool IsUsed => UsedAt > 0;

    public ErrorCode Clone() => (ErrorCode)MemberwiseClone();

    /// <summary>ErrorCodeOption of the contract: what the report form needs.</summary>
    public JObject ToOptionJson() => new()
    {
        ["ErrorCodeId"] = Id.ToString(),
        ["Code"] = Code,
        ["Title"] = Title,
        ["Description"] = Description,
        ["Instructions"] = Instructions,
        ["Group"] = Group,
        ["DefaultSeverity"] = DefaultSeverity.ToWire(),
        ["IsOther"] = IsOther,
    };

    /// <summary>ErrorCodeFull of the contract: what the catalog editor needs.</summary>
    public JObject ToFullJson()
    {
        var json = ToOptionJson();
        json["Active"] = Active;
        json["IsUsed"] = IsUsed;
        json["Scope"] = Scope;
        json["Version"] = Version;
        json["ChangedAt"] = ChangedAt;
        json["ChangedBy"] = ChangedBy;
        return json;
    }
}

public class EmtSettings
{
    public int RetentionDays { get; set; } = Constants.Limits.DEFAULT_RETENTION_DAYS;
    public bool OtherCodeEnabled { get; set; } = true;
    public int Version { get; set; } = 1;

    public JObject ToJson() => new()
    {
        ["RetentionDays"] = RetentionDays,
        ["OtherCodeEnabled"] = OtherCodeEnabled,
        ["Version"] = Version,
    };
}
