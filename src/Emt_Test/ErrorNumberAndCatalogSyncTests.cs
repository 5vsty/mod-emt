using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Emt.Emt_Server.Catalog;
using FSDEV.MOD.Emt.Emt_Server.Errors;
using FSDEV.MOD.Emt.Emt_Server.Storage;

namespace FSDEV.MOD.Emt.Emt_Test;

/// <summary>The numbers of the errors come from the IdManager; the catalog follows the states on ILP.</summary>
[TestClass]
public class ErrorNumberAndCatalogSyncTests
{
    [TestMethod]
    public void UT_NUM_01_The_number_of_a_new_error_is_the_number_of_the_series_and_its_digits_sort_after_the_old_numbers()
    {
        var series = new FakeSeries("ER", 10000009);
        var source = new IdManagerErrorNumberSource(series);

        Assert.IsTrue(source.TryReserve(out var number, out var text));
        Assert.AreEqual("ER10000009", text);
        Assert.AreEqual(10000009L, number);
        Assert.IsTrue(source.TryReserve(out var second, out _));
        Assert.IsTrue(second > number);
        Assert.IsTrue(number > 8, "the errors of the earlier version have numbers 1…8 and stay before");
    }

    [TestMethod]
    public void UT_NUM_02_Without_an_answer_of_the_IdManager_or_with_a_wrong_answer_there_is_no_number()
    {
        Assert.IsFalse(new IdManagerErrorNumberSource(new FakeSeries("ER", 1) { Fail = true }).TryReserve(out _, out var text));
        Assert.IsNull(text);
        Assert.IsFalse(new IdManagerErrorNumberSource(new FakeSeries("XX", 5)).TryReserve(out _, out _), "another series");
        Assert.IsFalse(new IdManagerErrorNumberSource(new FakeSeries("ER", 5) { Suffix = "abc" }).TryReserve(out _, out _), "no digits");
    }

    [TestMethod]
    public void UT_NUM_03_An_error_is_not_created_without_a_number_and_nothing_is_written()
    {
        var env = new TestEnvironment();
        var series = new FakeSeries("ER", 10000001) { Fail = true };
        var errors = new ErrorService(env.Store, env.Index, new IdManagerErrorNumberSource(series), env.Catalog, env.Locations, env.Permissions, env.Clock);

        var failed = errors.Submit(TestEnvironment.Anna, Guid.NewGuid(), TestEnvironment.Hall1, env.ScanCode, "x");

        Assert.IsFalse(failed.Successful);
        Assert.AreEqual(Reasons.UNAVAILABLE, failed.Reason);
        Assert.AreEqual(0, env.Store.Created.Count);

        series.Fail = false;
        var created = errors.Submit(TestEnvironment.Anna, Guid.NewGuid(), TestEnvironment.Hall1, env.ScanCode, "x");
        Assert.IsTrue(created.Successful, created.Reason);
        Assert.AreEqual("ER10000001", created.Value!.Error.ErrorNumber);
        Assert.AreEqual(10000001L, created.Value.Error.Number);
    }

    [TestMethod]
    public void UT_SYNC_01_Codes_that_appear_on_the_storage_later_are_in_the_catalog_after_a_reload_and_the_table_is_told_once()
    {
        var env = new TestEnvironment();
        var before = env.Catalog.GetAll().Count;
        var added = new ErrorCode { Id = Guid.NewGuid(), Code = "EC10000002", Title = "From the files", DefaultSeverity = Severity.Error, Active = true, Version = 1, ChangedAt = 5 };
        env.CatalogStorage.Codes[added.Id] = added;
        var changes = 0;
        env.Catalog.Changed += () => changes++;

        Assert.IsTrue(env.Catalog.TryReload(out var changed));
        Assert.IsTrue(changed);
        Assert.AreEqual(before + 1, env.Catalog.GetAll().Count);
        Assert.IsTrue(env.Catalog.TryGet(added.Id, out _));
        Assert.AreEqual(1, changes);

        Assert.IsTrue(env.Catalog.TryReload(out changed));
        Assert.IsFalse(changed, "nothing new");
        Assert.AreEqual(1, changes);
    }

    [TestMethod]
    public void UT_SYNC_02_A_reload_keeps_the_other_code_even_when_the_storage_does_not_have_it_yet()
    {
        var env = new TestEnvironment();
        env.CatalogStorage.Codes.Remove(Guid.Parse(FSDEV.MOD.Emt.Emt.Constants.Catalog.OTHER_CODE_ID));

        Assert.IsTrue(env.Catalog.TryReload(out _));

        Assert.IsTrue(env.Catalog.GetAll().Any(code => code.IsOther));
    }

    private sealed class FakeSeries : IErrorCodeGenerator
    {
        private readonly string Prefix;
        private long Next;

        public FakeSeries(string prefix, long start)
        {
            Prefix = prefix;
            Next = start;
        }

        public bool Fail { get; set; }
        public string? Suffix { get; set; }

        public bool TryRegister() => !Fail;

        public bool TryGenerate(out string? code)
        {
            code = null;
            if (Fail)
            {
                return false;
            }

            code = Prefix + (Suffix ?? (Next++).ToString());
            return true;
        }
    }
}
