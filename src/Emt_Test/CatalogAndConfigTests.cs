using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Emt.Emt.ModuleConfig;
using FSDEV.MOD.Emt.Emt.Validation;
using Newtonsoft.Json.Linq;
using Constants = FSDEV.MOD.Emt.Emt.Constants;

namespace FSDEV.MOD.Emt.Emt_Test;

[TestClass]
public class CatalogAndConfigTests
{
    private const string Title = "Geraet antwortet nicht";
    private const string Empty = "";

    private static OperationResult<ErrorCode> Save(TestEnvironment env, Guid? id, int? version, string code, string? title = null, bool active = true, Guid? user = null) =>
        env.Admin.SaveCode(user ?? TestEnvironment.Fay, id, version, code, title ?? Title, Empty, Empty, "General", "Error", active);

    [TestMethod]
    public void UT_CAT_01_A_new_code_is_saved_active_with_the_entered_values_and_scope_all()
    {
        var env = new TestEnvironment();

        var result = Save(env, null, null, "GEN-99");

        Assert.IsTrue(result.Successful, result.Reason);
        var code = result.Value!;
        Assert.AreEqual("GEN-99", code.Code);
        Assert.AreEqual(Severity.Error, code.DefaultSeverity);
        Assert.IsTrue(code.Active);
        Assert.AreEqual(ErrorCode.SCOPE_ALL, code.Scope);
        Assert.AreEqual(1, code.Version);
        Assert.AreEqual("fay", code.ChangedBy);
        Assert.IsTrue(env.CatalogStorage.Codes.ContainsKey(code.Id), "persisted");
    }

    [TestMethod]
    public void UT_CAT_02_A_code_that_exists_with_other_casing_is_refused_and_nothing_changes()
    {
        var env = new TestEnvironment();
        var before = env.CatalogStorage.Codes.Count;

        var result = Save(env, null, null, "scan-01");

        Assert.AreEqual(Reasons.CODE_EXISTS, result.Reason);
        Assert.AreEqual(before, env.CatalogStorage.Codes.Count);
    }

    [TestMethod]
    public void UT_CAT_03_The_code_of_a_used_code_is_locked_but_its_title_can_change()
    {
        var env = new TestEnvironment();
        _ = env.Submit();
        var scan = env.Catalog.GetAll().Single(c => c.Code == "SCAN-01");
        Assert.IsTrue(scan.IsUsed);

        var renamed = Save(env, scan.Id, scan.Version, "SCAN-02", scan.Title);
        Assert.AreEqual(Reasons.CODE_LOCKED, renamed.Reason);

        var retitled = Save(env, scan.Id, scan.Version, "SCAN-01", "New");
        Assert.IsTrue(retitled.Successful, retitled.Reason);
        Assert.AreEqual("New", retitled.Value!.Title);
        Assert.AreEqual(scan.Version + 1, retitled.Value.Version);
    }

    [TestMethod]
    public void UT_CAT_03_An_unused_code_can_be_renamed()
    {
        var env = new TestEnvironment();
        var created = Save(env, null, null, "TEST-99").Value!;

        var renamed = Save(env, created.Id, created.Version, "TEST-98");

        Assert.IsTrue(renamed.Successful, renamed.Reason);
        Assert.AreEqual("TEST-98", renamed.Value!.Code);
    }

    [TestMethod]
    public void UT_CAT_04_A_used_code_cannot_be_deleted_but_can_be_deactivated()
    {
        var env = new TestEnvironment();
        _ = env.Submit();
        var scan = env.Catalog.GetAll().Single(c => c.Code == "SCAN-01");

        var deletion = env.Admin.DeleteCode(TestEnvironment.Fay, scan.Id);
        Assert.AreEqual(Reasons.CODE_IN_USE, deletion.Reason);

        var deactivated = Save(env, scan.Id, scan.Version, "SCAN-01", scan.Title, false);
        Assert.IsTrue(deactivated.Successful);
        Assert.IsFalse(deactivated.Value!.Active);
    }

    [TestMethod]
    public void UT_CAT_04_A_code_used_by_an_error_in_the_index_is_in_use_even_if_never_marked()
    {
        var env = new TestEnvironment();
        var error = env.Submit();
        var scan = env.Catalog.GetAll().Single(c => c.Code == "SCAN-01");
        env.CatalogStorage.Codes[scan.Id].UsedAt = 0;
        env.Build(); // the mark was lost, the error is still there

        var deletion = env.Admin.DeleteCode(TestEnvironment.Fay, scan.Id);

        Assert.AreEqual(Reasons.CODE_IN_USE, deletion.Reason);
        Assert.IsTrue(env.Index.TryGet(error.Id, out _));
    }

