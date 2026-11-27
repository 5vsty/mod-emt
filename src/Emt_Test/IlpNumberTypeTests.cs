using FSDEV.ILP.Intralogistikplattform.SystemState.States;
using FSDEV.ILP.Intralogistikplattform.SystemState.States.CollectionStates;
using FSDEV.ILP.Intralogistikplattform.SystemState.States.PrimitiveStates;
using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Emt.Emt_Server.Catalog;
using FSDEV.MOD.Emt.Emt_Server.Storage;
using Constants = FSDEV.MOD.Emt.Emt.Constants;

namespace FSDEV.MOD.Emt.Emt_Test;

/// <summary>A small number on ILP comes back as an IntState; an entry with such a field must not disappear.</summary>
[TestClass]
public class IlpNumberTypeTests
{
    [TestMethod]
    public void UT_INT_01_An_error_code_whose_long_fields_are_int_states_is_still_read()
    {
        var id = Guid.NewGuid();
        var entry = new DictionaryState<StringState, IBaseState>($"{Constants.Ilp.Paths.ERROR_CODES}/{id}")
        {
            [new StringState("Id")] = new StringState(id.ToString()),
            [new StringState("Code")] = new StringState("EC10000004"),
            [new StringState("Title")] = new StringState("Kein Abtransport"),
            [new StringState("Description")] = new StringState(string.Empty),
            [new StringState("Instructions")] = new StringState(string.Empty),
            [new StringState("Group")] = new StringState(string.Empty),
            [new StringState("DefaultSeverity")] = new StringState("Error"),
            [new StringState("Active")] = new BoolState(true),
            [new StringState("IsOther")] = new BoolState(false),
            [new StringState("Scope")] = new StringState("All"),
            [new StringState("UsedAt")] = new IntState(0),
            [new StringState("Version")] = new IntState(1),
            [new StringState("ChangedAt")] = new LongState(1795750495909),
            [new StringState("ChangedBy")] = new StringState("Admin"),
        };
        var root = new DictionaryState<StringState, IBaseState>(Constants.Ilp.Paths.ERROR_CODES) { [new StringState(id.ToString())] = entry };
        var ilp = new FakeIlp();
        ilp.Put(root);

        Assert.IsTrue(new IlpCatalogStorage(ilp).TryLoadAll(out var codes));
        Assert.AreEqual(1, codes.Count(c => c.Code == "EC10000004"), "not dropped because UsedAt is an int");
    }

    [TestMethod]
    public void UT_INT_02_An_error_whose_long_fields_are_int_states_is_still_read()
    {
        var record = new ErrorRecord
        {
            Id = Guid.NewGuid(),
            Number = 10000009,
            ErrorNumber = "ER10000009",
            ClientRequestId = Guid.NewGuid(),
            Status = ErrorStatus.New,
            Severity = Severity.Warning,
            ErrorCodeId = Guid.NewGuid(),
            ErrorCode = "EC1",
            CodeTitle = "t",
            ReporterUserId = Guid.NewGuid(),
            ReporterName = "anna",
            ReportedAt = 1_763_900_000_000,
            LastChangedAt = 1_763_900_000_000,
            LastChangedByName = "anna",
            Version = 1,
        };
        record.AddEvent(ErrorEventTypes.CREATED, record.ReporterUserId, "anna", record.ReportedAt);
        var state = ErrorRecordIlpMapper.ToIlpState(record);
        state[new StringState("FinalizedAt")] = new IntState(0);
        state[new StringState("Number")] = new IntState(10000009);

        Assert.IsTrue(ErrorRecordIlpMapper.TryParse(state, out var parsed));
        Assert.AreEqual(10000009L, parsed.Number);
        Assert.AreEqual(0L, parsed.FinalizedAt);
    }

    [TestMethod]
    public void UT_INT_03_The_error_counter_can_be_an_int_state()
    {
        var ilp = new FakeIlp();
        ilp.Put(new IntState(Constants.Ilp.Paths.ERROR_NUMBER, 8));

        var store = new IlpErrorStore(ilp);

        Assert.IsTrue(store.TryReadSequence(out var value));
        Assert.AreEqual(8L, value);
    }
}
