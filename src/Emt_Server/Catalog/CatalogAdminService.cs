using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Emt.Emt_Server.Errors;
using FSDEV.MOD.Emt.Emt_Server.Security;
using FSDEV.MOD.Emt.Emt_Server.Storage;
using Newtonsoft.Json.Linq;
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

    public CatalogAdminService(CatalogStore catalog, SettingsStore settings, EmtPermissionService permissions, ErrorIndex index, RetentionJob retention, ErrorPushService push, IClock clock)
    {
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

        var result = Catalog.Save(errorCodeId, expectedVersion, code, title, description, instructions, group, defaultSeverity, active, GetUserName(userId), Clock.UtcNowMs);
        if (result.Successful)
        {
            Push.SendErrorCodesChanged(Catalog.Version);
        }

        return result;
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
    /// The health block of the settings page (07 funcGetSettings). The notification and archive entries are fixed values: the Notifications
    /// integration is postponed and there is no archive, all errors stay on ILP (conditions of the Gate 2 approval).
    /// </summary>
    public JObject GetHealth() => new()
    {
        ["ManagersCount"] = Permissions.GetUsersWithPermission(Constants.Permissions.MANAGE_ERRORS).Count,
        ["NotificationsState"] = "Disabled",
        ["PendingNotificationRetries"] = 0,
        ["LastRetentionRun"] = Retention.LastRunAt,
        ["IlpErrorCount"] = Index.Count,
        ["ArchivedErrorCount"] = 0,
        ["ArchiveState"] = "Disabled",
    };

    private string GetUserName(Guid userId) => Permissions.TryGetDisplayName(userId, out var name) ? name : userId.ToString();
}
