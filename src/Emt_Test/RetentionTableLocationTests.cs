using FSDEV.AMF.Controls.CSharpControls.Table;
using FSDEV.Common.Translations;
using FSDEV.FES.Server.Client;
using FSDEV.FES.Server.Client.Sessions;
using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Emt.Emt_Server.Errors;
using FSDEV.MOD.Emt.Emt_Server.Locations;
using FSDEV.MOD.Emt.Emt_Server.Tables;
using Newtonsoft.Json.Linq;
using Constants = FSDEV.MOD.Emt.Emt.Constants;

namespace FSDEV.MOD.Emt.Emt_Test;

[TestClass]
public class RetentionTableLocationTests
{
    /// <summary>Creates an error in the past and brings it into the final status with the given steps, all at the clock time of the creation.</summary>
    private static ErrorRecord CreateAged(TestEnvironment env, int daysAgo, params string[] actions)
    {
        var now = env.Clock.UtcNowMs;
        env.Clock.UtcNowMs = now - (daysAgo * 24L * 60 * 60 * 1000);
        var error = env.Submit();
        var current = error;
        foreach (var action in actions)
        {
            var result = env.Errors.ChangeStatus(TestEnvironment.Ben, error.Id, action, current.Version, "text");
            Assert.IsTrue(result.Successful, $"{action}: {result.Reason}");
            current = result.Value!;
        }

        env.Clock.UtcNowMs = now;
        return current;
    }

    [TestMethod]
    public void UT_RET_01_Only_final_errors_older_than_the_retention_time_are_removed()
    {
        var env = new TestEnvironment();
        var closed400 = CreateAged(env, 400, "Resolve", "Close");
        var inProgress400 = CreateAged(env, 400, "StartProgress");
        var closed100 = CreateAged(env, 100, "Resolve", "Close");
        var resolved400 = CreateAged(env, 400, "Resolve");
        var rejected366 = CreateAged(env, 366, "Reject");
        var rejected364 = CreateAged(env, 364, "Reject");
        var new400 = CreateAged(env, 400);

        var removedCallbacks = new List<Guid>();
        var job = new RetentionJob(env.Store, env.Index, env.Settings, env.Clock, error => removedCallbacks.Add(error.Id));
        var removed = job.RunOnce();

        Assert.AreEqual(2, removed);
        CollectionAssert.AreEquivalent(new[] { closed400.Id, rejected366.Id }, removedCallbacks);
        CollectionAssert.AreEquivalent(new[] { closed400.Id, rejected366.Id }, env.Store.Deleted);
        Assert.IsFalse(env.Index.TryGet(closed400.Id, out _));
        foreach (var kept in new[] { inProgress400, closed100, resolved400, rejected364, new400 })
        {
            Assert.IsTrue(env.Index.TryGet(kept.Id, out _), $"{kept.ErrorNumber} stays");
            Assert.IsTrue(env.Store.Records.ContainsKey(kept.Id), $"{kept.ErrorNumber} stays in the store");
        }

        Assert.AreEqual(env.Clock.UtcNowMs, job.LastRunAt);
    }

    [TestMethod]
    public void UT_RET_01_A_closed_error_within_the_retention_time_can_still_be_read()
    {
        var env = new TestEnvironment();
        var closed100 = CreateAged(env, 100, "Resolve", "Close");

        _ = env.Retention.RunOnce();

        var detail = env.Errors.GetDetail(TestEnvironment.Ben, closed100.Id);
        Assert.IsTrue(detail.Successful);
        Assert.AreEqual(ErrorStatus.Closed, detail.Value!.Error.Status);
    }

    [TestMethod]
    public void UT_RET_02_The_retention_time_of_the_settings_is_used_and_a_failed_delete_is_retried()
    {
        var env = new TestEnvironment();
        var closed100 = CreateAged(env, 100, "Resolve", "Close");
        var job = new RetentionJob(env.Store, env.Index, env.Settings, env.Clock, _ => { });
        Assert.AreEqual(0, job.RunOnce(), "365 days: still kept");

        var settings = env.Settings.Current;
        Assert.IsTrue(env.Settings.Save(60, true, settings.Version).Successful);

        env.Store.FailWrites = true;
        Assert.AreEqual(0, job.RunOnce(), "ilp refuses the delete");
        Assert.IsTrue(env.Index.TryGet(closed100.Id, out _), "the error is not forgotten when it could not be deleted");

        env.Store.FailWrites = false;
        Assert.AreEqual(1, job.RunOnce());
        Assert.IsFalse(env.Index.TryGet(closed100.Id, out _));
    }

