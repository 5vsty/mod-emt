using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Emt.Emt_Server.Catalog;
using System.Diagnostics.CodeAnalysis;

namespace FSDEV.MOD.Emt.Emt_Test;

internal class FakeCodeGenerator : IErrorCodeGenerator
{
    public int Next { get; set; } = 10000001;
    public bool Fail { get; set; }
    public int Asked { get; private set; }

    public bool TryRegister() => !Fail;

    public bool TryGenerate([NotNullWhen(true)] out string? code)
    {
        Asked++;
        code = null;
        if (Fail)
        {
            return false;
        }

        code = $"EC{Next++}";
        return true;
    }
}

/// <summary>The code of a new error code comes from the IdManager, the user does not type it.</summary>
[TestClass]
public class CodeGenerationTests
{
    private static CatalogAdminService CreateAdmin(TestEnvironment env, FakeCodeGenerator generator) =>
        new(env.Catalog, env.Settings, env.Permissions, env.Index, env.Retention, env.Push, env.Clock, "Disabled", generator);

    private static OperationResult<ErrorCode> Create(CatalogAdminService admin, string? code = "TYPED-1", string? title = "Label printer jams")
        => admin.SaveCode(TestEnvironment.Fay, null, null, code, title, string.Empty, string.Empty, "Printer", "Error", true);

    [TestMethod]
    public void UT_GEN_01_A_new_error_code_gets_its_code_from_the_generator_and_the_typed_code_is_ignored()
    {
        var env = new TestEnvironment();
        var admin = CreateAdmin(env, new FakeCodeGenerator());

        var first = Create(admin);
        var second = Create(admin, code: null, title: "Another one");

        Assert.IsTrue(first.Successful, first.Reason);
        Assert.AreEqual("EC10000001", first.Value!.Code);
        Assert.IsTrue(second.Successful, second.Reason);
        Assert.AreEqual("EC10000002", second.Value!.Code, "no code needed from the page");
        Assert.IsFalse(env.Catalog.GetAll().Any(code => code.Code == "TYPED-1"));
    }

    [TestMethod]
    public void UT_GEN_02_The_code_of_an_existing_error_code_never_changes_whatever_the_page_sends()
    {
        var env = new TestEnvironment();
        var generator = new FakeCodeGenerator();
        var admin = CreateAdmin(env, generator);
        var created = Create(admin).Value!;

        var edited = admin.SaveCode(TestEnvironment.Fay, created.Id, created.Version, "SOMETHING-ELSE", "New title", string.Empty, string.Empty, "Printer", "Warning", true);

        Assert.IsTrue(edited.Successful, edited.Reason);
        Assert.AreEqual(created.Code, edited.Value!.Code);
        Assert.AreEqual("New title", edited.Value.Title);
        Assert.AreEqual(1, generator.Asked, "editing asks for no code");
    }

    [TestMethod]
    public void UT_GEN_03_An_error_code_used_by_an_error_can_still_be_edited_because_its_code_stays()
    {
        var env = new TestEnvironment();
        var admin = CreateAdmin(env, new FakeCodeGenerator());
        var created = Create(admin).Value!;
        _ = env.Submit(TestEnvironment.Anna, created.Id);

        var edited = admin.SaveCode(TestEnvironment.Fay, created.Id, env.Catalog.GetAll().First(code => code.Id == created.Id).Version, "OTHER-TYPED", "Renamed", string.Empty, string.Empty, "Printer", "Error", true);

        Assert.IsTrue(edited.Successful, edited.Reason);
        Assert.AreEqual(created.Code, edited.Value!.Code);
    }

    [TestMethod]
    public void UT_GEN_04_Without_the_id_manager_no_error_code_is_created()
    {
        var env = new TestEnvironment();
        var generator = new FakeCodeGenerator { Fail = true };
        var admin = CreateAdmin(env, generator);
        var before = env.Catalog.GetAll().Count;

        var result = Create(admin);

        Assert.IsFalse(result.Successful);
        Assert.AreEqual(Reasons.UNAVAILABLE, result.Reason);
        Assert.AreEqual(before, env.Catalog.GetAll().Count);
    }

    [TestMethod]
    public void UT_GEN_05_A_refused_request_does_not_use_up_a_number_of_the_series()
    {
        var env = new TestEnvironment();
        var generator = new FakeCodeGenerator();
        var admin = CreateAdmin(env, generator);

        var refused = Create(admin, title: string.Empty);
        var accepted = Create(admin);

        Assert.IsFalse(refused.Successful);
        Assert.AreEqual(Reasons.VALIDATION_FAILED, refused.Reason);
        Assert.AreEqual(1, generator.Asked, "only the accepted request asked");
        Assert.AreEqual("EC10000001", accepted.Value!.Code, "no gap in the series");
    }

    [TestMethod]
    public void UT_GEN_06_The_user_without_the_permission_asks_for_no_code()
    {
        var env = new TestEnvironment();
        var generator = new FakeCodeGenerator();
        var admin = CreateAdmin(env, generator);

        var result = admin.SaveCode(TestEnvironment.Anna, null, null, null, "Title", string.Empty, string.Empty, "Printer", "Error", true);

        Assert.AreEqual(Reasons.FORBIDDEN, result.Reason);
        Assert.AreEqual(0, generator.Asked);
    }
}
