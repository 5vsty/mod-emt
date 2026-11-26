using FSDEV.MOD.Emt.Emt.Domain;
using Constants = FSDEV.MOD.Emt.Emt.Constants;

namespace FSDEV.MOD.Emt.Emt_Test;

[TestClass]
public class LifecycleTests
{
    private static readonly Dictionary<ErrorStatus, ErrorAction[]> ExpectedActions = new()
    {
        [ErrorStatus.New] = [ErrorAction.Acknowledge, ErrorAction.StartProgress, ErrorAction.Resolve, ErrorAction.Reject, ErrorAction.Cancel],
        [ErrorStatus.Acknowledged] = [ErrorAction.StartProgress, ErrorAction.Resolve, ErrorAction.Reject],
        [ErrorStatus.InProgress] = [ErrorAction.Resolve],
        [ErrorStatus.Resolved] = [ErrorAction.Reopen, ErrorAction.Close],
        [ErrorStatus.Closed] = [],
        [ErrorStatus.Rejected] = [],
        [ErrorStatus.Cancelled] = [],
    };

    [TestMethod]
    public void UT_LC_01_Acknowledge_then_start_progress_records_the_user_each_step()
    {
        var env = new TestEnvironment();
        var error = env.Submit();

        var acknowledged = env.Errors.ChangeStatus(TestEnvironment.Ben, error.Id, "Acknowledge", error.Version, null);
        Assert.IsTrue(acknowledged.Successful, acknowledged.Reason);
        Assert.AreEqual(ErrorStatus.Acknowledged, acknowledged.Value!.Status);

        var inProgress = env.Errors.ChangeStatus(TestEnvironment.Ben, error.Id, "StartProgress", acknowledged.Value.Version, null);
        Assert.IsTrue(inProgress.Successful, inProgress.Reason);
        Assert.AreEqual(ErrorStatus.InProgress, inProgress.Value!.Status);

        var events = inProgress.Value.Events;
        Assert.AreEqual(3, events.Count);
        Assert.AreEqual(ErrorEventTypes.STATUS_CHANGED, events[1].EventType);
        Assert.AreEqual("ben", events[1].UserName);
        Assert.AreEqual("NEW", events[1].FromValue);
        Assert.AreEqual("ACKNOWLEDGED", events[1].ToValue);
        Assert.AreEqual("ben", events[2].UserName);
        Assert.AreEqual("IN_PROGRESS", events[2].ToValue);
    }

    [TestMethod]
    public void UT_LC_02_Transition_table_matches_the_design_for_every_status_and_action()
    {
        foreach (var status in Enum.GetValues<ErrorStatus>())
        {
            foreach (var action in Enum.GetValues<ErrorAction>())
            {
                var allowed = ErrorLifecycle.TryGetTarget(status, action, out _);
                Assert.AreEqual(ExpectedActions[status].Contains(action), allowed, $"{status} + {action}");
            }
        }

        Assert.IsTrue(ErrorLifecycle.TryGetTarget(ErrorStatus.Resolved, ErrorAction.Reopen, out var reopened));
        Assert.AreEqual(ErrorStatus.InProgress, reopened);
        Assert.IsTrue(ErrorLifecycle.TryGetTarget(ErrorStatus.Resolved, ErrorAction.Close, out var closed));
        Assert.AreEqual(ErrorStatus.Closed, closed);
    }

    [TestMethod]
    public void UT_LC_02_Jumping_from_new_to_closed_is_refused_and_the_error_stays_new()
    {
        var env = new TestEnvironment();
        var error = env.Submit();

        var result = env.Errors.ChangeStatus(TestEnvironment.Ben, error.Id, "Close", error.Version, null);

        Assert.AreEqual(Reasons.INVALID_TRANSITION, result.Reason);
        Assert.IsTrue(env.Index.TryGet(error.Id, out var stored));
        Assert.AreEqual(ErrorStatus.New, stored.Status);
        Assert.AreEqual(1, stored.Version);
    }

