using FSDEV.Common.Logger;
using FSDEV.ILP.Intralogistikplattform.SystemState;
using FSDEV.ILP.Intralogistikplattform.SystemState.Client;
using FSDEV.ILP.Intralogistikplattform.SystemState.States;
using FSDEV.ILP.Intralogistikplattform.SystemState.States.CollectionStates;
using FSDEV.ILP.Intralogistikplattform.SystemState.States.PrimitiveStates;
using System.Collections.Immutable;
using Constants = FSDEV.MOD.Emt.Emt.Constants;
using UserManagerClient = FSDEV.MOD.UserManager.UserManager_Client.UserManager_Client;

namespace FSDEV.MOD.Emt.Emt_Server.Security;

/// <summary>
/// Reads Config/Modules/Emt/permissions.json (kept up to date through a subscription) and the groups and users of UserManager.
/// The layout of the file is the one UserManager expects: Permissions → permission → scope id → list of user and group ids.
/// </summary>
public class IlpPermissionSource : IPermissionSource
{
    private const string LOG_PREFIX = $"[{nameof(IlpPermissionSource)}]";
    private const string PERMISSIONS = "Permissions";

    private readonly IClientController Ilp;
    private readonly object ClientLock = new();

    private UserManagerClient? UserManager;
    private ImmutableDictionary<string, IReadOnlyCollection<Guid>> GrantedIds = ImmutableDictionary<string, IReadOnlyCollection<Guid>>.Empty;
    private bool PermissionsLoaded;

    public IlpPermissionSource(IClientController ilp)
    {
        Ilp = ilp;
    }

    public bool Initialize()
    {
        var result = Ilp.RegisterSubscription(Constants.Ilp.Paths.PERMISSIONS, (List<Change> _) => Reload(), true, out _);
        if (result != ExecutionResponse.SUCCEEDED)
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Cannot subscribe to {Constants.Ilp.Paths.PERMISSIONS}: {result}");
            return false;
        }

        Reload();
        _ = TryGetUserManager(out _);
        return true;
    }

    public bool TryGetGrantedIds(string permission, out IReadOnlyCollection<Guid> ids)
    {
        ids = [];
        if (!PermissionsLoaded)
        {
            return false;
        }

        // a permission that is not in the file is granted to nobody
        ids = GrantedIds.TryGetValue(permission, out var granted) ? granted : [];
        return true;
    }

    public IReadOnlyCollection<Guid> GetGroupIdsOfUser(Guid userId) =>
        TryGetUserManager(out var client) ? client.GetGroupsForUser(userId).Select(group => group.GroupId).ToList() : [];

    public bool TryGetGroupMembers(Guid groupId, out IReadOnlyCollection<Guid> members)
    {
        members = [];
        if (!TryGetUserManager(out var client) || !client.TryGetGroup(groupId, out var group))
        {
            return false;
        }

        members = group.Members.ToList();
        return true;
    }

    public bool TryGetUsers(out IReadOnlyList<EmtUser> users)
    {
        users = [];
        if (!TryGetUserManager(out var client) || !client.TryGetUsers(out var allUsers))
        {
            return false;
        }

        users = allUsers.Select(user => new EmtUser(user.UserId, GetDisplayName(user.FirstName, user.Surname, user.Alias))).ToList();
        return true;
    }

    public static string GetDisplayName(string firstName, string surname, string alias)
    {
        var name = $"{firstName} {surname}".Trim();
        return name.Length > 0 ? name : alias;
    }

    private bool TryGetUserManager(out UserManagerClient client)
    {
        lock (ClientLock)
        {
            if (UserManager is null && !UserManagerClient.TryCreateUserManagerClient(Ilp, out UserManager))
            {
                Logging.Log(LogLevel.Warning, $"{LOG_PREFIX} UserManager is not available, all permission checks are denied until it is");
            }

            client = UserManager!;
            return UserManager is not null;
        }
    }

    private void Reload()
    {
        if (!Ilp.TryGetState(Constants.Ilp.Paths.PERMISSIONS, out var state) || state is not DictionaryState<StringState, IBaseState> file)
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} {Constants.Ilp.Paths.PERMISSIONS} not found or not a dictState");
            GrantedIds = ImmutableDictionary<string, IReadOnlyCollection<Guid>>.Empty;
            PermissionsLoaded = false;
            return;
        }

        var result = ImmutableDictionary.CreateBuilder<string, IReadOnlyCollection<Guid>>();
        if (file.TryGetValue(new StringState(PERMISSIONS), out var permissionsState) && permissionsState is DictionaryState<StringState, IBaseState> permissions)
        {
            foreach (var (permissionName, permissionState) in permissions)
            {
                if (permissionState is not DictionaryState<StringState, IBaseState> byScope
                    || !byScope.TryGetValue(new StringState(Constants.Permissions.SCOPE_ID), out var membersState)
                    || membersState is not ListState<IBaseState> members)
                {
                    continue;
                }

                result[permissionName.Value] = members.OfType<StringState>().Select(member => Guid.TryParse(member.Value, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToList();
            }
        }

        GrantedIds = result.ToImmutable();
        PermissionsLoaded = true;
    }
}
