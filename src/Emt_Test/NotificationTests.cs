using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Emt.Emt.ModuleConfig;
using FSDEV.MOD.Emt.Emt_Server.Notifications;
using Newtonsoft.Json.Linq;
using System.Diagnostics.CodeAnalysis;

namespace FSDEV.MOD.Emt.Emt_Test;

internal class FakeNotificationPublisher : INotificationPublisher
{
    public List<ErrorRecord> Published { get; } = [];
    public List<string> Archived { get; } = [];
    public bool FailPublish { get; set; }
    public bool FailArchive { get; set; }
    public bool ThrowOnPublish { get; set; }

    public bool TryPublish(ErrorRecord error, [NotNullWhen(true)] out string? notificationId)
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

        Published.Add(error);
        notificationId = Guid.NewGuid().ToString();
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
}

[TestClass]
public class NotificationTests
{
    private static NotificationObserver Attach(TestEnvironment env, FakeNotificationPublisher publisher)
    {
        var observer = new NotificationObserver(publisher, env.Errors, CancellationToken.None, true);
        env.Errors.AddObserver(observer);
        return observer;
    }

    private static ErrorRecord Reload(TestEnvironment env, Guid errorId)
    {
        Assert.IsTrue(env.Index.TryGet(errorId, out var error));
        return error;
    }

    [TestMethod]
    public void UT_NTF_01_A_new_error_creates_one_notification_and_remembers_it_without_changing_the_error()
    {
        var env = new TestEnvironment();
        var publisher = new FakeNotificationPublisher();
        _ = Attach(env, publisher);

        var created = env.Submit();

        Assert.AreEqual(1, publisher.Published.Count);
        Assert.AreEqual(created.Id, publisher.Published[0].Id);
        var error = Reload(env, created.Id);
        Assert.AreEqual(ErrorRecord.NOTIFICATION_ACTIVE, error.NotificationState);
        Assert.IsTrue(Guid.TryParse(error.NotificationId, out _));
        Assert.AreEqual(created.Version, error.Version, "the notification is no change of the error");
        Assert.AreEqual(created.Events.Count, error.Events.Count, "no event in the timeline");
        Assert.AreEqual(error.NotificationId, env.Store.Records[created.Id].NotificationId, "written to the store");
    }

    [TestMethod]
    public void UT_NTF_02_A_resolved_error_archives_its_notification_and_re_opening_does_not_create_a_new_one()
    {
        var env = new TestEnvironment();
        var publisher = new FakeNotificationPublisher();
        _ = Attach(env, publisher);
        var created = env.Submit();
        var notificationId = Reload(env, created.Id).NotificationId;

        _ = env.Errors.ChangeStatus(TestEnvironment.Ben, created.Id, "Acknowledge", Reload(env, created.Id).Version, null);
        Assert.AreEqual(0, publisher.Archived.Count, "a handled error keeps its notification");

        _ = env.Errors.ChangeStatus(TestEnvironment.Ben, created.Id, "StartProgress", Reload(env, created.Id).Version, null);
        _ = env.Errors.ChangeStatus(TestEnvironment.Ben, created.Id, "Resolve", Reload(env, created.Id).Version, "Label roll replaced");

        CollectionAssert.AreEqual(new[] { notificationId }, publisher.Archived.ToArray());
        Assert.AreEqual(ErrorRecord.NOTIFICATION_ARCHIVED, Reload(env, created.Id).NotificationState);

        _ = env.Errors.ChangeStatus(TestEnvironment.Ben, created.Id, "Reopen", Reload(env, created.Id).Version, "Came back");
        _ = env.Errors.ChangeStatus(TestEnvironment.Ben, created.Id, "Resolve", Reload(env, created.Id).Version, "Fixed again");
        _ = env.Errors.ChangeStatus(TestEnvironment.Ben, created.Id, "Close", Reload(env, created.Id).Version, null);

        Assert.AreEqual(1, publisher.Published.Count, "BR-17: re-opening creates no new notification");
        Assert.AreEqual(1, publisher.Archived.Count, "an archived notification is not archived again");
    }

