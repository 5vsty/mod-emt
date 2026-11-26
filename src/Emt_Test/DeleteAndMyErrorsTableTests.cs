using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Emt.Emt_Server.Errors;
using FSDEV.MOD.Emt.Emt_Server.Tables;
using Newtonsoft.Json.Linq;
using Constants = FSDEV.MOD.Emt.Emt.Constants;

namespace FSDEV.MOD.Emt.Emt_Test;

[TestClass]
public class DeleteAndMyErrorsTableTests
{
    // ---- delete ----

    [TestMethod]
    public void UT_DEL_01_A_manager_deletes_an_error_from_ilp_and_the_index_and_the_observers_are_told()
    {
        var env = new TestEnvironment();
        var error = env.Submit(TestEnvironment.Anna);

        var result = env.Errors.Delete(TestEnvironment.Ben, error.Id);

        Assert.IsTrue(result.Successful, result.Reason);
        Assert.IsFalse(env.Index.TryGet(error.Id, out _));
        Assert.IsFalse(env.Store.Records.ContainsKey(error.Id));
        Assert.AreEqual(ErrorChangeType.Deleted, env.Observer.Changes.Last().Type);
        Assert.AreEqual(error.Id, env.Observer.Changes.Last().Error.Id);
    }

    [TestMethod]
    public void UT_DEL_02_Only_managers_may_delete_and_an_unknown_error_is_not_found()
    {
        var env = new TestEnvironment();
        var error = env.Submit(TestEnvironment.Anna);

        Assert.AreEqual(Reasons.FORBIDDEN, env.Errors.Delete(TestEnvironment.Anna, error.Id).Reason, "the reporter may not delete");
        Assert.IsTrue(env.Index.TryGet(error.Id, out _));
        Assert.AreEqual(Reasons.NOT_FOUND, env.Errors.Delete(TestEnvironment.Ben, Guid.NewGuid()).Reason);
    }

    [TestMethod]
    public void UT_DEL_03_A_failed_ilp_delete_keeps_the_error()
    {
        var env = new TestEnvironment();
        var error = env.Submit(TestEnvironment.Anna);
        env.Store.FailWrites = true;

        Assert.AreEqual(Reasons.UNAVAILABLE, env.Errors.Delete(TestEnvironment.Ben, error.Id).Reason);
        Assert.IsTrue(env.Index.TryGet(error.Id, out _));
    }

    // ---- my errors table ----

    [TestMethod]
    public void UT_MY_01_Every_user_has_an_own_stable_table_model_id()
    {
        Assert.AreEqual(MyErrorsTables.GetModelId(TestEnvironment.Anna), MyErrorsTables.GetModelId(TestEnvironment.Anna));
        Assert.AreNotEqual(MyErrorsTables.GetModelId(TestEnvironment.Anna), MyErrorsTables.GetModelId(TestEnvironment.Chris));
        Assert.AreNotEqual(Guid.Parse(Constants.Tables.ERROR_LIST), MyErrorsTables.GetModelId(TestEnvironment.Anna));
    }

    [TestMethod]
    public void UT_MY_02_Get_my_errors_opens_the_users_table_and_returns_its_model_id()
    {
        var env = new TestEnvironment();
        _ = env.Submit(TestEnvironment.Anna);
        var handler = new Emt_Server.MessageHandler.MessageHandler(env.Errors, env.Admin, env.MyErrors);
        handler.RegisterHandlers();

        var context = TestEnvironment.CreateContext(TestEnvironment.Anna, new JObject
        {
            ["ID"] = Constants.Frontend.EMT_GUID,
            [Constants.Frontend.Requests.GET_MY_ERRORS] = "token",
        });
        handler.HandleMessage(context);
        var response = (JObject)context.ResponseQueue.Single()[0];

        var modelId = MyErrorsTables.GetModelId(TestEnvironment.Anna);
        Assert.AreEqual(true, (bool?)response["Successful"]);
        Assert.AreEqual(modelId.ToString(), (string?)response["TableModelId"]);
        Assert.IsTrue(env.Fes.MessageHandlers.ContainsKey(modelId.ToString()), "the table listens for its model id");
    }

    [TestMethod]
    public void UT_MY_03_The_table_holds_the_errors_of_its_user_only()
    {
        var env = new TestEnvironment();
        var own = env.Submit(TestEnvironment.Anna);
        _ = env.Submit(TestEnvironment.Chris);

        var rows = env.MyErrors.GetErrorsOf(TestEnvironment.Anna);

        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(own.Id, rows[0].Id);
    }
}
