using FSDEV.ILP.Intralogistikplattform.SystemState;
using FSDEV.ILP.Intralogistikplattform.SystemState.States;
using FSDEV.ILP.Intralogistikplattform.SystemState.States.CollectionStates;
using FSDEV.ILP.Intralogistikplattform.SystemState.States.PrimitiveStates;
using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Emt.Emt_Server.Catalog;
using Constants = FSDEV.MOD.Emt.Emt.Constants;

namespace FSDEV.MOD.Emt.Emt_Test;

/// <summary>
/// The catalog is written to ILP with a ChangeRequest. FakeIlp runs the same MergeChanges step as the real client controller,
/// so a state that ILP would reject with REJECTED_SANITY_CHECKS_FAILED fails here as well.
/// </summary>
[TestClass]
public class CatalogStorageTests
{
    private static FakeIlp IlpWithEmptyCatalog()
    {
        var ilp = new FakeIlp();
        ilp.Put(new DictionaryState<StringState, IBaseState>(Constants.Ilp.Paths.ERROR_CODES));
        return ilp;
    }

    [TestMethod]
    public void TrySave_OtherCode_IsAcceptedByIlp()
    {
        var ilp = IlpWithEmptyCatalog();
        var other = CatalogStore.CreateOtherCode(1_700_000_000_000);

        var saved = new IlpCatalogStorage(ilp).TrySave(other);
        var creates = ilp.Sent.SelectMany(request => request.Changes).OfType<FSDEV.ILP.Intralogistikplattform.SystemState.StateChanges.Changes.CreateState>().Select(create => create.FutureState.Key).ToList();
        var duplicates = creates.GroupBy(key => key).Where(group => group.Count() > 1).Select(group => $"{group.Key} x{group.Count()}");
        Assert.IsTrue(saved, "duplicates: " + string.Join(" | ", duplicates) + " || all creates: " + string.Join(" | ", creates));
        Assert.AreEqual(1, ilp.Sent.Count);
    }

    [TestMethod]
    public void TrySave_Code_IsAcceptedByIlp()
    {
        var ilp = IlpWithEmptyCatalog();
        var code = new ErrorCode
        {
            Id = Guid.NewGuid(),
            Code = "SCAN-01",
            Title = "Scanner defekt",
            Description = "Beschreibung",
            Instructions = string.Empty,
            Group = "Scanner",
            DefaultSeverity = Severity.Error,
            ChangedAt = 1_700_000_000_000,
            ChangedBy = "anna",
        };

        Assert.IsTrue(new IlpCatalogStorage(ilp).TrySave(code));
    }

    [TestMethod]
    public void TryLoadAll_CodeWithTextsSavedPerLanguage_IsReadAsSingleTexts()
    {
        var id = Guid.NewGuid();
        var root = new DictionaryState<StringState, IBaseState>(Constants.Ilp.Paths.ERROR_CODES)
        {
            [new StringState(id.ToString())] = new DictionaryState<StringState, IBaseState>(id.ToString())
            {
                [new StringState("Id")] = new StringState(id.ToString()),
                [new StringState("Code")] = new StringState("SCAN-01"),
                [new StringState("Title")] = PerLanguage(("en", "Scanner broken"), ("de", "Scanner defekt")),
                [new StringState("Description")] = PerLanguage(("en", "Only english")),
                [new StringState("Instructions")] = PerLanguage(),
                [new StringState("Group")] = new StringState("Scanner"),
                [new StringState("DefaultSeverity")] = new StringState(Severity.Error.ToWire()),
                [new StringState("Active")] = new BoolState(true),
                [new StringState("IsOther")] = new BoolState(false),
                [new StringState("Scope")] = new StringState(ErrorCode.SCOPE_ALL),
                [new StringState("UsedAt")] = new LongState(0),
                [new StringState("Version")] = new IntState(1),
                [new StringState("ChangedAt")] = new LongState(1_700_000_000_000),
                [new StringState("ChangedBy")] = new StringState("anna"),
            },
        };
        var ilp = new FakeIlp();
        ilp.Put(root);

        Assert.IsTrue(new IlpCatalogStorage(ilp).TryLoadAll(out var codes));
        var code = codes.Single();
        Assert.AreEqual("Scanner defekt", code.Title);
        Assert.AreEqual("Only english", code.Description);
        Assert.AreEqual(string.Empty, code.Instructions);
    }

    private static DictionaryState<StringState, IBaseState> PerLanguage(params (string Language, string Text)[] texts)
    {
        var state = new DictionaryState<StringState, IBaseState>(Guid.NewGuid().ToString());
        foreach (var (language, text) in texts)
        {
            state[new StringState(language)] = new StringState(text);
        }

        return state;
    }
}
