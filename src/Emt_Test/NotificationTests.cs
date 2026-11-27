using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Emt.Emt.ModuleConfig;
using FSDEV.MOD.Emt.Emt_Server.Errors;
using FSDEV.MOD.Emt.Emt_Server.Notifications;
using Newtonsoft.Json.Linq;
using System.Diagnostics.CodeAnalysis;
using Constants = FSDEV.MOD.Emt.Emt.Constants;

namespace FSDEV.MOD.Emt.Emt_Test;

internal class FakeNotificationPublisher : INotificationPublisher
{
    public List<(Guid ErrorId, NotificationMessage Message, string Actor, string Id)> Published { get; } = [];
    public List<string> Archived { get; } = [];
    public bool FailPublish { get; set; }
    public bool FailArchive { get; set; }
    public bool ThrowOnPublish { get; set; }

    public bool TryPublish(ErrorRecord error, NotificationMessage message, string actorName, [NotNullWhen(true)] out string? notificationId)
    {
        if (ThrowOnPublish)
        {
            throw new InvalidOperationException("the Notifications module is broken");
        }

        notificationId = null;
        if (FailPublish)
        {
            return false;
        }

        notificationId = Guid.NewGuid().ToString();
        Published.Add((error.Id, message, actorName, notificationId));
        return true;
    }

    public bool TryArchive(string notificationId)
    {
        if (FailArchive)
        {
            return false;
        }

        Archived.Add(notificationId);
        return true;
    }

    public List<(Guid UserId, string Kind, string Detail)> To(Guid errorId) => Published.Where(p => p.ErrorId == errorId).Select(p => (p.Message.UserId, p.Message.Kind, p.Message.Detail)).ToList();
}

[TestClass]
public class NotificationTests
{
    // anna and chris report; ben and gil manage errors (gil is the technician in the scenarios); hana has all permissions

    private static NotificationObserver Attach(TestEnvironment env, FakeNotificationPublisher publisher, bool inline = true, CancellationToken token = default)
    {
        var observer = new NotificationObserver(publisher, env.Errors, env.Permissions.HasPermission, () => env.Permissions.GetUsersWithPermission(Constants.Permissions.MANAGE_ERRORS), token, inline);
        env.Errors.AddObserver(observer);
        return observer;
    }

    private static ErrorRecord Reload(TestEnvironment env, Guid errorId)
    {
        Assert.IsTrue(env.Index.TryGet(errorId, out var error));
        return error;
    }

    private static string[] Ids(ErrorRecord error) => error.NotificationId.Split(';', StringSplitOptions.RemoveEmptyEntries);

    [TestMethod]
    public void UT_NTF_01_A_new_error_creates_one_notification_for_each_manager_and_none_for_the_reporter()
    {
        var env = new TestEnvironment();
        var publisher = new FakeNotificationPublisher();
        _ = Attach(env, publisher);

        var created = env.Submit(TestEnvironment.Anna);

        var sent = publisher.To(created.Id);
        CollectionAssert.AreEquivalent(new[] { TestEnvironment.Ben, TestEnvironment.Gil, TestEnvironment.Hana }, sent.Select(s => s.UserId).ToArray());
        Assert.IsTrue(sent.All(s => s.Kind == NotificationKinds.NEW_ERROR));
        Assert.IsTrue(publisher.Published.All(p => p.Actor == "anna"));
        var error = Reload(env, created.Id);
        Assert.AreEqual(ErrorRecord.NOTIFICATION_ACTIVE, error.NotificationState);
        CollectionAssert.AreEquivalent(publisher.Published.Select(p => p.Id).ToArray(), Ids(error), "all ids are kept");
        Assert.AreEqual(created.Version, error.Version, "the notifications are no change of the error");
        Assert.AreEqual(created.Events.Count, error.Events.Count, "no event in the timeline");
        Assert.AreEqual(error.NotificationId, env.Store.Records[created.Id].NotificationId, "written to the store");
    }

    [TestMethod]
    public void UT_NTF_02_A_reporter_who_is_a_manager_is_not_told_about_the_own_report()
    {
        var env = new TestEnvironment();
        var publisher = new FakeNotificationPublisher();
        _ = Attach(env, publisher);

        var created = env.Submit(TestEnvironment.Hana);

        CollectionAssert.AreEquivalent(new[] { TestEnvironment.Ben, TestEnvironment.Gil }, publisher.To(created.Id).Select(s => s.UserId).ToArray());
    }