    [TestMethod]
    public void UT_LC_03_Resolve_needs_a_note()
    {
        var env = new TestEnvironment();
        var error = env.Submit();
        var started = env.Errors.ChangeStatus(TestEnvironment.Ben, error.Id, "StartProgress", error.Version, null).Value!;

        var withoutNote = env.Errors.ChangeStatus(TestEnvironment.Ben, error.Id, "Resolve", started.Version, "  ");
        Assert.AreEqual(Reasons.VALIDATION_FAILED, withoutNote.Reason);
        Assert.AreEqual(ErrorStatus.InProgress, env.Index.TryGet(error.Id, out var unchanged) ? unchanged.Status : default);

        var withNote = env.Errors.ChangeStatus(TestEnvironment.Ben, error.Id, "Resolve", started.Version, "Reader replaced");
        Assert.IsTrue(withNote.Successful, withNote.Reason);
        Assert.AreEqual(ErrorStatus.Resolved, withNote.Value!.Status);
        Assert.AreEqual("Reader replaced", withNote.Value.Events.Last().Text);
        Assert.AreEqual(0, withNote.Value.FinalizedAt, "RESOLVED is not final");
    }

    [TestMethod]
    public void UT_LC_04_Reporter_can_cancel_a_new_error_but_not_an_acknowledged_one()
    {
        var env = new TestEnvironment();
        var errorA = env.Submit();
        var errorB = env.Submit();
        _ = env.Errors.ChangeStatus(TestEnvironment.Ben, errorB.Id, "Acknowledge", errorB.Version, null);
        Assert.IsTrue(env.Index.TryGet(errorB.Id, out var currentB));

        var withoutReason = env.Errors.Cancel(TestEnvironment.Anna, errorA.Id, errorA.Version, "");
        Assert.AreEqual(Reasons.VALIDATION_FAILED, withoutReason.Reason);

        var cancelledA = env.Errors.Cancel(TestEnvironment.Anna, errorA.Id, errorA.Version, "Reported twice");
        Assert.IsTrue(cancelledA.Successful, cancelledA.Reason);
        Assert.AreEqual(ErrorStatus.Cancelled, cancelledA.Value!.Status);
        Assert.IsTrue(cancelledA.Value.FinalizedAt > 0);

        var cancelledB = env.Errors.Cancel(TestEnvironment.Anna, errorB.Id, currentB!.Version, "Too late");
        Assert.AreEqual(Reasons.INVALID_TRANSITION, cancelledB.Reason);
    }

    [TestMethod]
    public void UT_LC_04_Cancelling_is_not_possible_through_the_manager_message()
    {
        var env = new TestEnvironment();
        var error = env.Submit();

        var result = env.Errors.ChangeStatus(TestEnvironment.Ben, error.Id, "Cancel", error.Version, "x");

        Assert.AreEqual(Reasons.INVALID_TRANSITION, result.Reason);
    }

    [TestMethod]
    public void UT_LC_05_Final_errors_refuse_every_change()
    {
        var env = new TestEnvironment();
        var error = env.Submit();
        var rejected = env.Errors.ChangeStatus(TestEnvironment.Ben, error.Id, "Reject", error.Version, "Not an error").Value!;
        Assert.IsTrue(rejected.FinalizedAt > 0);

        foreach (var action in Enum.GetValues<ErrorAction>().Where(a => !ErrorLifecycle.IsReporterAction(a)))
        {
            var result = env.Errors.ChangeStatus(TestEnvironment.Ben, error.Id, action.ToString(), rejected.Version, "text");
            Assert.AreEqual(Reasons.INVALID_TRANSITION, result.Reason, action.ToString());
        }

        Assert.AreEqual(Reasons.FINAL_STATE, env.Errors.AddNote(TestEnvironment.Ben, error.Id, "note").Reason);
        Assert.AreEqual(Reasons.FINAL_STATE, env.Errors.ChangeSeverity(TestEnvironment.Ben, error.Id, "Critical", rejected.Version).Reason);
        Assert.AreEqual(Reasons.FINAL_STATE, env.Errors.Assign(TestEnvironment.Ben, error.Id, TestEnvironment.Gil, rejected.Version).Reason);

        var detail = env.Errors.GetDetail(TestEnvironment.Ben, error.Id).Value!;
        Assert.AreEqual(0, detail.AllowedActions.Count, "a final error offers no action");
    }

    [TestMethod]
    public void UT_LC_06_Reopen_needs_a_reason_and_the_error_can_be_resolved_again()
    {
        var env = new TestEnvironment();
        var error = env.Submit();
        var resolved = env.Errors.ChangeStatus(TestEnvironment.Ben, error.Id, "Resolve", error.Version, "Reader replaced").Value!;

        var withoutReason = env.Errors.ChangeStatus(TestEnvironment.Ben, error.Id, "Reopen", resolved.Version, "");
        Assert.AreEqual(Reasons.VALIDATION_FAILED, withoutReason.Reason);

        var reopened = env.Errors.ChangeStatus(TestEnvironment.Ben, error.Id, "Reopen", resolved.Version, "Happens again");
        Assert.IsTrue(reopened.Successful, reopened.Reason);
        Assert.AreEqual(ErrorStatus.InProgress, reopened.Value!.Status);
        Assert.AreEqual("Happens again", reopened.Value.Events.Last().Text);

        var resolvedAgain = env.Errors.ChangeStatus(TestEnvironment.Ben, error.Id, "Resolve", reopened.Value.Version, "Replaced the cable");
        Assert.AreEqual(ErrorStatus.Resolved, resolvedAgain.Value!.Status);
    }