    [TestMethod]
    public void UT_NTF_03_A_cancelled_error_archives_its_notification()
    {
        var env = new TestEnvironment();
        var publisher = new FakeNotificationPublisher();
        _ = Attach(env, publisher);
        var created = env.Submit();

        var result = env.Errors.Cancel(TestEnvironment.Anna, created.Id, created.Version, "Pressed by mistake");

        Assert.IsTrue(result.Successful, result.Reason);
        Assert.AreEqual(1, publisher.Archived.Count);
        Assert.AreEqual(ErrorRecord.NOTIFICATION_ARCHIVED, Reload(env, created.Id).NotificationState);
    }

    [TestMethod]
    public void UT_NTF_04_A_failing_publisher_never_fails_the_report_and_the_error_is_marked()
    {
        var env = new TestEnvironment();
        var publisher = new FakeNotificationPublisher { FailPublish = true };
        _ = Attach(env, publisher);

        var created = env.Submit();

        Assert.AreEqual(ErrorStatus.New, created.Status);
        Assert.AreEqual(ErrorRecord.NOTIFICATION_FAILED, Reload(env, created.Id).NotificationState);

        _ = env.Errors.ChangeStatus(TestEnvironment.Ben, created.Id, "Reject", Reload(env, created.Id).Version, "Wrong report");
        Assert.AreEqual(0, publisher.Archived.Count, "nothing to archive");
    }

    [TestMethod]
    public void UT_NTF_05_A_publisher_that_throws_never_fails_the_report()
    {
        var env = new TestEnvironment();
        var publisher = new FakeNotificationPublisher { ThrowOnPublish = true };
        _ = Attach(env, publisher);

        var created = env.Submit();

        Assert.IsTrue(env.Store.Records.ContainsKey(created.Id));
        Assert.AreEqual(ErrorRecord.NOTIFICATION_NONE, Reload(env, created.Id).NotificationState);
    }

    [TestMethod]
    public void UT_NTF_06_A_notification_that_cannot_be_archived_stays_active_and_is_archived_with_the_next_change()
    {
        var env = new TestEnvironment();
        var publisher = new FakeNotificationPublisher();
        _ = Attach(env, publisher);
        var created = env.Submit();
        _ = env.Errors.ChangeStatus(TestEnvironment.Ben, created.Id, "StartProgress", Reload(env, created.Id).Version, null);

        publisher.FailArchive = true;
        _ = env.Errors.ChangeStatus(TestEnvironment.Ben, created.Id, "Resolve", Reload(env, created.Id).Version, "Done");
        Assert.AreEqual(ErrorRecord.NOTIFICATION_ACTIVE, Reload(env, created.Id).NotificationState);

        publisher.FailArchive = false;
        _ = env.Errors.ChangeStatus(TestEnvironment.Ben, created.Id, "Close", Reload(env, created.Id).Version, null);
        Assert.AreEqual(ErrorRecord.NOTIFICATION_ARCHIVED, Reload(env, created.Id).NotificationState);
    }

    [TestMethod]
    public void UT_NTF_07_The_background_queue_keeps_the_order_of_the_changes()
    {
        var env = new TestEnvironment();
        var publisher = new FakeNotificationPublisher();
        using var stop = new CancellationTokenSource();
        env.Errors.AddObserver(new NotificationObserver(publisher, env.Errors, stop.Token));

        var created = env.Submit();
        _ = env.Errors.Cancel(TestEnvironment.Anna, created.Id, created.Version, "Pressed by mistake");

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && Reload(env, created.Id).NotificationState != ErrorRecord.NOTIFICATION_ARCHIVED)
        {
            Thread.Sleep(20);
        }

        stop.Cancel();
        Assert.AreEqual(ErrorRecord.NOTIFICATION_ARCHIVED, Reload(env, created.Id).NotificationState, "created first, archived after it");
        Assert.AreEqual(1, publisher.Published.Count);
        Assert.AreEqual(1, publisher.Archived.Count);
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
    public void UT_NTF_08_The_notifications_are_off_unless_they_are_enabled_with_a_facility()
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