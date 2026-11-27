using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Emt.Emt.Validation;
using FSDEV.MOD.Emt.Emt_Server.Errors;
using FSDEV.MOD.Emt.Emt_Server.Security;
using FSDEV.MOD.Emt.Emt_Server.Storage;
using Newtonsoft.Json.Linq;
using System.Diagnostics.CodeAnalysis;
using Constants = FSDEV.MOD.Emt.Emt.Constants;

namespace FSDEV.MOD.Emt.Emt_Server.Catalog;

public record SettingsResult(EmtSettings Settings, JObject Health);

/// <summary>
/// The operations of the catalog and settings page. Needs the Manage-Catalog permission for everything.
/// </summary>
public class CatalogAdminService
{
    private readonly CatalogStore Catalog;
    private readonly SettingsStore Settings;
    private readonly EmtPermissionService Permissions;
    private readonly ErrorIndex Index;
    private readonly RetentionJob Retention;
    private readonly ErrorPushService Push;
    private readonly IClock Clock;
    private readonly string NotificationsState;
    private readonly IErrorCodeGenerator? CodeGenerator;

    /// <param name="notificationsState">Enabled or Disabled, shown in the health block of the settings page.</param>
    /// <param name="codeGenerator">Gives every new error code its code; null = the code is typed by the user (tests, old behaviour).</param>
    public CatalogAdminService(CatalogStore catalog, SettingsStore settings, EmtPermissionService permissions, ErrorIndex index, RetentionJob retention, ErrorPushService push, IClock clock, string notificationsState = "Disabled", IErrorCodeGenerator? codeGenerator = null)
    {
        NotificationsState = notificationsState;
        CodeGenerator = codeGenerator;
        Catalog = catalog;
        Settings = settings;
        Permissions = permissions;
        Index = index;
        Retention = retention;
        Push = push;
        Clock = clock;
    }

    public OperationResult<List<ErrorCode>> GetCodes(Guid userId)
    {
        if (!Permissions.HasPermission(userId, Constants.Permissions.MANAGE_CATALOG))
        {
            return OperationResult<List<ErrorCode>>.Fail(Reasons.FORBIDDEN);
        }

        return OperationResult<List<ErrorCode>>.Ok(Catalog.GetAll());
    }

    public OperationResult<ErrorCode> SaveCode(Guid userId, Guid? errorCodeId, int? expectedVersion, string? code, string? title, string? description,
        string? instructions, string? group, string? defaultSeverity, bool active)
    {
        if (!Permissions.HasPermission(userId, Constants.Permissions.MANAGE_CATALOG))
        {
            return OperationResult<ErrorCode>.Fail(Reasons.FORBIDDEN);
        }

        if (CodeGenerator is not null && !TryGetCode(errorCodeId, title, description, instructions, group, out code, out var failure))
        {
            return failure;
        }

        var result = Catalog.Save(errorCodeId, expectedVersion, code, title, description, instructions, group, defaultSeverity, active, GetUserName(userId), Clock.UtcNowMs);
        if (result.Successful)
        {
            Push.SendErrorCodesChanged(Catalog.Version);
        }

        return result;
    }

    /// <summary>
    /// The code of a new error code is generated, the code of an existing one never changes: what the page sends as the code is ignored.
    /// The other fields are checked first, so a number of the series is not used up by a request that is refused anyway.
    /// </summary>
    private bool TryGetCode(Guid? errorCodeId, string? title, string? description, string? instructions, string? group, out string? code, [NotNullWhen(false)] out OperationResult<ErrorCode>? failure)
    {
        code = null;
        failure = null;

        if (errorCodeId is Guid id)
        {
            var existing = Catalog.GetAll().FirstOrDefault(known => known.Id == id);
            if (existing is null)
            {
                failure = OperationResult<ErrorCode>.Fail(Reasons.NOT_FOUND);
                return false;
            }

            code = existing.Code;
            return true;
        }

        // any valid code stands in for the one that is not there yet
        var errors = ErrorCodeValidator.Validate(IdManagerErrorCodeGenerator.PREFIX, title, description, instructions, group);
        if (errors.Count > 0)
        {
            failure = OperationResult<ErrorCode>.Invalid(errors);
            return false;
        }

        if (!CodeGenerator!.TryGenerate(out code))
        {
            failure = OperationResult<ErrorCode>.Fail(Reasons.UNAVAILABLE);
            return false;
        }

        return true;
    }

    public OperationResult<bool> DeleteCode(Guid userId, Guid errorCodeId)
    {
        if (!Permissions.HasPermission(userId, Constants.Permissions.MANAGE_CATALOG))
        {
            return OperationResult<bool>.Fail(Reasons.FORBIDDEN);
        }

        var result = Catalog.Delete(errorCodeId);
        if (result.Successful)
        {
            Push.SendErrorCodesChanged(Catalog.Version);
        }

        return result;
    }

    public OperationResult<SettingsResult> GetSettings(Guid userId)
    {
        if (!Permissions.HasPermission(userId, Constants.Permissions.MANAGE_CATALOG))
        {
            return OperationResult<SettingsResult>.Fail(Reasons.FORBIDDEN);
        }

        return OperationResult<SettingsResult>.Ok(new SettingsResult(Settings.Current, GetHealth()));
    }

    public OperationResult<SettingsResult> SaveSettings(Guid userId, int retentionDays, bool otherCodeEnabled, int expectedVersion)
    {
        if (!Permissions.HasPermission(userId, Constants.Permissions.MANAGE_CATALOG))
        {
            return OperationResult<SettingsResult>.Fail(Reasons.FORBIDDEN);
        }

        var previous = Settings.Current;
        var result = Settings.Save(retentionDays, otherCodeEnabled, expectedVersion);
        if (!result.Successful)
        {
            return result.Reason == Reasons.CONFLICT
                ? OperationResult<SettingsResult>.Conflicted(result.ConflictByName, new SettingsResult(Settings.Current, GetHealth()))
                : result.ValidationErrors.Count > 0
                    ? OperationResult<SettingsResult>.Invalid(result.ValidationErrors)
                    : OperationResult<SettingsResult>.Fail(result.Reason!);
        }

        if (previous.OtherCodeEnabled != otherCodeEnabled)
        {
            Catalog.NotifyOtherCodeChanged();
            Push.SendErrorCodesChanged(Catalog.Version);
        }

        return OperationResult<SettingsResult>.Ok(new SettingsResult(result.Value!, GetHealth()));
    }

    /// <summary>
    /// The health block of the settings page (07 funcGetSettings). The archive entries are fixed values: there is no archive, all errors stay on ILP
    /// (conditions of the Gate 2 approval). The notifications state follows the module config (Notifications.Enabled).
    /// </summary>
    public JObject GetHealth() => new()
    {
        ["ManagersCount"] = Permissions.GetUsersWithPermission(Constants.Permissions.MANAGE_ERRORS).Count,
        ["NotificationsState"] = NotificationsState,
        ["PendingNotificationRetries"] = 0,
        ["LastRetentionRun"] = Retention.LastRunAt,
        ["IlpErrorCount"] = Index.Count,
        ["ArchivedErrorCount"] = 0,
        ["ArchiveState"] = "Disabled",
    };

    private string GetUserName(Guid userId) => Permissions.TryGetDisplayName(userId, out var name) ? name : userId.ToString();
}