    [TestMethod]
    public void UT_NTF_03_Assigning_tells_the_assignee_and_the_reporter_but_not_the_manager_who_assigned()
    {
        var env = new TestEnvironment();
        var created = env.Submit(TestEnvironment.Anna);
        var publisher = new FakeNotificationPublisher();
        _ = Attach(env, publisher);

        var result = env.Errors.Assign(TestEnvironment.Ben, created.Id, TestEnvironment.Gil, created.Version);
        Assert.IsTrue(result.Successful, result.Reason);

        CollectionAssert.AreEquivalent(
            new[] { (TestEnvironment.Gil, NotificationKinds.ASSIGNED, "gil"), (TestEnvironment.Anna, NotificationKinds.ASSIGNEE_CHANGED, "gil") },
            publisher.To(created.Id));
        Assert.IsTrue(publisher.Published.All(p => p.Actor == "ben"));
    }

    [TestMethod]
    public void UT_NTF_04_A_status_change_by_the_assignee_tells_the_reporter_only()
    {
        var env = new TestEnvironment();
        var created = env.Submit(TestEnvironment.Anna);
        _ = env.Errors.Assign(TestEnvironment.Ben, created.Id, TestEnvironment.Gil, created.Version);
        var publisher = new FakeNotificationPublisher();
        _ = Attach(env, publisher);

        _ = env.Errors.ChangeStatus(TestEnvironment.Gil, created.Id, "StartProgress", Reload(env, created.Id).Version, null);
        _ = env.Errors.ChangeStatus(TestEnvironment.Gil, created.Id, "Resolve", Reload(env, created.Id).Version, "Label roll replaced");

        CollectionAssert.AreEqual(
            new[] { (TestEnvironment.Anna, NotificationKinds.STATUS_CHANGED, "IN_PROGRESS"), (TestEnvironment.Anna, NotificationKinds.STATUS_CHANGED, "RESOLVED") },
            publisher.To(created.Id));
        Assert.AreEqual(0, publisher.Archived.Count, "a resolved error keeps its notifications");
    }

    [TestMethod]
    public void UT_NTF_05_A_note_of_a_manager_tells_the_assignee_and_the_reporter()
    {
        var env = new TestEnvironment();
        var created = env.Submit(TestEnvironment.Anna);
        _ = env.Errors.Assign(TestEnvironment.Ben, created.Id, TestEnvironment.Gil, created.Version);
        var publisher = new FakeNotificationPublisher();
        _ = Attach(env, publisher);

        var result = env.Errors.AddNote(TestEnvironment.Ben, created.Id, "Spare part is on its way");
        Assert.IsTrue(result.Successful, result.Reason);

        CollectionAssert.AreEquivalent(
            new[] { (TestEnvironment.Gil, NotificationKinds.NOTE_ADDED, "Spare part is on its way"), (TestEnvironment.Anna, NotificationKinds.NOTE_ADDED, "Spare part is on its way") },
            publisher.To(created.Id));
    }

    [TestMethod]
    public void UT_NTF_06_A_cancelled_error_moves_the_earlier_notifications_and_tells_the_managers()
    {
        var env = new TestEnvironment();
        var publisher = new FakeNotificationPublisher();
        _ = Attach(env, publisher);
        var created = env.Submit(TestEnvironment.Anna);
        var earlier = Ids(Reload(env, created.Id));
        Assert.AreEqual(3, earlier.Length);

        var result = env.Errors.Cancel(TestEnvironment.Anna, created.Id, created.Version, "Pressed by mistake");
        Assert.IsTrue(result.Successful, result.Reason);

        CollectionAssert.AreEquivalent(earlier, publisher.Archived.ToArray(), "the notifications before the change are in the history");
        var cancelled = publisher.To(created.Id).Where(p => p.Kind == NotificationKinds.STATUS_CHANGED).ToList();
        CollectionAssert.AreEquivalent(new[] { TestEnvironment.Ben, TestEnvironment.Gil, TestEnvironment.Hana }, cancelled.Select(c => c.UserId).ToArray());
        Assert.IsTrue(cancelled.All(c => c.Detail == "CANCELLED"));
        var error = Reload(env, created.Id);
        Assert.AreEqual(ErrorRecord.NOTIFICATION_ACTIVE, error.NotificationState, "the new ones stay until their recipient presses OK");
        Assert.AreEqual(3, Ids(error).Length);
        Assert.IsFalse(Ids(error).Intersect(earlier).Any());
    }