    [TestMethod]
    public void UT_LC_07_Resolved_is_never_closed_by_the_system()
    {
        var env = new TestEnvironment();
        var error = env.Submit();
        _ = env.Errors.ChangeStatus(TestEnvironment.Ben, error.Id, "Resolve", error.Version, "Done");

        env.Clock.AdvanceDays(800);
        _ = env.Retention.RunOnce();

        Assert.IsTrue(env.Index.TryGet(error.Id, out var stored));
        Assert.AreEqual(ErrorStatus.Resolved, stored.Status);
        Assert.AreEqual(2, stored.Events.Count, "no event of the system was added");

        var closed = env.Errors.ChangeStatus(TestEnvironment.Gil, error.Id, "Close", stored.Version, null);
        Assert.IsTrue(closed.Successful, closed.Reason);
        Assert.AreEqual("gil", closed.Value!.Events.Last().UserName);
    }

    [TestMethod]
    public void UT_LC_Only_the_reporter_and_managers_get_their_actions_offered()
    {
        var asReporter = ErrorLifecycle.GetAllowedActions(ErrorStatus.New, isManager: false, isReporter: true);
        CollectionAssert.AreEqual(new[] { ErrorAction.Cancel }, asReporter);

        var asManager = ErrorLifecycle.GetAllowedActions(ErrorStatus.Acknowledged, isManager: true, isReporter: false);
        CollectionAssert.AreEquivalent(new[] { ErrorAction.StartProgress, ErrorAction.Resolve, ErrorAction.Reject }, asManager);
    }

    [TestMethod]
    public void UT_ASG_01_Assignee_must_be_an_error_manager()
    {
        var env = new TestEnvironment();
        var error = env.Submit();
        var acknowledged = env.Errors.ChangeStatus(TestEnvironment.Gil, error.Id, "Acknowledge", error.Version, null).Value!;

        var toBen = env.Errors.Assign(TestEnvironment.Gil, error.Id, TestEnvironment.Ben, acknowledged.Version);
        Assert.IsTrue(toBen.Successful, toBen.Reason);
        Assert.AreEqual("ben", toBen.Value!.AssigneeName);
        Assert.AreEqual(TestEnvironment.Ben, toBen.Value.AssigneeUserId);
        Assert.AreEqual(ErrorEventTypes.ASSIGNED, toBen.Value.Events.Last().EventType);

        var toChris = env.Errors.Assign(TestEnvironment.Gil, error.Id, TestEnvironment.Chris, toBen.Value.Version);
        Assert.AreEqual(Reasons.INVALID_ASSIGNEE, toChris.Reason);
        Assert.IsTrue(env.Index.TryGet(error.Id, out var stored));
        Assert.AreEqual("ben", stored.AssigneeName);
    }

    [TestMethod]
    public void UT_ASG_02_Error_keeps_its_assignee_after_the_permission_was_revoked_and_can_be_reassigned()
    {
        var env = new TestEnvironment();
        var error = env.Submit();
        var assigned = env.Errors.Assign(TestEnvironment.Gil, error.Id, TestEnvironment.Ben, error.Version).Value!;

        env.Source.Revoke(Constants.Permissions.MANAGE_ERRORS, TestEnvironment.Ben);

        var detail = env.Errors.GetDetail(TestEnvironment.Gil, error.Id).Value!;
        Assert.AreEqual("ben", detail.Error.AssigneeName);
        Assert.IsTrue(detail.Error.Events.Any(e => e.EventType == ErrorEventTypes.ASSIGNED && e.ToValue == "ben"));

        var reassigned = env.Errors.Assign(TestEnvironment.Gil, error.Id, TestEnvironment.Gil, assigned.Version);
        Assert.IsTrue(reassigned.Successful, reassigned.Reason);
        Assert.AreEqual("gil", reassigned.Value!.AssigneeName);
    }

