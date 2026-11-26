using Newtonsoft.Json.Linq;

namespace FSDEV.MOD.Emt.Emt.Validation;

public readonly record struct ValidationError(string Field, string MessageKey)
{
    public JObject ToJson() => new() { ["Field"] = Field, ["MessageKey"] = MessageKey };
}

public static class ValidationKeys
{
    public const string REQUIRED = "EMT_VALIDATION_REQUIRED";
    public const string TOO_LONG = "EMT_VALIDATION_TOO_LONG";
    public const string INVALID = "EMT_VALIDATION_INVALID";
    public const string TOO_SMALL = "EMT_VALIDATION_TOO_SMALL";
}

public static class ErrorReportValidator
{
    /// <summary>
    /// Validates a report (FR-07..FR-09). <paramref name="isOtherCode"/> makes the description mandatory (BR-22).
    /// </summary>
    public static List<ValidationError> Validate(string? locationId, Guid errorCodeId, bool isOtherCode, string? description, bool locationRequired = true)
    {
        var errors = new List<ValidationError>();

        if (locationRequired && string.IsNullOrWhiteSpace(locationId))
        {
            errors.Add(new("LocationId", ValidationKeys.REQUIRED));
        }

        if (errorCodeId == Guid.Empty)
        {
            errors.Add(new("ErrorCodeId", ValidationKeys.REQUIRED));
        }

        if (isOtherCode && string.IsNullOrWhiteSpace(description))
        {
            errors.Add(new("Description", ValidationKeys.REQUIRED));
        }

        if (description is not null && description.Length > Constants.Limits.MAX_DESCRIPTION_LENGTH)
        {
            errors.Add(new("Description", ValidationKeys.TOO_LONG));
        }

        return errors;
    }

    public static List<ValidationError> ValidateText(string? text, bool required, int maxLength, string field = "Text")
    {
        var errors = new List<ValidationError>();
        if (required && string.IsNullOrWhiteSpace(text))
        {
            errors.Add(new(field, ValidationKeys.REQUIRED));
        }

        if (text is not null && text.Length > maxLength)
        {
            errors.Add(new(field, ValidationKeys.TOO_LONG));
        }

        return errors;
    }
}

public static class ErrorCodeValidator
{
    public static List<ValidationError> Validate(string? code, string? title, string? description, string? instructions, string? group)
    {
        var errors = new List<ValidationError>();

        if (string.IsNullOrWhiteSpace(code))
        {
            errors.Add(new("Code", ValidationKeys.REQUIRED));
        }
        else if (code.Trim().Length > Constants.Limits.MAX_CODE_LENGTH)
        {
            errors.Add(new("Code", ValidationKeys.TOO_LONG));
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            errors.Add(new("Title", ValidationKeys.REQUIRED));
        }
        else if (title.Trim().Length > Constants.Limits.MAX_TITLE_LENGTH)
        {
            errors.Add(new("Title", ValidationKeys.TOO_LONG));
        }

        if (description is not null && description.Trim().Length > Constants.Limits.MAX_TEXT_FIELD_LENGTH)
        {
            errors.Add(new("Description", ValidationKeys.TOO_LONG));
        }

        if (instructions is not null && instructions.Trim().Length > Constants.Limits.MAX_TEXT_FIELD_LENGTH)
        {
            errors.Add(new("Instructions", ValidationKeys.TOO_LONG));
        }

        if (group is not null && group.Length > Constants.Limits.MAX_GROUP_LENGTH)
        {
            errors.Add(new("Group", ValidationKeys.TOO_LONG));
        }

        return errors;
    }
}

public static class SettingsValidator
{
    public static List<ValidationError> Validate(int retentionDays)
    {
        var errors = new List<ValidationError>();
        if (retentionDays < Constants.Limits.MIN_RETENTION_DAYS)
        {
            errors.Add(new("RetentionDays", ValidationKeys.TOO_SMALL));
        }

        return errors;
    }
}