    [TestMethod]
    public void UT_NTF_07_A_resolved_error_keeps_its_notifications_and_closing_it_moves_the_earlier_ones()
    {
        var env = new TestEnvironment();
        var created = env.Submit(TestEnvironment.Anna);
        _ = env.Errors.Assign(TestEnvironment.Ben, created.Id, TestEnvironment.Gil, created.Version);
        var publisher = new FakeNotificationPublisher();
        _ = Attach(env, publisher);
        _ = env.Errors.ChangeStatus(TestEnvironment.Gil, created.Id, "StartProgress", Reload(env, created.Id).Version, null);
        _ = env.Errors.ChangeStatus(TestEnvironment.Gil, created.Id, "Resolve", Reload(env, created.Id).Version, "Done");
        Assert.AreEqual(0, publisher.Archived.Count);
        var beforeClose = Ids(Reload(env, created.Id));
        Assert.AreEqual(2, beforeClose.Length);

        _ = env.Errors.ChangeStatus(TestEnvironment.Ben, created.Id, "Close", Reload(env, created.Id).Version, null);

        CollectionAssert.AreEquivalent(beforeClose, publisher.Archived.ToArray());
        var closed = publisher.To(created.Id).Skip(2).ToList();
        CollectionAssert.AreEquivalent(new[] { TestEnvironment.Gil, TestEnvironment.Anna }, closed.Select(c => c.UserId).ToArray(), "the assignee and the reporter are told, the manager who closed it is not");
        Assert.IsTrue(closed.All(c => c.Detail == "CLOSED"));
    }

    [TestMethod]
    public void UT_NTF_08_A_failing_publisher_never_fails_the_report_and_the_error_is_marked()
    {
        var env = new TestEnvironment();
        var publisher = new FakeNotificationPublisher { FailPublish = true };
        _ = Attach(env, publisher);

        var created = env.Submit(TestEnvironment.Anna);

        Assert.AreEqual(ErrorStatus.New, created.Status);
        var error = Reload(env, created.Id);
        Assert.AreEqual(ErrorRecord.NOTIFICATION_FAILED, error.NotificationState);
        Assert.AreEqual(0, Ids(error).Length);
    }

    [TestMethod]
    public void UT_NTF_09_A_publisher_that_throws_never_fails_the_report()
    {
        var env = new TestEnvironment();
        var publisher = new FakeNotificationPublisher { ThrowOnPublish = true };
        _ = Attach(env, publisher);

        var created = env.Submit(TestEnvironment.Anna);

        Assert.IsTrue(env.Store.Records.ContainsKey(created.Id));
        Assert.AreEqual(ErrorRecord.NOTIFICATION_NONE, Reload(env, created.Id).NotificationState);
    }

    [TestMethod]
    public void UT_NTF_10_A_notification_that_cannot_be_moved_stays_active()
    {
        var env = new TestEnvironment();
        var publisher = new FakeNotificationPublisher();
        _ = Attach(env, publisher);
        var created = env.Submit(TestEnvironment.Anna);
        var earlier = Ids(Reload(env, created.Id));
        publisher.FailArchive = true;

        _ = env.Errors.Cancel(TestEnvironment.Anna, created.Id, created.Version, "Pressed by mistake");

        var kept = Ids(Reload(env, created.Id));
        CollectionAssert.IsSubsetOf(earlier, kept, "still in the list");
        Assert.AreEqual(ErrorRecord.NOTIFICATION_ACTIVE, Reload(env, created.Id).NotificationState);
    }