    [TestMethod]
    public void UT_ASG_Unassigning_is_possible()
    {
        var env = new TestEnvironment();
        var error = env.Submit();
        var assigned = env.Errors.Assign(TestEnvironment.Gil, error.Id, TestEnvironment.Ben, error.Version).Value!;

        var unassigned = env.Errors.Assign(TestEnvironment.Gil, error.Id, null, assigned.Version);

        Assert.IsTrue(unassigned.Successful, unassigned.Reason);
        Assert.IsNull(unassigned.Value!.AssigneeUserId);
        Assert.AreEqual(string.Empty, unassigned.Value.AssigneeName);
    }

    [TestMethod]
    public void UT_CONC_01_Stale_version_is_a_conflict_that_names_the_user_who_changed_the_error()
    {
        var env = new TestEnvironment();
        var error = env.Submit();

        var benAcknowledges = env.Errors.ChangeStatus(TestEnvironment.Ben, error.Id, "Acknowledge", error.Version, null);
        Assert.IsTrue(benAcknowledges.Successful);

        var gilRejects = env.Errors.ChangeStatus(TestEnvironment.Gil, error.Id, "Reject", error.Version, "Not an error");

        Assert.AreEqual(Reasons.CONFLICT, gilRejects.Reason);
        Assert.AreEqual("ben", gilRejects.ConflictByName);
        Assert.AreEqual(ErrorStatus.Acknowledged, gilRejects.Value!.Status, "the response carries the current error");
        Assert.IsTrue(env.Index.TryGet(error.Id, out var stored));
        Assert.AreEqual(ErrorStatus.Acknowledged, stored.Status);
    }

    [TestMethod]
    public void UT_CONC_02_Two_parallel_changes_with_the_same_version_only_one_succeeds()
    {
        var env = new TestEnvironment();
        var error = env.Submit();
        using var start = new ManualResetEventSlim(false);

        var tasks = new[] { TestEnvironment.Ben, TestEnvironment.Gil }.Select(user => Task.Run(() =>
        {
            start.Wait();
            return env.Errors.ChangeStatus(user, error.Id, "Acknowledge", error.Version, null);
        })).ToArray();
        start.Set();
        Task.WaitAll(tasks);

        Assert.AreEqual(1, tasks.Count(t => t.Result.Successful));
        Assert.AreEqual(1, tasks.Count(t => t.Result.Reason == Reasons.CONFLICT));
        Assert.IsTrue(env.Index.TryGet(error.Id, out var stored));
        Assert.AreEqual(2, stored.Version);
        Assert.AreEqual(2, stored.Events.Count);
    }

    [TestMethod]
    public void UT_CONC_Notes_do_not_change_the_version_and_never_conflict()
    {
        var env = new TestEnvironment();
        var error = env.Submit();

        var first = env.Errors.AddNote(TestEnvironment.Ben, error.Id, "First look");
        var second = env.Errors.AddNote(TestEnvironment.Gil, error.Id, "Second look");

        Assert.IsTrue(first.Successful && second.Successful);
        Assert.AreEqual(error.Version, second.Value!.Version);
        Assert.AreEqual(3, second.Value.Events.Count);
    }

    [TestMethod]
    public void UT_ERR_10_Every_change_adds_exactly_one_event_with_before_and_after()
    {
        var env = new TestEnvironment();
        var error = env.Submit();

        var severity = env.Errors.ChangeSeverity(TestEnvironment.Ben, error.Id, "Critical", error.Version).Value!;
        var assigned = env.Errors.Assign(TestEnvironment.Ben, error.Id, TestEnvironment.Ben, severity.Version).Value!;
        var acknowledged = env.Errors.ChangeStatus(TestEnvironment.Ben, error.Id, "Acknowledge", assigned.Version, null).Value!;
        var noted = env.Errors.AddNote(TestEnvironment.Ben, error.Id, "Looking into it").Value!;

        Assert.AreEqual(5, noted.Events.Count);
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 }, noted.Events.Select(e => e.Seq).ToArray());
        Assert.AreEqual(ErrorEventTypes.SEVERITY_CHANGED, noted.Events[1].EventType);
        Assert.AreEqual("Warning", noted.Events[1].FromValue);
        Assert.AreEqual("Critical", noted.Events[1].ToValue);
        Assert.AreEqual("-", noted.Events[2].FromValue);
        Assert.AreEqual("ben", noted.Events[2].ToValue);
        Assert.AreEqual(ErrorEventTypes.NOTE_ADDED, noted.Events[4].EventType);
        Assert.AreEqual(acknowledged.Version, noted.Version);
        Assert.IsTrue(noted.Events.All(e => e.At > 0 && e.UserName.Length > 0));
    }
}
