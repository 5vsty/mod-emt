using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Emt.Emt.Validation;
using FSDEV.MOD.Emt.Emt_Server.Errors;
using Constants = FSDEV.MOD.Emt.Emt.Constants;

namespace FSDEV.MOD.Emt.Emt_Test;

[TestClass]
public class ErrorServiceTests
{
    [TestMethod]
    public void UT_ERR_01_Submit_creates_a_new_error_with_code_severity_location_and_the_reporter_of_the_session()
    {
        var env = new TestEnvironment();

        var result = env.Errors.Submit(TestEnvironment.Anna, Guid.NewGuid(), TestEnvironment.Hall1, env.ScanCode, "Scanner does not read the code");

        Assert.IsTrue(result.Successful, result.Reason);
        var error = result.Value!.Error;
        Assert.IsFalse(result.Value.Duplicate);
        Assert.AreEqual(ErrorStatus.New, error.Status);
        Assert.AreEqual(Severity.Warning, error.Severity, "severity of the code");
        Assert.AreEqual("SCAN-01", error.ErrorCode);
        Assert.AreEqual("Hall 1 / Wareneingang", error.LocationPath);
        Assert.AreEqual(TestEnvironment.Hall1, error.LocationId);
        Assert.AreEqual(TestEnvironment.Anna, error.ReporterUserId);
        Assert.AreEqual("anna", error.ReporterName);
        Assert.AreEqual(1, error.Version);
        Assert.IsTrue(error.ReportedAt > 0);
        Assert.AreEqual(1, error.Events.Count);
        Assert.AreEqual(ErrorEventTypes.CREATED, error.Events[0].EventType);
        Assert.IsTrue(env.Store.Records.ContainsKey(error.Id), "written to the store");
        Assert.IsTrue(env.Index.TryGet(error.Id, out _), "in the index");
    }

    [TestMethod]
    public void UT_SEQ_01_Numbers_count_up_and_the_counter_is_written_together_with_the_error()
    {
        var env = new TestEnvironment();

        var first = env.Submit();
        var second = env.Submit(TestEnvironment.Chris);

        Assert.AreEqual("EMT-000001", first.ErrorNumber);
        Assert.AreEqual("EMT-000002", second.ErrorNumber);
        CollectionAssert.AreEqual(new[] { ("EMT-000001", 1L), ("EMT-000002", 2L) }, env.Store.Created.ToArray());
        Assert.AreEqual(2, env.Store.Sequence);
    }

    [TestMethod]
    public void UT_SEQ_01_A_failed_write_does_not_use_up_a_number()
    {
        var env = new TestEnvironment();
        env.Store.FailWrites = true;

        var failed = env.Errors.Submit(TestEnvironment.Anna, Guid.NewGuid(), TestEnvironment.Hall1, env.ScanCode, "x");
        Assert.AreEqual(Reasons.UNAVAILABLE, failed.Reason);
        Assert.AreEqual(0, env.Index.Count, "nothing is shown that was not stored");

        env.Store.FailWrites = false;
        var next = env.Submit();
        Assert.AreEqual("EMT-000001", next.ErrorNumber);
    }

    [TestMethod]
    public void UT_SEQ_01_After_a_restart_the_counter_is_never_behind_the_highest_number()
    {
        var env = new TestEnvironment();
        _ = env.Submit();
        _ = env.Submit();
        env.Store.Sequence = 0; // counter lost or behind

        env.Build();
        var next = env.Submit();

        Assert.AreEqual("EMT-000003", next.ErrorNumber);
    }

    [TestMethod]
    public void UT_ERR_02_Reporter_comes_from_the_session_not_from_the_payload()
    {
        var env = new TestEnvironment();
        var handler = new Emt_Server.MessageHandler.MessageHandler(env.Errors, env.Admin, env.MyErrors);
        handler.RegisterHandlers();

        var context = TestEnvironment.CreateContext(TestEnvironment.Anna, new Newtonsoft.Json.Linq.JObject
        {
            ["ID"] = Constants.Frontend.EMT_GUID,
            [Constants.Frontend.Requests.SUBMIT_ERROR] = "token",
            ["ClientRequestId"] = Guid.NewGuid().ToString(),
            ["LocationId"] = TestEnvironment.Hall1,
            ["ErrorCodeId"] = env.ScanCode.ToString(),
            ["Description"] = "x",
            ["ReporterUserId"] = TestEnvironment.Ben.ToString(),
            ["ReporterName"] = "ben",
        });
        handler.HandleMessage(context);

        var response = (Newtonsoft.Json.Linq.JObject)context.ResponseQueue.Single()[0];
        Assert.IsTrue((bool)response["Successful"]!);
        Assert.IsTrue(env.Index.TryGet(Guid.Parse((string)response["ErrorId"]!), out var stored));
        Assert.AreEqual(TestEnvironment.Anna, stored.ReporterUserId);
        Assert.AreEqual("anna", stored.ReporterName);
    }