    [TestMethod]
    public void UT_NTF_11_A_notification_that_its_recipient_dismissed_is_forgotten_and_the_last_one_archives_the_state()
    {
        var env = new TestEnvironment();
        var publisher = new FakeNotificationPublisher();
        var observer = Attach(env, publisher);
        var created = env.Submit(TestEnvironment.Anna);
        var ids = Ids(Reload(env, created.Id)).Select(Guid.Parse).ToList();

        observer.NotificationDismissed(created.Id, ids[0]);
        observer.NotificationDismissed(created.Id, ids[0]);
        Assert.AreEqual(2, Ids(Reload(env, created.Id)).Length, "dismissing twice changes nothing");
        Assert.AreEqual(ErrorRecord.NOTIFICATION_ACTIVE, Reload(env, created.Id).NotificationState);

        observer.NotificationDismissed(created.Id, ids[1]);
        observer.NotificationDismissed(created.Id, ids[2]);

        Assert.AreEqual(0, Ids(Reload(env, created.Id)).Length);
        Assert.AreEqual(ErrorRecord.NOTIFICATION_ARCHIVED, Reload(env, created.Id).NotificationState);
    }

    [TestMethod]
    public void UT_NTF_12_Somebody_who_lost_the_permission_is_not_told()
    {
        var env = new TestEnvironment();
        var created = env.Submit(TestEnvironment.Anna);
        _ = env.Errors.Assign(TestEnvironment.Ben, created.Id, TestEnvironment.Gil, created.Version);
        _ = env.Source.Granted[Constants.Permissions.REPORT_ERRORS].Remove(TestEnvironment.Anna);
        _ = env.Source.Granted[Constants.Permissions.MANAGE_ERRORS].Remove(TestEnvironment.Gil);
        var publisher = new FakeNotificationPublisher();
        _ = Attach(env, publisher);

        _ = env.Errors.AddNote(TestEnvironment.Ben, created.Id, "Hello");

        Assert.AreEqual(0, publisher.Published.Count, "the reporter may not report any more and the assignee may not manage errors");
        Assert.AreEqual(ErrorRecord.NOTIFICATION_NONE, Reload(env, created.Id).NotificationState);
    }

    [TestMethod]
    public void UT_NTF_13_The_background_queue_keeps_the_order_of_the_changes()
    {
        var env = new TestEnvironment();
        var publisher = new FakeNotificationPublisher();
        using var stop = new CancellationTokenSource();
        _ = Attach(env, publisher, false, stop.Token);

        var created = env.Submit(TestEnvironment.Anna);
        _ = env.Errors.Cancel(TestEnvironment.Anna, created.Id, created.Version, "Pressed by mistake");

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && (publisher.Published.Count < 6 || Ids(Reload(env, created.Id)).Length < 3))
        {
            Thread.Sleep(20);
        }

