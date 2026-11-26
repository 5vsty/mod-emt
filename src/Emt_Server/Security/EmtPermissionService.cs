using FSDEV.Common.Logger;
using Constants = FSDEV.MOD.Emt.Emt.Constants;

namespace FSDEV.MOD.Emt.Emt_Server.Security;

public readonly record struct EmtUser(Guid UserId, string DisplayName);

/// <summary>
/// Where the permission data comes from (permissions.json on ILP and the users and groups of UserManager).
/// </summary>
public interface IPermissionSource
{
    /// <summary>The ids (users or groups) that were granted the permission in the EMT scope.</summary>
    bool TryGetGrantedIds(string permission, out IReadOnlyCollection<Guid> ids);

    IReadOnlyCollection<Guid> GetGroupIdsOfUser(Guid userId);

    bool TryGetGroupMembers(Guid groupId, out IReadOnlyCollection<Guid> members);

    bool TryGetUsers(out IReadOnlyList<EmtUser> users);
}

/// <summary>
/// Evaluates the three EMT permissions. A user has a permission if the user or one of the user's groups is granted it.
/// Missing data (e.g. UserManager not running) means "no permission" (fail closed, R-04).
/// </summary>
public class EmtPermissionService
{
    private const string LOG_PREFIX = $"[{nameof(EmtPermissionService)}]";

    private readonly IPermissionSource Source;

    public EmtPermissionService(IPermissionSource source)
    {
        Source = source;
    }

    public bool HasPermission(Guid userId, string permission)
    {
        if (userId == Guid.Empty)
        {
            return false;
        }

        if (!Source.TryGetGrantedIds(permission, out var grantedIds))
        {
            Logging.Log(LogLevel.Warning, $"{LOG_PREFIX} Permission data for '{permission}' not available, denying user {userId}");
            return false;
        }

        if (grantedIds.Contains(userId))
        {
            return true;
        }

        return Source.GetGroupIdsOfUser(userId).Any(grantedIds.Contains);
    }

    /// <summary>All users that have the permission, directly or through a group.</summary>
    public List<Guid> GetUsersWithPermission(string permission)
    {
        var result = new HashSet<Guid>();

        if (!Source.TryGetGrantedIds(permission, out var grantedIds))
        {
            Logging.Log(LogLevel.Warning, $"{LOG_PREFIX} Permission data for '{permission}' not available, no user has the permission");
            return [];
        }

        foreach (var id in grantedIds)
        {
            if (Source.TryGetGroupMembers(id, out var members))
            {
                result.UnionWith(members);
            }
            else
            {
                _ = result.Add(id);
            }
        }

        return result.ToList();
    }

    public bool TryGetDisplayName(Guid userId, out string displayName)
    {
        displayName = string.Empty;
        if (!Source.TryGetUsers(out var users))
        {
            return false;
        }

        foreach (var user in users)
        {
            if (user.UserId == userId)
            {
                displayName = user.DisplayName;
                return true;
            }
        }

        return false;
    }

    public List<EmtUser> GetUsersWithPermissionDetails(string permission)
    {
        var ids = GetUsersWithPermission(permission).ToHashSet();
        if (ids.Count == 0 || !Source.TryGetUsers(out var users))
        {
            return [];
        }

        return users.Where(user => ids.Contains(user.UserId)).OrderBy(user => user.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public bool HasAnyManager() => GetUsersWithPermission(Constants.Permissions.MANAGE_ERRORS).Count > 0;
}
