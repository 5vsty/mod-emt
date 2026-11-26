using FSDEV.FES.Server.Client;
using FSDEV.FES.Server.Client.Sessions;
using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Emt.Emt_Server;
using FSDEV.MOD.Emt.Emt_Server.Security;
using Newtonsoft.Json.Linq;
using System.Reflection;
using Constants = FSDEV.MOD.Emt.Emt.Constants;

namespace FSDEV.MOD.Emt.Emt_Test;

[TestClass]
public class SecurityTests
{
    private const string TABLE_ID = "6e66699f-b266-4989-bbdc-4a4fc40a05df";

    private static JObject Request(string function, params (string Key, JToken Value)[] fields)
    {
        var message = new JObject { ["ID"] = Constants.Frontend.EMT_GUID, [function] = "token" };
        foreach (var (key, value) in fields)
        {
            message[key] = value;
        }

        return message;
    }

    /// <summary>A valid request for every function of the contract, so a refusal can only come from the permission check.</summary>
    private static Dictionary<string, JObject> ValidRequests(Guid errorId, Guid codeId) => new()
    {
        [Constants.Frontend.Requests.GET_REPORT_FORM_DATA] = Request(Constants.Frontend.Requests.GET_REPORT_FORM_DATA),
        [Constants.Frontend.Requests.SUBMIT_ERROR] = Request(Constants.Frontend.Requests.SUBMIT_ERROR, ("ClientRequestId", Guid.NewGuid().ToString()), ("LocationId", TestEnvironment.Hall1), ("ErrorCodeId", codeId.ToString())),
        [Constants.Frontend.Requests.GET_MY_ERRORS] = Request(Constants.Frontend.Requests.GET_MY_ERRORS),
        [Constants.Frontend.Requests.CANCEL_ERROR] = Request(Constants.Frontend.Requests.CANCEL_ERROR, ("ErrorId", errorId.ToString()), ("ExpectedVersion", 1), ("Text", "x")),
        [Constants.Frontend.Requests.DELETE_ERROR] = Request(Constants.Frontend.Requests.DELETE_ERROR, ("ErrorId", errorId.ToString())),
        [Constants.Frontend.Requests.GET_ERROR_DETAIL] = Request(Constants.Frontend.Requests.GET_ERROR_DETAIL, ("ErrorId", errorId.ToString())),
        [Constants.Frontend.Requests.CHANGE_ERROR_STATUS] = Request(Constants.Frontend.Requests.CHANGE_ERROR_STATUS, ("ErrorId", errorId.ToString()), ("Action", "Resolve"), ("ExpectedVersion", 1), ("Text", "x")),
        [Constants.Frontend.Requests.ASSIGN_ERROR] = Request(Constants.Frontend.Requests.ASSIGN_ERROR, ("ErrorId", errorId.ToString()), ("AssigneeUserId", TestEnvironment.Ben.ToString()), ("ExpectedVersion", 1)),
        [Constants.Frontend.Requests.CHANGE_ERROR_SEVERITY] = Request(Constants.Frontend.Requests.CHANGE_ERROR_SEVERITY, ("ErrorId", errorId.ToString()), ("Severity", "Critical"), ("ExpectedVersion", 1)),
        [Constants.Frontend.Requests.ADD_ERROR_NOTE] = Request(Constants.Frontend.Requests.ADD_ERROR_NOTE, ("ErrorId", errorId.ToString()), ("Text", "x")),
        [Constants.Frontend.Requests.GET_ASSIGNABLE_USERS] = Request(Constants.Frontend.Requests.GET_ASSIGNABLE_USERS),
        [Constants.Frontend.Requests.GET_ERROR_CODES] = Request(Constants.Frontend.Requests.GET_ERROR_CODES),
        [Constants.Frontend.Requests.SAVE_ERROR_CODE] = Request(Constants.Frontend.Requests.SAVE_ERROR_CODE, ("Code", "NEW-1"), ("Title", "a"), ("DefaultSeverity", "Error"), ("Active", true)),
        [Constants.Frontend.Requests.DELETE_ERROR_CODE] = Request(Constants.Frontend.Requests.DELETE_ERROR_CODE, ("ErrorCodeId", codeId.ToString())),
        [Constants.Frontend.Requests.GET_SETTINGS] = Request(Constants.Frontend.Requests.GET_SETTINGS),
        [Constants.Frontend.Requests.SAVE_SETTINGS] = Request(Constants.Frontend.Requests.SAVE_SETTINGS, ("RetentionDays", 365), ("OtherCodeEnabled", true), ("ExpectedVersion", 1)),
    };