        stop.Cancel();
        Assert.AreEqual(6, publisher.Published.Count, "3 for the new error, 3 for the cancellation");
        Assert.AreEqual(3, publisher.Archived.Count, "created first, the first three archived after");
        Assert.AreEqual(NotificationKinds.NEW_ERROR, publisher.Published[0].Message.Kind);
        Assert.AreEqual(NotificationKinds.STATUS_CHANGED, publisher.Published[5].Message.Kind);
    }

    [TestMethod]
    public void UT_NTF_14_The_words_of_a_status_and_a_severity_are_given_in_german_and_english()
    {
        Assert.AreEqual("In Bearbeitung", NotificationTexts.Detail(NotificationKinds.STATUS_CHANGED, "IN_PROGRESS", true));
        Assert.AreEqual("In progress", NotificationTexts.Detail(NotificationKinds.STATUS_CHANGED, "IN_PROGRESS", false));
        Assert.AreEqual("Kritisch", NotificationTexts.Detail(NotificationKinds.SEVERITY_CHANGED, "Critical", true));
        Assert.AreEqual("gil", NotificationTexts.Detail(NotificationKinds.ASSIGNED, "gil", true), "a name stays");
        Assert.AreEqual("odd", NotificationTexts.Detail(NotificationKinds.STATUS_CHANGED, "odd", false), "an unknown value stays");
    }

    [TestMethod]
    public void UT_NTF_15_A_new_error_has_a_type_for_each_severity_and_the_other_kinds_one_type()
    {
        Assert.AreEqual("EMT_NOTIF_NEW_ERROR_CRITICAL", IlpNotificationPublisher.GetTypeIdentifier(NotificationKinds.NEW_ERROR, Severity.Critical));
        Assert.AreEqual("EMT_NOTIF_ASSIGNED", IlpNotificationPublisher.GetTypeIdentifier(NotificationKinds.ASSIGNED, Severity.Critical));
    }

    [TestMethod]
    public void UT_NTF_17_The_open_button_asks_the_page_of_the_recipient_to_open_the_error_and_nobody_else_even_when_the_notification_cannot_be_archived()
    {
        var errorId = Guid.NewGuid();
        var notification = FSDEV.MOD.Notifications.Notifications.Notification.CreateNewNotification("EMT_NOTIF_NEW_ERROR_ERROR", errorId, Guid.NewGuid(), TestEnvironment.Gil, "Emt", new Dictionary<string, object>());
        var asked = new List<(Guid UserId, Guid ErrorId)>();
        var jobs = new List<Action>();
        var publisher = new IlpNotificationPublisher(new FakeIlp(), "Emt", Guid.NewGuid()) { OpenRequested = (user, error) => asked.Add((user, error)), Background = jobs.Add };

        publisher.HandleOpen(TestEnvironment.Ben, notification);
        Assert.AreEqual(0, jobs.Count, "a notification of somebody else is not even handed to the background");
        Assert.AreEqual(0, asked.Count, "a notification of somebody else");

        publisher.HandleOpen(TestEnvironment.Gil, notification);
        Assert.AreEqual(0, asked.Count, "the ILP client calls the button on its main thread: nothing is done there, it could never write to ILP");
        Assert.AreEqual(1, jobs.Count);

        jobs[0]();
        CollectionAssert.AreEqual(new[] { (TestEnvironment.Gil, errorId) }, asked);
    }

    [TestMethod]
    public void UT_NTF_18_The_push_to_open_an_error_goes_to_the_one_user_only()
    {
        var env = new TestEnvironment();
        var errorId = Guid.NewGuid();

        env.Push.SendOpenError(TestEnvironment.Gil, errorId);

        var sent = env.Sender.Sent.Single();
        CollectionAssert.AreEqual(new[] { TestEnvironment.Gil }, sent.Users.ToArray());
        Assert.AreEqual(errorId.ToString(), sent.Message[0].Value<string>(Constants.Frontend.Push.OPEN_ERROR));
        Assert.AreEqual(errorId.ToString(), sent.Message[0].Value<string>("ErrorId"));
    }

    private static JObject DefaultConfig() => JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ModuleConfig", "defaultModuleConfig.json")));

    private static JObject IlpConfig(JObject? notifications)
    {
        var config = new JObject { ["ModuleVersion"] = "1.0.0", ["ModuleInstanceName"] = "Emt", ["Active"] = true };
        if (notifications is not null)
        {
            config["Notifications"] = notifications;
        }

        return config;
    }

    [TestMethod]
    public void UT_NTF_16_The_notifications_are_off_unless_they_are_enabled_with_a_facility()
    {
        var facility = Guid.NewGuid();

        Assert.IsTrue(ModuleConfig.TryCreate(Guid.NewGuid(), IlpConfig(null), DefaultConfig(), out var byDefault));
        Assert.IsFalse(byDefault.NotificationsEnabled, "default");

        Assert.IsTrue(ModuleConfig.TryCreate(Guid.NewGuid(), IlpConfig(new JObject { ["Enabled"] = true, ["FacilityId"] = facility.ToString() }), DefaultConfig(), out var enabled));
        Assert.IsTrue(enabled.NotificationsEnabled);
        Assert.AreEqual(facility, enabled.NotificationsFacilityId);

        Assert.IsTrue(ModuleConfig.TryCreate(Guid.NewGuid(), IlpConfig(new JObject { ["Enabled"] = true, ["FacilityId"] = "no guid" }), DefaultConfig(), out var badFacility));
        Assert.IsFalse(badFacility.NotificationsEnabled, "enabled without a valid facility is switched off");

        Assert.IsTrue(ModuleConfig.TryCreate(Guid.NewGuid(), IlpConfig(new JObject { ["Enabled"] = false, ["FacilityId"] = facility.ToString() }), DefaultConfig(), out var off));
        Assert.IsFalse(off.NotificationsEnabled);
    }
}