    [TestMethod]
    public void UT_CAT_05_An_unused_code_can_be_deleted()
    {
        var env = new TestEnvironment();
        var created = Save(env, null, null, "TEST-99").Value!;

        var deletion = env.Admin.DeleteCode(TestEnvironment.Fay, created.Id);

        Assert.IsTrue(deletion.Successful, deletion.Reason);
        Assert.IsFalse(env.Catalog.TryGet(created.Id, out _));
        Assert.IsFalse(env.CatalogStorage.Codes.ContainsKey(created.Id));
        Assert.AreEqual(Reasons.NOT_FOUND, env.Admin.DeleteCode(TestEnvironment.Fay, created.Id).Reason);
    }

    [TestMethod]
    public void UT_CAT_The_other_code_is_always_there_and_protected()
    {
        var env = new TestEnvironment();
        var otherId = Guid.Parse(Constants.Catalog.OTHER_CODE_ID);

        var other = env.Catalog.GetAll().Single(c => c.IsOther);
        Assert.AreEqual(otherId, other.Id);

        Assert.AreEqual(Reasons.OTHER_CODE_PROTECTED, env.Admin.DeleteCode(TestEnvironment.Fay, otherId).Reason);
        Assert.AreEqual(Reasons.OTHER_CODE_PROTECTED, Save(env, otherId, other.Version, "OTHER").Reason);
        Assert.AreEqual(Reasons.CODE_EXISTS, Save(env, null, null, "other").Reason, "the code of the other code is reserved");

        env.CatalogStorage.Codes.Clear();
        env.Build();
        Assert.IsTrue(env.Catalog.TryGet(otherId, out _), "created again when it is missing");
    }

    [TestMethod]
    public void UT_CAT_Validation_needs_code_and_title()
    {
        var env = new TestEnvironment();

        var noCode = Save(env, null, null, " ");
        Assert.IsTrue(noCode.ValidationErrors.Any(e => e.Field == "Code" && e.MessageKey == ValidationKeys.REQUIRED));

        var noTitle = Save(env, null, null, "X-1", "  ");
        Assert.IsTrue(noTitle.ValidationErrors.Any(e => e.Field == "Title" && e.MessageKey == ValidationKeys.REQUIRED));

        var badSeverity = env.Admin.SaveCode(TestEnvironment.Fay, null, null, "X-2", Title, Empty, Empty, "g", "Fatal", true);
        Assert.IsTrue(badSeverity.ValidationErrors.Any(e => e.Field == "DefaultSeverity"));

        var tooLong = Save(env, null, null, new string('A', Constants.Limits.MAX_CODE_LENGTH + 1));
        Assert.IsTrue(tooLong.ValidationErrors.Any(e => e.MessageKey == ValidationKeys.TOO_LONG));
    }

    [TestMethod]
    public void UT_CAT_Stale_version_is_a_conflict()
    {
        var env = new TestEnvironment();
        var created = Save(env, null, null, "TEST-99").Value!;
        _ = Save(env, created.Id, created.Version, "TEST-99", Title, false);

        var stale = Save(env, created.Id, created.Version, "TEST-99");

        Assert.AreEqual(Reasons.CONFLICT, stale.Reason);
        Assert.AreEqual("fay", stale.ConflictByName);
    }

    [TestMethod]
    public void UT_CAT_A_failed_write_leaves_the_catalog_unchanged()
    {
        var env = new TestEnvironment();
        env.CatalogStorage.FailSave = true;

        var result = Save(env, null, null, "TEST-99");

        Assert.AreEqual(Reasons.UNAVAILABLE, result.Reason);
        Assert.IsFalse(env.Catalog.GetAll().Any(c => c.Code == "TEST-99"));
    }

    [TestMethod]
    public void UT_CAT_Catalog_changes_are_pushed_to_reporters_and_catalog_managers()
    {
        var env = new TestEnvironment();
        env.Sender.Sent.Clear();

        _ = Save(env, null, null, "TEST-99");

        var (users, message) = env.Sender.Sent.Single();
        CollectionAssert.AreEquivalent(new[] { TestEnvironment.Anna, TestEnvironment.Chris, TestEnvironment.Hana, TestEnvironment.Fay }, users.ToArray());
        Assert.IsTrue(((JObject)message[0]).ContainsKey(Constants.Frontend.Push.ERROR_CODES_CHANGED));
    }

    [TestMethod]
    public void UT_CAT_Settings_keep_at_least_30_days_and_use_versions()
    {
        var env = new TestEnvironment();
        var settings = env.Settings.Current;
        Assert.AreEqual(365, settings.RetentionDays, "default retention");

        var tooShort = env.Admin.SaveSettings(TestEnvironment.Fay, 29, true, settings.Version);
        Assert.IsTrue(tooShort.ValidationErrors.Any(e => e.Field == "RetentionDays"));

        var saved = env.Admin.SaveSettings(TestEnvironment.Fay, 30, true, settings.Version);
        Assert.IsTrue(saved.Successful, saved.Reason);
        Assert.AreEqual(30, saved.Value!.Settings.RetentionDays);
        Assert.AreEqual(settings.Version + 1, saved.Value.Settings.Version);

        var stale = env.Admin.SaveSettings(TestEnvironment.Fay, 60, true, settings.Version);
        Assert.AreEqual(Reasons.CONFLICT, stale.Reason);
        Assert.AreEqual(30, stale.Value!.Settings.RetentionDays);
    }

