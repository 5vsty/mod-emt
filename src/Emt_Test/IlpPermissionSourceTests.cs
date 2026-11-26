using FSDEV.ILP.Intralogistikplattform.SystemState.States;
using FSDEV.ILP.Intralogistikplattform.SystemState.States.CollectionStates;
using FSDEV.ILP.Intralogistikplattform.SystemState.States.PrimitiveStates;
using FSDEV.MOD.Emt.Emt_Server.Security;
using System.Reflection;
using Constants = FSDEV.MOD.Emt.Emt.Constants;

namespace FSDEV.MOD.Emt.Emt_Test;

/// <summary>
/// permissions.json of the other modules has an "Index" next to the scope id of every permission (display order in UserManager).
/// The EMT file has it too, so reading the file must not be disturbed by it.
/// </summary>
[TestClass]
public class IlpPermissionSourceTests
{
    private static readonly Guid Admin = Guid.Parse("bd8f31f8-a52c-4781-a2b3-32e7f2e56060");

    private static DictionaryState<StringState, IBaseState> PermissionFile()
    {
        var file = new DictionaryState<StringState, IBaseState>(Constants.Ilp.Paths.PERMISSIONS);
        file[new StringState("ModuleId")] = new StringState(Constants.Permissions.MODULE_ID);
        file[new StringState("Name")] = new StringState("EMT");

        var permissions = new DictionaryState<StringState, IBaseState>(Guid.NewGuid().ToString());
        var index = 1;
        foreach (var name in new[] { Constants.Permissions.REPORT_ERRORS, Constants.Permissions.MANAGE_ERRORS, Constants.Permissions.MANAGE_CATALOG })
        {
            var permission = new DictionaryState<StringState, IBaseState>(Guid.NewGuid().ToString());
            permission[new StringState("Index")] = new IntState(index++);
            var members = new ListState<IBaseState>(Guid.NewGuid().ToString());
            members.Add(new StringState(Admin.ToString()));
            permission[new StringState(Constants.Permissions.SCOPE_ID)] = members;
            permissions[new StringState(name)] = permission;
        }

        file[new StringState("Permissions")] = permissions;
        return file;
    }

    [TestMethod]
    public void Permission_file_with_Index_is_read_and_grants_the_admin_user()
    {
        var ilp = new FakeIlp();
        ilp.Put(PermissionFile());
        var source = new IlpPermissionSource(ilp);

        typeof(IlpPermissionSource).GetMethod("Reload", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(source, null);

        foreach (var permission in new[] { Constants.Permissions.REPORT_ERRORS, Constants.Permissions.MANAGE_ERRORS, Constants.Permissions.MANAGE_CATALOG })
        {
            Assert.IsTrue(source.TryGetGrantedIds(permission, out var ids), permission);
            CollectionAssert.AreEqual(new[] { Admin }, ids.ToArray(), permission);
        }
    }
}