    [TestMethod]
    public void UT_RET_02_Removing_updates_the_error_list_table_through_the_callback()
    {
        var env = new TestEnvironment();
        var table = new ErrorListTable();
        Assert.IsTrue(table.Initialize(new FakeFesClient(), new Translations("none"), 1000));
        var closed = CreateAged(env, 400, "Resolve", "Close");
        table.Upsert(closed);
        Assert.IsTrue(table.ContainsRow(closed.Id));

        var job = new RetentionJob(env.Store, env.Index, env.Settings, env.Clock, error => table.OnErrorChanged(error, ErrorChangeType.Deleted));
        Assert.AreEqual(1, job.RunOnce());

        Assert.IsFalse(table.ContainsRow(closed.Id));
    }

    [TestMethod]
    public async Task UT_RET_03_The_background_loop_stops_when_the_token_is_cancelled()
    {
        var env = new TestEnvironment();
        using var source = new CancellationTokenSource();

        var running = env.Retention.RunAsync(TimeSpan.FromHours(24), source.Token);
        await Task.Delay(100);
        Assert.AreEqual(env.Clock.UtcNowMs, env.Retention.LastRunAt, "runs once at the start");
        source.Cancel();

        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(running.IsCompletedSuccessfully);
    }

    // ---- error list table ----

    [TestMethod]
    public void UT_LIST_01_A_row_has_exactly_the_columns_of_the_table()
    {
        var env = new TestEnvironment();
        var error = env.Submit();

        var row = ErrorListTable.ToRow(error);

        var columns = ErrorListTable.GetColumns().Select(c => c.Name).ToList();
        CollectionAssert.AreEquivalent(columns, row.Keys.ToList());
        foreach (var contract in new[] { "ErrorNumber", "Number", "ReportedAt", "LocationPath", "ErrorCode", "CodeTitle", "Severity", "Status", "ReporterName", "AssigneeName" })
        {
            CollectionAssert.Contains(columns, contract, $"07 §2.4 column {contract}");
        }

        Assert.AreEqual("EMT-000001", row["ErrorNumber"]);
        Assert.AreEqual(1L, row["Number"]);
        Assert.AreEqual(error.ReportedAt, row["ReportedAt"]);
        Assert.AreEqual("Hall 1 / Wareneingang", row["LocationPath"]);
        Assert.AreEqual("SCAN-01", row["ErrorCode"]);
        Assert.AreEqual("Scanner liest nicht", row["CodeTitle"]);
        Assert.AreEqual("Warning", row["Severity"]);
        Assert.AreEqual("NEW", row["Status"]);
        Assert.AreEqual("anna", row["ReporterName"]);
        Assert.AreEqual("-", row["AssigneeName"], "a value that is not set is a dash, for the filter");
    }

    [TestMethod]
    public void UT_LIST_01_Rows_follow_the_errors_of_the_service()
    {
        var env = new TestEnvironment();
        var table = new ErrorListTable();
        Assert.IsTrue(table.Initialize(new FakeFesClient(), new Translations("none"), 1000));
        env.Errors.AddObserver(table);

        var error = env.Submit();
        Assert.IsTrue(table.ContainsRow(error.Id), "created");

        var acknowledged = env.Errors.ChangeStatus(TestEnvironment.Ben, error.Id, "Acknowledge", error.Version, null);
        Assert.IsTrue(acknowledged.Successful);
        Assert.IsTrue(table.ContainsRow(error.Id), "updated, not duplicated");

        table.Load(env.Index.GetAll());
        Assert.IsTrue(table.ContainsRow(error.Id), "loading again is possible");
    }

    [TestMethod]
    public void UT_LIST_02_Catalog_table_rows_follow_the_catalog()
    {
        var env = new TestEnvironment();
        var table = new ErrorCodeTable(env.Catalog);
        Assert.IsTrue(table.Initialize(new FakeFesClient(), new Translations("none")));
        env.Catalog.Changed += table.Refresh;

        var columns = ErrorCodeTable.GetColumns().Select(c => c.Name).ToList();
        var row = ErrorCodeTable.ToRow(env.Catalog.GetAll().First());
        CollectionAssert.AreEquivalent(columns, row.Keys.ToList());

        var created = env.Admin.SaveCode(TestEnvironment.Fay, null, null, "NEW-1", "a", "", "", "", "Info", true);
        Assert.IsTrue(created.Successful, created.Reason);
        var deleted = env.Admin.DeleteCode(TestEnvironment.Fay, created.Value!.Id);
        Assert.IsTrue(deleted.Successful, deleted.Reason);
    }

    // ---- locations ----