    private static JObject Send(Emt_Server.MessageHandler.MessageHandler handler, Guid userId, JObject message)
    {
        var context = TestEnvironment.CreateContext(userId, message);
        handler.HandleMessage(context);
        return (JObject)context.ResponseQueue.Single()[0];
    }

    private static Emt_Server.MessageHandler.MessageHandler CreateHandler(TestEnvironment env)
    {
        var handler = new Emt_Server.MessageHandler.MessageHandler(env.Errors, env.Admin, env.MyErrors);
        handler.RegisterHandlers();
        return handler;
    }

    private static string[] AllFunctions() => typeof(Constants.Frontend.Requests)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (string)field.GetRawConstantValue()!)
        .ToArray();

    [TestMethod]
    public void UT_SEC_03_Every_function_is_refused_for_a_user_without_any_emt_permission()
    {
        var env = new TestEnvironment();
        var error = env.Submit(TestEnvironment.Anna);
        var handler = CreateHandler(env);
        var requests = ValidRequests(error.Id, env.ScanCode);
        CollectionAssert.AreEquivalent(AllFunctions(), requests.Keys.ToArray(), "the test covers every function of the contract");
        var created = env.Store.CreateCalls;

        foreach (var (function, message) in requests)
        {
            var response = Send(handler, TestEnvironment.Dora, message);

            Assert.IsFalse((bool)response["Successful"]!, function);
            Assert.AreEqual(Reasons.FORBIDDEN, (string?)response["Reason"], function);
            Assert.IsNull(response["Error"], $"{function} must not return error data");
            Assert.IsNull(response["Events"], function);
        }

        Assert.AreEqual(created, env.Store.CreateCalls);
        Assert.AreEqual(1, env.Index.Count);
        Assert.IsTrue(env.Index.TryGet(error.Id, out var unchanged));
        Assert.AreEqual(ErrorStatus.New, unchanged.Status, "AC-57: the error did not change");
        Assert.AreEqual(1, unchanged.Version);
    }

    [TestMethod]
    public void UT_SEC_03_Every_function_checks_the_permission_it_needs()
    {
        var env = new TestEnvironment();
        var error = env.Submit(TestEnvironment.Chris);
        var handler = CreateHandler(env);
        var requests = ValidRequests(error.Id, env.GenCode);

        var report = new[] { Constants.Frontend.Requests.GET_REPORT_FORM_DATA, Constants.Frontend.Requests.SUBMIT_ERROR, Constants.Frontend.Requests.GET_MY_ERRORS, Constants.Frontend.Requests.CANCEL_ERROR };
        var manage = new[] { Constants.Frontend.Requests.CHANGE_ERROR_STATUS, Constants.Frontend.Requests.ASSIGN_ERROR, Constants.Frontend.Requests.CHANGE_ERROR_SEVERITY, Constants.Frontend.Requests.ADD_ERROR_NOTE, Constants.Frontend.Requests.GET_ASSIGNABLE_USERS, Constants.Frontend.Requests.DELETE_ERROR };
        var catalog = new[] { Constants.Frontend.Requests.GET_ERROR_CODES, Constants.Frontend.Requests.SAVE_ERROR_CODE, Constants.Frontend.Requests.DELETE_ERROR_CODE, Constants.Frontend.Requests.GET_SETTINGS, Constants.Frontend.Requests.SAVE_SETTINGS };

        // anna only reports: no management, no catalog
        foreach (var function in manage.Concat(catalog))
        {
            Assert.AreEqual(Reasons.FORBIDDEN, (string?)Send(handler, TestEnvironment.Anna, requests[function])["Reason"], $"anna {function}");
        }

        // ben only manages errors: no reporting, no catalog
        foreach (var function in report.Concat(catalog))
        {
            Assert.AreEqual(Reasons.FORBIDDEN, (string?)Send(handler, TestEnvironment.Ben, requests[function])["Reason"], $"ben {function}");
        }

        // fay only manages the catalog
        foreach (var function in report.Concat(manage).Concat(new[] { Constants.Frontend.Requests.GET_ERROR_DETAIL }))
        {
            Assert.AreEqual(Reasons.FORBIDDEN, (string?)Send(handler, TestEnvironment.Fay, requests[function])["Reason"], $"fay {function}");
        }

        Assert.AreEqual(1, env.Index.Count);
    }

    [TestMethod]
    public void UT_SEC_01_Detail_is_for_managers_and_the_reporter_of_the_error_only()
    {
        var env = new TestEnvironment();
        var error = env.Submit(TestEnvironment.Anna);

        var asManager = env.Errors.GetDetail(TestEnvironment.Ben, error.Id);
        Assert.IsTrue(asManager.Successful);
        CollectionAssert.AreEquivalent(new[] { ErrorAction.Acknowledge, ErrorAction.StartProgress, ErrorAction.Resolve, ErrorAction.Reject }, asManager.Value!.AllowedActions);

        var asReporter = env.Errors.GetDetail(TestEnvironment.Anna, error.Id);
        Assert.IsTrue(asReporter.Successful);
        CollectionAssert.AreEqual(new[] { ErrorAction.Cancel }, asReporter.Value!.AllowedActions, "read only, apart from cancelling the own new error");

        Assert.AreEqual(Reasons.FORBIDDEN, env.Errors.GetDetail(TestEnvironment.Chris, error.Id).Reason, "an error of somebody else");
        Assert.AreEqual(Reasons.FORBIDDEN, env.Errors.GetDetail(TestEnvironment.Dora, error.Id).Reason);
        Assert.AreEqual(Reasons.NOT_FOUND, env.Errors.GetDetail(TestEnvironment.Ben, Guid.NewGuid()).Reason);

        var hana = env.Errors.GetDetail(TestEnvironment.Hana, error.Id).Value!;
        Assert.IsFalse(hana.AllowedActions.Contains(ErrorAction.Cancel), "only the reporter cancels, not a user who may report");
    }

    [TestMethod]
    public void UT_SEC_02_Only_the_reporter_cancels_and_only_while_the_error_is_new()
    {
        var env = new TestEnvironment();
        var error = env.Submit(TestEnvironment.Anna);

        Assert.AreEqual(Reasons.NOT_REPORTER, env.Errors.Cancel(TestEnvironment.Chris, error.Id, error.Version, "x").Reason);
        Assert.AreEqual(Reasons.FORBIDDEN, env.Errors.Cancel(TestEnvironment.Ben, error.Id, error.Version, "x").Reason, "ben has no report permission");
        Assert.IsTrue(env.Index.TryGet(error.Id, out var stored));
        Assert.AreEqual(ErrorStatus.New, stored.Status);
    }

    [TestMethod]
    public void UT_SEC_Permissions_come_from_users_and_from_groups()
    {
        var env = new TestEnvironment();
        var group = Guid.NewGuid();
        env.Source.Grant(Constants.Permissions.MANAGE_ERRORS, group);
        env.Source.GroupMembers[group] = [TestEnvironment.Dora];
        env.Source.GroupsOfUser[TestEnvironment.Dora] = [group];

        Assert.IsTrue(env.Permissions.HasPermission(TestEnvironment.Dora, Constants.Permissions.MANAGE_ERRORS));
        Assert.IsFalse(env.Permissions.HasPermission(TestEnvironment.Dora, Constants.Permissions.MANAGE_CATALOG));
        CollectionAssert.Contains(env.Permissions.GetUsersWithPermission(Constants.Permissions.MANAGE_ERRORS), TestEnvironment.Dora);
        CollectionAssert.DoesNotContain(env.Permissions.GetUsersWithPermission(Constants.Permissions.MANAGE_ERRORS), group, "the group id itself is not a user");

        env.Source.Revoke(Constants.Permissions.MANAGE_ERRORS, group);
        Assert.IsFalse(env.Permissions.HasPermission(TestEnvironment.Dora, Constants.Permissions.MANAGE_ERRORS), "revoking takes effect at once");
    }

    [TestMethod]
    public void UT_SEC_Without_permission_data_everybody_is_refused()
    {
        var env = new TestEnvironment();
        env.Source.Available = false;

        Assert.IsFalse(env.Permissions.HasPermission(TestEnvironment.Hana, Constants.Permissions.MANAGE_ERRORS));
        Assert.AreEqual(0, env.Permissions.GetUsersWithPermission(Constants.Permissions.MANAGE_ERRORS).Count);
        Assert.AreEqual(Reasons.FORBIDDEN, env.Errors.GetReportFormData(TestEnvironment.Hana).Reason);
        Assert.IsFalse(env.Permissions.HasPermission(Guid.Empty, Constants.Permissions.REPORT_ERRORS));
    }

    // ---- AuthorizingFesClient (UT-SEC-04) ----

    private static (WsMessageContext<Session, Websocket> Context, Guid WebsocketId) CreateTableContext(Guid userId, string function, string modelId)
    {
        var session = new Session(Guid.NewGuid(), userId, "test");
        var websocket = new Websocket();
        websocket.InitializeWebsocket(session.SessionId, Guid.NewGuid());
        var message = new JObject { ["ID"] = TABLE_ID, [function] = modelId };
        return (new WsMessageContext<Session, Websocket>(session, websocket, new JArray(message)), websocket.WebsocketId);
    }

    [TestMethod]
    public void UT_SEC_04_Table_requests_only_reach_the_table_for_users_with_the_permission()
    {
        var env = new TestEnvironment();
        var fes = new FakeFesClient();
        var client = new AuthorizingFesClient(fes, Guid.Parse(Constants.Tables.ERROR_LIST), userId => env.Permissions.HasPermission(userId, Constants.Permissions.MANAGE_ERRORS));
        var received = new List<Guid>();
        Assert.IsTrue(client.TryAddMessageHandler(Constants.Tables.ERROR_LIST, (in WsMessageContext<Session, Websocket> context) => received.Add(context.session.UserGuid)));

        var forBen = CreateTableContext(TestEnvironment.Ben, "funcRequestEntries", Constants.Tables.ERROR_LIST);
        var forDora = CreateTableContext(TestEnvironment.Dora, "funcRequestEntries", Constants.Tables.ERROR_LIST);
        var forAnna = CreateTableContext(TestEnvironment.Anna, "funcRequestCsv", Constants.Tables.ERROR_LIST);
        fes.MessageHandlers[Constants.Tables.ERROR_LIST](forBen.Context);
        fes.MessageHandlers[Constants.Tables.ERROR_LIST](forDora.Context);
        fes.MessageHandlers[Constants.Tables.ERROR_LIST](forAnna.Context);

        CollectionAssert.AreEqual(new[] { TestEnvironment.Ben }, received);
    }

    [TestMethod]
    public void UT_SEC_04_Every_table_function_is_checked_and_other_messages_pass()
    {
        var env = new TestEnvironment();
        var fes = new FakeFesClient();
        var client = new AuthorizingFesClient(fes, Guid.Parse(Constants.Tables.ERROR_LIST), userId => env.Permissions.HasPermission(userId, Constants.Permissions.MANAGE_ERRORS));
        var received = 0;
        _ = client.TryAddMessageHandler("table", (in WsMessageContext<Session, Websocket> _) => received++);

        foreach (var function in new[] { "funcRequestEntries", "funcRequestCsv", "funcStopUpdates", "funcCancelQuery" })
        {
            fes.MessageHandlers["table"](CreateTableContext(TestEnvironment.Dora, function, Constants.Tables.ERROR_LIST).Context);
        }

        Assert.AreEqual(0, received, "all four table functions are refused for dora");

        // a request for another table model is not ours to judge; the other table has its own client
        fes.MessageHandlers["table"](CreateTableContext(TestEnvironment.Dora, "funcRequestEntries", Constants.Tables.ERROR_CODES).Context);
        Assert.AreEqual(1, received);
    }

    [TestMethod]
    public void UT_SEC_04_Data_is_only_sent_to_websockets_of_users_who_still_have_the_permission()
    {
        var env = new TestEnvironment();
        var fes = new FakeFesClient();
        var client = new AuthorizingFesClient(fes, Guid.Parse(Constants.Tables.ERROR_LIST), userId => env.Permissions.HasPermission(userId, Constants.Permissions.MANAGE_ERRORS));
        _ = client.TryAddMessageHandler("table", (in WsMessageContext<Session, Websocket> _) => { });
        _ = client.TryAddSessionRemovedHandler("table", (_, _) => { });

        var ben = CreateTableContext(TestEnvironment.Ben, "funcRequestEntries", Constants.Tables.ERROR_LIST);
        fes.MessageHandlers["table"](ben.Context);

        Assert.IsTrue(client.TrySendToWebsocket(ben.WebsocketId, new JArray()), "ben asked and has the permission");
        Assert.IsFalse(client.TrySendToWebsocket(Guid.NewGuid(), new JArray()), "a websocket that never asked");
        Assert.AreEqual(1, fes.SentToWebsockets.Count);

        env.Source.Revoke(Constants.Permissions.MANAGE_ERRORS, TestEnvironment.Ben);
        Assert.IsFalse(client.TrySendToWebsocket(ben.WebsocketId, new JArray()), "the permission was revoked");
        Assert.AreEqual(1, fes.SentToWebsockets.Count);

        env.Source.Grant(Constants.Permissions.MANAGE_ERRORS, TestEnvironment.Ben);
        Assert.IsFalse(client.TrySendToWebsocket(ben.WebsocketId, new JArray()), "after a loss the websocket has to ask again");

        var again = CreateTableContext(TestEnvironment.Ben, "funcRequestEntries", Constants.Tables.ERROR_LIST);
        fes.MessageHandlers["table"](again.Context);
        Assert.IsTrue(client.TrySendToWebsocket(again.WebsocketId, new JArray()));

        fes.SessionRemovedHandlers["table"](again.Context.session, again.Context.websocket);
        Assert.IsFalse(client.TrySendToWebsocket(again.WebsocketId, new JArray()), "the closed websocket is forgotten");
    }
}