    [TestMethod]
    public void UT_ERR_03_Submit_without_any_error_manager_still_creates_the_error_and_says_so()
    {
        var env = new TestEnvironment();
        env.Source.Granted[Constants.Permissions.MANAGE_ERRORS].Clear();
        env.Source.Revoke(Constants.Permissions.MANAGE_ERRORS, TestEnvironment.Hana);

        var result = env.Errors.Submit(TestEnvironment.Anna, Guid.NewGuid(), TestEnvironment.Hall1, env.ScanCode, "x");

        Assert.IsTrue(result.Successful, result.Reason);
        Assert.IsTrue(result.Value!.NoManagersConfigured);
        Assert.AreEqual(1, env.Index.Count);
    }

    [TestMethod]
    public void UT_ERR_04_Resent_request_returns_the_same_error()
    {
        var env = new TestEnvironment();
        var requestId = Guid.NewGuid();

        var first = env.Errors.Submit(TestEnvironment.Anna, requestId, TestEnvironment.Hall1, env.ScanCode, "x");
        var second = env.Errors.Submit(TestEnvironment.Anna, requestId, TestEnvironment.Hall1, env.ScanCode, "x");

        Assert.IsFalse(first.Value!.Duplicate);
        Assert.IsTrue(second.Value!.Duplicate);
        Assert.AreEqual(first.Value.Error.ErrorNumber, second.Value.Error.ErrorNumber);
        Assert.AreEqual(1, env.Store.CreateCalls);
        Assert.AreEqual(1, env.Observer.Changes.Count, "no second push");
    }