    [TestMethod]
    public void UT_LOC_01_Locations_are_cached_and_read_again_after_the_maximum_age()
    {
        var source = new FakeLocationSource();
        source.Locations.Add(new LocationOption("a", "Hall 1", "Building"));
        var now = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var provider = new LocationProvider(source, [], TimeSpan.FromMinutes(10), () => now);

        Assert.IsTrue(provider.TryGetAll(out var first));
        Assert.AreEqual(1, first.Count);

        source.Locations.Add(new LocationOption("b", "Hall 2", "Building"));
        now = now.AddMinutes(5);
        Assert.IsTrue(provider.TryGetAll(out var cached));
        Assert.AreEqual(1, cached.Count, "from the cache");
        Assert.AreEqual(1, source.Calls);

        now = now.AddMinutes(6);
        Assert.IsTrue(provider.TryGetAll(out var refreshed));
        Assert.AreEqual(2, refreshed.Count);
    }

    [TestMethod]
    public void UT_LOC_01_A_failed_refresh_keeps_the_old_locations_and_a_failed_first_read_is_a_failure()
    {
        var source = new FakeLocationSource { Available = false };
        var now = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var provider = new LocationProvider(source, [], TimeSpan.FromMinutes(10), () => now);
        Assert.IsFalse(provider.TryGetAll(out _));

        source.Available = true;
        source.Locations.Add(new LocationOption("a", "Hall 1", "Building"));
        Assert.IsTrue(provider.TryGetAll(out _));

        source.Available = false;
        now = now.AddHours(1);
        Assert.IsTrue(provider.TryGetAll(out var kept));
        Assert.AreEqual(1, kept.Count);
    }

    [TestMethod]
    public void UT_LOC_02_Selectable_types_limit_the_locations_and_lookup_finds_by_id()
    {
        var source = new FakeLocationSource();
        source.Locations.Add(new LocationOption("11111111-0000-0000-0000-000000000000", "Hall 1", "Building"));
        source.Locations.Add(new LocationOption("22222222-0000-0000-0000-000000000000", "Hall 1 / Wareneingang", "Room"));
        var provider = new LocationProvider(source, ["room"], TimeSpan.FromMinutes(10));

        Assert.IsTrue(provider.TryGetAll(out var all));
        Assert.AreEqual(1, all.Count);
        Assert.IsTrue(provider.TryGet("22222222-0000-0000-0000-000000000000", out var location));
        Assert.AreEqual("Hall 1 / Wareneingang", location.Path);
        Assert.IsFalse(provider.TryGet("11111111-0000-0000-0000-000000000000", out _), "the building is not selectable");
    }

    [TestMethod]
    public void UT_LOC_03_Report_form_data_is_unavailable_without_locations()
    {
        var env = new TestEnvironment();
        env.LocationSource.Available = false;
        env.Build();

        Assert.AreEqual(Reasons.UNAVAILABLE, env.Errors.GetReportFormData(TestEnvironment.Anna).Reason);
        Assert.AreEqual(Reasons.UNAVAILABLE, env.Errors.Submit(TestEnvironment.Anna, Guid.NewGuid(), TestEnvironment.Hall1, env.ScanCode, "x").Reason);
        Assert.AreEqual(0, env.Index.Count);
    }

    [TestMethod]
    public void UT_LOC_Location_json_has_the_contract_fields()
    {
        var json = new LocationOption("id", "Hall 1 / Wareneingang", "Room").ToJson();

        Assert.AreEqual("id", (string?)json["LocationId"]);
        Assert.AreEqual("Hall 1 / Wareneingang", (string?)json["Path"]);
        Assert.AreEqual("Room", (string?)json["Type"]);
    }

    [TestMethod]
    public void UT_JSON_Summary_has_the_contract_fields()
    {
        var env = new TestEnvironment();
        var error = env.Submit();

        var summary = error.ToSummaryJson();
        foreach (var field in new[] { "ErrorId", "ErrorNumber", "Number", "Status", "Severity", "ErrorCodeId", "ErrorCode", "CodeTitle", "LocationId", "LocationPath", "ReporterUserId", "ReporterName", "AssigneeUserId", "AssigneeName", "ReportedAt", "LastChangedAt", "LastChangedByName", "FinalizedAt", "Version" })
        {
            Assert.IsTrue(summary.ContainsKey(field), field);
        }

        Assert.IsFalse(summary.ContainsKey("Description"), "the description is part of the detail only");
        Assert.AreEqual("NEW", (string?)summary["Status"]);
        Assert.AreEqual(JTokenType.Null, summary["AssigneeUserId"]!.Type);
        Assert.AreEqual("Test", (string?)error.ToSummaryJson(true)["Description"]);
        Assert.AreEqual("Scanner liest nicht", (string?)summary["CodeTitle"]);
    }
}