    [TestMethod]
    public void UT_CAT_Health_reports_managers_and_the_fixed_values_of_the_postponed_parts()
    {
        var env = new TestEnvironment();
        _ = env.Submit();

        var health = env.Admin.GetSettings(TestEnvironment.Fay).Value!.Health;

        Assert.AreEqual(3, (int)health["ManagersCount"]!);
        Assert.AreEqual(1, (int)health["IlpErrorCount"]!);
        Assert.AreEqual("Disabled", (string?)health["NotificationsState"]);
        Assert.AreEqual("Disabled", (string?)health["ArchiveState"]);
        Assert.AreEqual(0, (int)health["ArchivedErrorCount"]!);
    }

    // ---- ModuleConfig ----

    private static JObject DefaultConfig() => JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ModuleConfig", "defaultModuleConfig.json")));

    [TestMethod]
    public void UT_CFG_01_Default_config_is_inactive_and_ilp_values_override_it()
    {
        var ilpConfig = new JObject
        {
            ["ModuleVersion"] = "1.0.0",
            ["ModuleInstanceName"] = "emt-1",
            ["Retention"] = new JObject { ["CleanupIntervalHours"] = 6 },
            ["Location"] = new JObject { ["SelectableAreaTypes"] = new JArray("Room", "Zone") },
        };

        Assert.IsTrue(ModuleConfig.TryCreate(Guid.NewGuid(), ilpConfig, DefaultConfig(), out var config));

        Assert.IsFalse(config.ModuleActive, "EX-13: switched off by default");
        Assert.AreEqual(6, config.CleanupIntervalHours);
        Assert.AreEqual(24, DefaultConfig()["Retention"]!["CleanupIntervalHours"]!.Value<int>());
        CollectionAssert.AreEqual(new[] { "Room", "Zone" }, config.SelectableAreaTypes.ToArray());
        Assert.AreEqual("EMT-", config.ErrorNumberPrefix);
        Assert.AreEqual(6, config.ErrorNumberMinDigits);
        Assert.AreEqual(1000, config.LiveUpdateDebounceMs);
        Assert.AreEqual(1, config.LocationFdfVersion);
        Assert.AreEqual("Config/project.json", config.LocationProjectPath);
    }

    [TestMethod]
    public void UT_CFG_01_Active_flag_is_taken_from_ilp()
    {
        var ilpConfig = new JObject { ["ModuleVersion"] = "1", ["ModuleInstanceName"] = "x", ["Active"] = true };

        Assert.IsTrue(ModuleConfig.TryCreate(Guid.NewGuid(), ilpConfig, DefaultConfig(), out var config));
        Assert.IsTrue(config.ModuleActive);
    }

    [TestMethod]
    public void UT_CFG_01_The_config_for_the_frontend_has_no_archive_path_and_no_sql_data()
    {
        var ilpConfig = new JObject { ["ModuleVersion"] = "1", ["ModuleInstanceName"] = "x", ["Active"] = true };

        Assert.IsTrue(ModuleConfig.TryCreate(Guid.NewGuid(), ilpConfig, DefaultConfig(), out var config));

        var json = config.JsonRepresentation.ToString();
        Assert.IsFalse(json.Contains("Archive", StringComparison.OrdinalIgnoreCase), "D-11: no archive");
        Assert.IsFalse(json.Contains("Sql", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void UT_CFG_01_Missing_version_or_out_of_range_values_make_the_config_invalid()
    {
        Assert.IsFalse(ModuleConfig.TryCreate(Guid.NewGuid(), new JObject { ["ModuleInstanceName"] = "x" }, DefaultConfig(), out _), "ModuleVersion missing");

        var badInterval = new JObject
        {
            ["ModuleVersion"] = "1",
            ["ModuleInstanceName"] = "x",
            ["Retention"] = new JObject { ["CleanupIntervalHours"] = 0 },
        };
        Assert.IsFalse(ModuleConfig.TryCreate(Guid.NewGuid(), badInterval, DefaultConfig(), out _));

        var wrongType = new JObject { ["ModuleVersion"] = "1", ["ModuleInstanceName"] = "x", ["Active"] = "yes" };
        Assert.IsFalse(ModuleConfig.TryCreate(Guid.NewGuid(), wrongType, DefaultConfig(), out _));
    }
}