    [TestMethod]
    public void UT_ERR_04_Two_parallel_requests_with_the_same_id_create_one_error()
    {
        var env = new TestEnvironment();
        var requestId = Guid.NewGuid();
        using var start = new ManualResetEventSlim(false);

        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return env.Errors.Submit(TestEnvironment.Anna, requestId, TestEnvironment.Hall1, env.ScanCode, "x");
        })).ToArray();
        start.Set();
        Task.WaitAll(tasks);

        Assert.AreEqual(1, env.Store.CreateCalls);
        Assert.AreEqual(1, tasks.Count(t => !t.Result.Value!.Duplicate));
    }

    [TestMethod]
    public void UT_ERR_11_After_a_restart_a_resent_request_is_still_recognized()
    {
        var env = new TestEnvironment();
        var requestId = Guid.NewGuid();
        var first = env.Errors.Submit(TestEnvironment.Anna, requestId, TestEnvironment.Hall1, env.ScanCode, "x").Value!;

        env.Build();
        var second = env.Errors.Submit(TestEnvironment.Anna, requestId, TestEnvironment.Hall1, env.ScanCode, "x").Value!;

        Assert.IsTrue(second.Duplicate);
        Assert.AreEqual(first.Error.Id, second.Error.Id);
        Assert.AreEqual(1, env.Store.CreateCalls);
    }

    [TestMethod]
    public void UT_ERR_04_A_request_id_of_another_user_is_not_accepted()
    {
        var env = new TestEnvironment();
        var requestId = Guid.NewGuid();
        _ = env.Errors.Submit(TestEnvironment.Anna, requestId, TestEnvironment.Hall1, env.ScanCode, "x");

        var result = env.Errors.Submit(TestEnvironment.Chris, requestId, TestEnvironment.Hall1, env.ScanCode, "x");

        Assert.AreEqual(Reasons.FORBIDDEN, result.Reason);
    }

    [TestMethod]
    public void UT_ERR_05_An_inactive_code_is_refused()
    {
        var env = new TestEnvironment();

        var result = env.Errors.Submit(TestEnvironment.Anna, Guid.NewGuid(), TestEnvironment.Hall1, env.InactiveCode, "x");

        Assert.AreEqual(Reasons.ERROR_CODE_INACTIVE, result.Reason);
        Assert.AreEqual(0, env.Index.Count);
    }

    [TestMethod]
    public void UT_ERR_05_A_code_that_gets_deactivated_while_the_form_is_open_is_refused()
    {
        var env = new TestEnvironment();
        var code = env.Catalog.GetAll().Single(c => c.Code == "SCAN-01");
        _ = env.Catalog.Save(code.Id, code.Version, code.Code, code.Title, code.Description, code.Instructions, code.Group, code.DefaultSeverity.ToWire(), false, "fay", env.Clock.UtcNowMs);

        var result = env.Errors.Submit(TestEnvironment.Anna, Guid.NewGuid(), TestEnvironment.Hall1, env.ScanCode, "x");

        Assert.AreEqual(Reasons.ERROR_CODE_INACTIVE, result.Reason);
    }

    [TestMethod]
    public void UT_ERR_06_No_duplicate_warning_a_second_report_of_the_same_code_and_place_creates_a_new_error()
    {
        var env = new TestEnvironment();
        var first = env.Submit(TestEnvironment.Anna);
        _ = env.Errors.ChangeStatus(TestEnvironment.Ben, first.Id, "Acknowledge", first.Version, null);

        var second = env.Submit(TestEnvironment.Chris);

        Assert.AreEqual("EMT-000002", second.ErrorNumber);
        Assert.AreEqual(ErrorStatus.New, second.Status);
    }

    [TestMethod]
    public void UT_ERR_07_A_deactivated_code_keeps_showing_in_the_old_error_and_leaves_the_form()
    {
        var env = new TestEnvironment();
        var error = env.Submit();
        var code = env.Catalog.GetAll().Single(c => c.Code == "SCAN-01");

        var deletion = env.Admin.DeleteCode(TestEnvironment.Fay, code.Id);
        Assert.AreEqual(Reasons.CODE_IN_USE, deletion.Reason);

        _ = env.Admin.SaveCode(TestEnvironment.Fay, code.Id, code.Version, code.Code, code.Title, code.Description, code.Instructions, code.Group, code.DefaultSeverity.ToWire(), false);

        var form = env.Errors.GetReportFormData(TestEnvironment.Anna).Value!;
        Assert.IsFalse(form.Codes.Any(c => c.Code == "SCAN-01"));
        Assert.IsTrue(env.Errors.GetDetail(TestEnvironment.Ben, error.Id).Value!.Error.ErrorCode == "SCAN-01");
    }

    [TestMethod]
    public void UT_ERR_08_Changing_the_catalog_later_does_not_change_existing_errors()
    {
        var env = new TestEnvironment();
        var error = env.Submit();
        var code = env.Catalog.GetAll().Single(c => c.Code == "SCAN-01");

        var changed = env.Admin.SaveCode(TestEnvironment.Fay, code.Id, code.Version, code.Code,
            "Neuer Titel", code.Description, code.Instructions, code.Group, "Critical", true);
        Assert.IsTrue(changed.Successful, changed.Reason);

        Assert.IsTrue(env.Index.TryGet(error.Id, out var stored));
        Assert.AreEqual("Scanner liest nicht", stored.CodeTitle);
        Assert.AreEqual(Severity.Warning, stored.Severity);
        Assert.AreEqual(Severity.Warning, env.Store.Records[error.Id].Severity);
    }

    [TestMethod]
    public void UT_ERR_Code_title_is_copied_into_the_error()
    {
        var env = new TestEnvironment();

        var error = env.Submit();

        Assert.AreEqual("Scanner liest nicht", error.CodeTitle);
    }

    [TestMethod]
    public void UT_VAL_01_Location_and_code_are_required()
    {
        var env = new TestEnvironment();

        var noLocation = env.Errors.Submit(TestEnvironment.Anna, Guid.NewGuid(), null, env.ScanCode, "x");
        Assert.AreEqual(Reasons.VALIDATION_FAILED, noLocation.Reason);
        Assert.IsTrue(noLocation.ValidationErrors.Any(e => e.Field == "LocationId" && e.MessageKey == ValidationKeys.REQUIRED));

        var noCode = env.Errors.Submit(TestEnvironment.Anna, Guid.NewGuid(), TestEnvironment.Hall1, Guid.Empty, "x");
        Assert.IsTrue(noCode.ValidationErrors.Any(e => e.Field == "ErrorCodeId"));

        var unknownCode = env.Errors.Submit(TestEnvironment.Anna, Guid.NewGuid(), TestEnvironment.Hall1, Guid.NewGuid(), "x");
        Assert.AreEqual(Reasons.VALIDATION_FAILED, unknownCode.Reason);

        var unknownLocation = env.Errors.Submit(TestEnvironment.Anna, Guid.NewGuid(), Guid.NewGuid().ToString(), env.ScanCode, "x");
        Assert.AreEqual(Reasons.LOCATION_NOT_FOUND, unknownLocation.Reason);

        var good = env.Errors.Submit(TestEnvironment.Anna, Guid.NewGuid(), TestEnvironment.Hall2, env.ScanCode, "x");
        Assert.AreEqual("Hall 2 / Versand", good.Value!.Error.LocationPath);
        Assert.AreEqual(1, env.Index.Count, "only the valid report created an error");
    }

    [TestMethod]
    public void UT_VAL_02_The_other_code_and_too_long_texts_need_valid_descriptions()
    {
        var env = new TestEnvironment();
        var other = Guid.Parse(Constants.Catalog.OTHER_CODE_ID);

        var emptyDescription = env.Errors.Submit(TestEnvironment.Anna, Guid.NewGuid(), TestEnvironment.Hall1, other, "  ");
        Assert.AreEqual(Reasons.VALIDATION_FAILED, emptyDescription.Reason);
        Assert.IsTrue(emptyDescription.ValidationErrors.Any(e => e.Field == "Description" && e.MessageKey == ValidationKeys.REQUIRED));

        var withDescription = env.Errors.Submit(TestEnvironment.Anna, Guid.NewGuid(), TestEnvironment.Hall1, other, "Something broke");
        Assert.IsTrue(withDescription.Successful, withDescription.Reason);

        var tooLong = env.Errors.Submit(TestEnvironment.Anna, Guid.NewGuid(), TestEnvironment.Hall1, env.ScanCode, new string('x', Constants.Limits.MAX_DESCRIPTION_LENGTH + 1));
        Assert.IsTrue(tooLong.ValidationErrors.Any(e => e.Field == "Description" && e.MessageKey == ValidationKeys.TOO_LONG));

        var atLimit = env.Errors.Submit(TestEnvironment.Anna, Guid.NewGuid(), TestEnvironment.Hall1, env.ScanCode, new string('x', Constants.Limits.MAX_DESCRIPTION_LENGTH));
        Assert.IsTrue(atLimit.Successful, atLimit.Reason);
    }

    [TestMethod]
    public void UT_REP_01_Report_form_offers_active_codes_and_locations_only()
    {
        var env = new TestEnvironment();

        var form = env.Errors.GetReportFormData(TestEnvironment.Anna).Value!;

        var codes = form.Codes.Select(c => c.Code).ToList();
        CollectionAssert.Contains(codes, "SCAN-01");
        CollectionAssert.Contains(codes, "GEN-01");
        CollectionAssert.Contains(codes, Constants.Catalog.OTHER_CODE);
        CollectionAssert.DoesNotContain(codes, "GEN-02");
        Assert.AreEqual(2, form.Locations.Count);
        Assert.IsFalse(form.NoActiveCodes);
    }

    [TestMethod]
    public void UT_REP_02_Without_active_codes_the_form_says_so_and_the_other_code_can_be_switched_off()
    {
        var env = new TestEnvironment();
        foreach (var code in env.Catalog.GetAll().Where(c => !c.IsOther && c.Active))
        {
            _ = env.Catalog.Save(code.Id, code.Version, code.Code, code.Title, code.Description, code.Instructions, code.Group, code.DefaultSeverity.ToWire(), false, "fay", env.Clock.UtcNowMs);
        }

        var withOther = env.Errors.GetReportFormData(TestEnvironment.Anna).Value!;
        Assert.AreEqual(1, withOther.Codes.Count);
        Assert.IsTrue(withOther.Codes[0].IsOther);
        Assert.IsFalse(withOther.NoActiveCodes);

        var settings = env.Settings.Current;
        _ = env.Admin.SaveSettings(TestEnvironment.Fay, settings.RetentionDays, false, settings.Version);

        var withoutOther = env.Errors.GetReportFormData(TestEnvironment.Anna).Value!;
        Assert.AreEqual(0, withoutOther.Codes.Count);
        Assert.IsTrue(withoutOther.NoActiveCodes);
    }

    [TestMethod]
    public void UT_REP_My_errors_are_the_own_open_errors_and_recent_final_ones()
    {
        var env = new TestEnvironment();
        var open = env.Submit(TestEnvironment.Anna);
        var recentFinal = env.Submit(TestEnvironment.Anna);
        _ = env.Errors.Cancel(TestEnvironment.Anna, recentFinal.Id, recentFinal.Version, "dup");
        var oldFinal = env.Submit(TestEnvironment.Anna);
        _ = env.Errors.Cancel(TestEnvironment.Anna, oldFinal.Id, oldFinal.Version, "dup");
        _ = env.Submit(TestEnvironment.Chris);

        env.Clock.AdvanceDays(Constants.Limits.MY_ERRORS_RECENT_DAYS + 1);
        var mine = env.Errors.GetMyErrors(TestEnvironment.Anna).Value!;

        CollectionAssert.AreEquivalent(new[] { open.Id }, mine.Select(e => e.Id).ToArray(), "final errors older than the period are not listed");

        env.Clock.UtcNowMs -= 2L * 24 * 60 * 60 * 1000;
        mine = env.Errors.GetMyErrors(TestEnvironment.Anna).Value!;
        Assert.AreEqual(3, mine.Count);
        Assert.IsTrue(mine.All(e => e.ReporterUserId == TestEnvironment.Anna));
    }

    [TestMethod]
    public void UT_PUSH_01_A_change_goes_to_the_managers_and_the_reporter_only()
    {
        var env = new TestEnvironment();

        var error = env.Submit(TestEnvironment.Anna);

        var (users, message) = env.Sender.Sent.Single();
        CollectionAssert.AreEquivalent(new[] { TestEnvironment.Ben, TestEnvironment.Gil, TestEnvironment.Hana, TestEnvironment.Anna }, users.ToArray());
        CollectionAssert.DoesNotContain(users.ToArray(), TestEnvironment.Chris);
        CollectionAssert.DoesNotContain(users.ToArray(), TestEnvironment.Dora);
        var push = (Newtonsoft.Json.Linq.JObject)message[0];
        Assert.AreEqual(Constants.Frontend.EMT_GUID, (string?)push["ID"]);
        Assert.AreEqual(error.Id.ToString(), (string?)push[Constants.Frontend.Push.ERROR_CHANGED]);
        Assert.AreEqual("Created", (string?)push["ChangeType"]);
        Assert.AreEqual("EMT-000001", (string?)push["Error"]!["ErrorNumber"]);

        _ = env.Errors.ChangeStatus(TestEnvironment.Ben, error.Id, "Acknowledge", error.Version, null);
        Assert.AreEqual("Updated", (string?)((Newtonsoft.Json.Linq.JObject)env.Sender.Sent.Last().Message[0])["ChangeType"]);
    }

    [TestMethod]
    public void UT_PUSH_01_A_reporter_without_the_report_permission_gets_nothing()
    {
        var env = new TestEnvironment();
        var error = env.Submit(TestEnvironment.Anna);
        env.Sender.Sent.Clear();
        env.Source.Revoke(Constants.Permissions.REPORT_ERRORS, TestEnvironment.Anna);

        _ = env.Errors.ChangeStatus(TestEnvironment.Ben, error.Id, "Acknowledge", error.Version, null);

        CollectionAssert.DoesNotContain(env.Sender.Sent.Single().Users.ToArray(), TestEnvironment.Anna);
    }

    [TestMethod]
    public void UT_PUSH_02_A_failing_observer_does_not_fail_the_change()
    {
        var env = new TestEnvironment();
        env.Errors.AddObserver(new ThrowingObserver());

        var result = env.Errors.Submit(TestEnvironment.Anna, Guid.NewGuid(), TestEnvironment.Hall1, env.ScanCode, "x");

        Assert.IsTrue(result.Successful);
        Assert.IsTrue(env.Store.Records.ContainsKey(result.Value!.Error.Id));
    }

    private class ThrowingObserver : IErrorObserver
    {
        public void OnErrorChanged(ErrorRecord error, ErrorChangeType changeType) => throw new InvalidOperationException("observer failed");
    }
}
