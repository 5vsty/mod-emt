using FSDEV.ILP.Intralogistikplattform.SystemState;
using FSDEV.ILP.Intralogistikplattform.SystemState.Client;
using FSDEV.ILP.Intralogistikplattform.SystemState.Deserializer;
using FSDEV.ILP.Intralogistikplattform.SystemState.Messages;
using FSDEV.ILP.Intralogistikplattform.SystemState.Serializer;
using FSDEV.ILP.Intralogistikplattform.SystemState.StateChanges.Changes;
using FSDEV.ILP.Intralogistikplattform.SystemState.States;
using FSDEV.ILP.Intralogistikplattform.SystemState.States.CollectionStates;
using FSDEV.ILP.Intralogistikplattform.SystemState.States.PrimitiveStates;
using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Emt.Emt_Server.Storage;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Constants = FSDEV.MOD.Emt.Emt.Constants;

namespace FSDEV.MOD.Emt.Emt_Test;

/// <summary>
/// An ILP client that only keeps states and records the change requests. States are handed out as copies without pending changes,
/// like the real client controller does.
/// </summary>
internal class FakeIlp : IClientController
{
    public Dictionary<string, IBaseState> States { get; } = new();
    public List<ChangeRequest> Sent { get; } = [];
    public ExecutionResponse Response { get; set; } = ExecutionResponse.SUCCEEDED;

    public void Put(IBaseState state)
    {
        _ = state.GetChanges();
        States[state.Key] = state.Clone();
    }

    public bool TryGetState(string key, [NotNullWhen(true)] out IBaseState? state)
    {
        state = Resolve(key)?.Clone();
        return state is not null;
    }

    /// <summary>Finds a state by its key, also inside the dictionaries that were put in as a whole.</summary>
    private IBaseState? Resolve(string key)
    {
        if (States.TryGetValue(key, out var found))
        {
            return found;
        }

        foreach (var (rootKey, root) in States)
        {
            if (!key.StartsWith(rootKey + "/", StringComparison.Ordinal))
            {
                continue;
            }

            var current = root;
            foreach (var segment in key[(rootKey.Length + 1)..].Split('/'))
            {
                if (current is not DictionaryState<StringState, IBaseState> dict || !dict.TryGetValue(new StringState(segment), out var child))
                {
                    current = null;
                    break;
                }

                current = child;
            }

            if (current is not null)
            {
                return current;
            }
        }

        return null;
    }

    public ExecutionResponse SendChangeRequest(ChangeRequest changeRequest)
    {
        Sent.Add(changeRequest);

        // The real client controller merges the changes first and rejects the request when that fails (ClientController.SendChangeRequest).
        var mergeChanges = typeof(ChangeRequest).GetMethod("MergeChanges", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        if (mergeChanges is not null && mergeChanges.Invoke(changeRequest, null) is false)
        {
            return ExecutionResponse.REJECTED_SANITY_CHECKS_FAILED;
        }

        // The ILP server denies a change request with two CreateStates for the same key (ServerController, "duplicate keys exist").
        if (changeRequest.Changes.OfType<CreateState>().GroupBy(create => create.FutureState.Key).Any(group => group.Count() > 1))
        {
            return ExecutionResponse.REJECTED_SANITY_CHECKS_FAILED;
        }

        return Response;
    }

    public ExecutionResponse SendChangeRequest(ChangeRequest changeRequest, bool additionalLogs) => SendChangeRequest(changeRequest);

    public Task<ExecutionResponse> SendChangeRequestAsync(ChangeRequest changeRequest) => Task.FromResult(SendChangeRequest(changeRequest));

    public void AddSubscription(string key) => throw new NotSupportedException();

    public string ClientName() => "fake";

    public void Initialize(string _clientName, ClientNetworkingBase _clientNetworking, BlockingCollection<IMessage> _messageQueue, Dictionary<string, IPrimitiveState> _internalState, BlockingCollection<ChangeReply> _changeReplies, ISerializer serializer, IDeserializer deserializer) => throw new NotSupportedException();

    public bool IsInitialized() => true;

    public bool IsReadyToReceive() => true;

    public bool IsRegistered() => true;

    public ExecutionResponse Register() => throw new NotSupportedException();

    public ExecutionResponse RegisterValidationCallback(string key, ValidationFunction func, bool allowValidationOfChildren, out Guid validationId) => throw new NotSupportedException();

    public void RemoveSubscription(string key) => throw new NotSupportedException();

    public void Stop()
    {
    }

    public bool TryGetAllStates(out DictionaryState<StringState, IBaseState> states) => throw new NotSupportedException();

    public bool TryGetTypeOfState(string key, [NotNullWhen(true)] out Type? type)
    {
        type = States.TryGetValue(key, out var state) ? state.GetType() : null;
        return type is not null;
    }

    public ExecutionResponse Unregister(string reason) => throw new NotSupportedException();

    public ExecutionResponse UnregisterValidationCallback(string key, Guid id) => throw new NotSupportedException();

    public ExecutionResponse RegisterSubscription(string key, SubscriptionFunction func, bool watchChildren, out Guid subscriptionId) => throw new NotSupportedException();

    public ExecutionResponse UnregisterSubscription(string key, Guid subscriptionId) => throw new NotSupportedException();
}

[TestClass]
public class StorageTests
{
    private const string SEQUENCE_PATH = Constants.Ilp.Paths.ERROR_NUMBER;

    private static ErrorRecord CreateRecord(long number = 7, long reportedAt = 1_763_900_000_000)
    {
        var record = new ErrorRecord
        {
            Id = Guid.NewGuid(),
            Number = number,
            ErrorNumber = $"EMT-{number:000000}",
            ClientRequestId = Guid.NewGuid(),
            Status = ErrorStatus.New,
            Severity = Severity.Warning,
            ErrorCodeId = Guid.NewGuid(),
            ErrorCode = "SCAN-01",
            CodeTitle = "Scanner",
            LocationId = Guid.NewGuid().ToString(),
            LocationPath = "Hall 1 / Wareneingang",
            Description = "Does not read \"quoted\" codes\nsecond line",
            ReporterUserId = Guid.NewGuid(),
            ReporterName = "anna",
            ReportedAt = reportedAt,
            LastChangedAt = reportedAt,
            LastChangedByName = "anna",
            Version = 1,
        };
        record.AddEvent(ErrorEventTypes.CREATED, record.ReporterUserId, "anna", reportedAt);
        return record;
    }

    private static string MonthPath(ErrorRecord record) => $"{Constants.Ilp.Paths.ERRORS}/{ErrorRecordIlpMapper.GetMonthKey(record.ReportedAt)}";

    [TestMethod]
    public void UT_MAP_01_Error_survives_a_round_trip_through_the_ilp_schema()
    {
        var record = CreateRecord();
        record.AssigneeUserId = Guid.NewGuid();
        record.AssigneeName = "ben";
        record.AddEvent(ErrorEventTypes.ASSIGNED, Guid.NewGuid(), "gil", 1_763_900_001_000, fromValue: "-", toValue: "ben");
        record.FinalizedAt = 1_763_900_002_000;
        record.Status = ErrorStatus.Closed;

        var state = ErrorRecordIlpMapper.ToIlpState(record);

        Assert.AreEqual($"{MonthPath(record)}/{record.Id}", state.Key);
        Assert.IsTrue(ErrorRecordIlpMapper.TryParse(state, out var parsed));
        Assert.AreEqual(record.Id, parsed.Id);
        Assert.AreEqual(record.Number, parsed.Number);
        Assert.AreEqual(record.ErrorNumber, parsed.ErrorNumber);
        Assert.AreEqual(record.ClientRequestId, parsed.ClientRequestId);
        Assert.AreEqual(ErrorStatus.Closed, parsed.Status);
        Assert.AreEqual(Severity.Warning, parsed.Severity);
        Assert.AreEqual(record.ErrorCodeId, parsed.ErrorCodeId);
        Assert.AreEqual(record.CodeTitle, parsed.CodeTitle);
        Assert.AreEqual(record.LocationPath, parsed.LocationPath);
        Assert.AreEqual(record.Description, parsed.Description);
        Assert.AreEqual(record.ReporterUserId, parsed.ReporterUserId);
        Assert.AreEqual(record.AssigneeUserId, parsed.AssigneeUserId);
        Assert.AreEqual("ben", parsed.AssigneeName);
        Assert.AreEqual(record.ReportedAt, parsed.ReportedAt);
        Assert.AreEqual(record.FinalizedAt, parsed.FinalizedAt);
        Assert.AreEqual(ErrorRecord.NOTIFICATION_NONE, parsed.NotificationState);
        Assert.AreEqual(1, parsed.Version);
        Assert.AreEqual(2, parsed.Events.Count);
        Assert.AreEqual("ben", parsed.Events[1].ToValue);
    }

    [TestMethod]
    public void UT_MAP_01_Unassigned_error_has_an_empty_assignee_on_ilp()
    {
        var state = ErrorRecordIlpMapper.ToIlpState(CreateRecord());

        Assert.IsTrue(ErrorRecordIlpMapper.TryParse(state, out var parsed));
        Assert.IsNull(parsed.AssigneeUserId);
        Assert.IsTrue(state.TryGetValue(new StringState("AssigneeUserId"), out var assignee));
        Assert.AreEqual(string.Empty, ((StringState)assignee).Value);
    }

    [TestMethod]
    public void UT_MAP_01_A_code_title_saved_per_language_is_read_as_the_german_text()
    {
        var state = ErrorRecordIlpMapper.ToIlpState(CreateRecord());
        state[new StringState("CodeTitle")] = new DictionaryState<StringState, IBaseState>(Guid.NewGuid().ToString())
        {
            [new StringState("en")] = new StringState("Scanner EN"),
            [new StringState("de")] = new StringState("Scanner DE"),
        };

        Assert.IsTrue(ErrorRecordIlpMapper.TryParse(state, out var parsed));
        Assert.AreEqual("Scanner DE", parsed.CodeTitle);
    }

    [TestMethod]
    public void UT_MAP_01_Unknown_status_or_broken_events_are_not_parsed()
    {
        var record = CreateRecord();
        var state = ErrorRecordIlpMapper.ToIlpState(record);
        state[new StringState("Status")] = new StringState("WHATEVER");
        Assert.IsFalse(ErrorRecordIlpMapper.TryParse(state, out _));

        state = ErrorRecordIlpMapper.ToIlpState(record);
        state[new StringState("Events")] = new StringState("{not json");
        Assert.IsFalse(ErrorRecordIlpMapper.TryParse(state, out _));
    }

    [TestMethod]
    public void UT_STORE_01_Create_sends_counter_month_and_error_in_one_change_request()
    {
        var ilp = new FakeIlp();
        ilp.Put(new LongState(SEQUENCE_PATH, 6));
        var record = CreateRecord();

        var created = new IlpErrorStore(ilp).TryCreate(record, 7);

        Assert.IsTrue(created);
        var request = ilp.Sent.Single();
        var counter = request.Changes.OfType<ChangeState>().Single();
        Assert.AreEqual(SEQUENCE_PATH, counter.FutureState.Key);
        Assert.AreEqual(7L, ((LongState)counter.FutureState).Value);
        Assert.AreEqual(6L, ((LongState)counter.FormerState).Value);

        var createdKeys = request.Changes.OfType<CreateState>().Select(c => c.FutureState.Key).ToList();
        CollectionAssert.Contains(createdKeys, MonthPath(record), "the month dictionary is created because it does not exist");
        CollectionAssert.Contains(createdKeys, $"{MonthPath(record)}/{record.Id}");
        CollectionAssert.Contains(createdKeys, $"{MonthPath(record)}/{record.Id}/Status");
        CollectionAssert.Contains(createdKeys, $"{MonthPath(record)}/{record.Id}/CodeTitle");
        CollectionAssert.Contains(createdKeys, $"{MonthPath(record)}/{record.Id}/Events");
    }

    [TestMethod]
    public void UT_STORE_01_Create_does_not_create_an_existing_month_again_and_creates_the_counter_if_missing()
    {
        var ilp = new FakeIlp();
        var record = CreateRecord();
        ilp.Put(new DictionaryState<StringState, IBaseState>(MonthPath(record)));

        Assert.IsTrue(new IlpErrorStore(ilp).TryCreate(record, 1));

        var request = ilp.Sent.Single();
        Assert.AreEqual(1, request.Changes.OfType<CreateState>().Count(c => c.FutureState.Key == SEQUENCE_PATH), "no counter on ilp yet: it is created");
        Assert.AreEqual(0, request.Changes.OfType<CreateState>().Count(c => c.FutureState.Key == MonthPath(record)));
    }

    [TestMethod]
    public void UT_STORE_01_A_rejected_change_request_is_a_failure()
    {
        var ilp = new FakeIlp { Response = ExecutionResponse.REJECTED_VALIDATION_FAILED };
        ilp.Put(new LongState(SEQUENCE_PATH, 0));

        Assert.IsFalse(new IlpErrorStore(ilp).TryCreate(CreateRecord(1), 1));
    }

    [TestMethod]
    public void UT_STORE_02_Update_changes_only_the_fields_that_changed()
    {
        var ilp = new FakeIlp();
        var record = CreateRecord();
        ilp.Put(ErrorRecordIlpMapper.ToIlpState(record));

        var changed = record.Clone();
        changed.Status = ErrorStatus.Acknowledged;
        changed.Version = 2;
        changed.LastChangedByName = "ben";
        changed.AddEvent(ErrorEventTypes.STATUS_CHANGED, Guid.NewGuid(), "ben", record.ReportedAt + 1000, "Acknowledge", "NEW", "ACKNOWLEDGED");

        Assert.IsTrue(new IlpErrorStore(ilp).TryUpdate(changed));

        var request = ilp.Sent.Single();
        Assert.IsFalse(request.Changes.OfType<CreateState>().Any(), "nothing is created");
        Assert.IsFalse(request.Changes.OfType<DeleteState>().Any(), "nothing is deleted");
        var changedKeys = request.Changes.OfType<ChangeState>().Select(c => c.FutureState.Key.Split('/').Last()).ToList();
        CollectionAssert.AreEquivalent(new[] { "Status", "Version", "LastChangedByName", "Events" }, changedKeys);
        var status = request.Changes.OfType<ChangeState>().Single(c => c.FutureState.Key.EndsWith("/Status"));
        Assert.AreEqual("NEW", ((StringState)status.FormerState).Value);
        Assert.AreEqual("ACKNOWLEDGED", ((StringState)status.FutureState).Value);
    }

    [TestMethod]
    public void UT_STORE_02_Update_of_an_error_that_is_not_on_ilp_fails()
    {
        var ilp = new FakeIlp();

        Assert.IsFalse(new IlpErrorStore(ilp).TryUpdate(CreateRecord()));
        Assert.AreEqual(0, ilp.Sent.Count);
    }

    [TestMethod]
    public void UT_STORE_03_Deleting_the_last_error_of_a_month_deletes_the_month_too()
    {
        var ilp = new FakeIlp();
        var record = CreateRecord();
        var month = new DictionaryState<StringState, IBaseState>(MonthPath(record));
        month[new StringState(record.Id.ToString())] = ErrorRecordIlpMapper.ToIlpState(record);
        ilp.Put(month);

        Assert.IsTrue(new IlpErrorStore(ilp).TryDelete(record));

        var deleted = ilp.Sent.Single().Changes.OfType<DeleteState>().Select(d => d.FormerState.Key).ToList();
        CollectionAssert.Contains(deleted, MonthPath(record));
        CollectionAssert.Contains(deleted, $"{MonthPath(record)}/{record.Id}/Status");
    }

    [TestMethod]
    public void UT_STORE_03_Deleting_one_of_several_errors_keeps_the_month()
    {
        var ilp = new FakeIlp();
        var record = CreateRecord(1);
        var other = CreateRecord(2);
        var month = new DictionaryState<StringState, IBaseState>(MonthPath(record));
        month[new StringState(record.Id.ToString())] = ErrorRecordIlpMapper.ToIlpState(record);
        month[new StringState(other.Id.ToString())] = ErrorRecordIlpMapper.ToIlpState(other);
        ilp.Put(month);

        Assert.IsTrue(new IlpErrorStore(ilp).TryDelete(record));

        var deleted = ilp.Sent.Single().Changes.OfType<DeleteState>().Select(d => d.FormerState.Key).ToList();
        CollectionAssert.DoesNotContain(deleted, MonthPath(record));
        Assert.IsTrue(deleted.All(key => key.StartsWith($"{MonthPath(record)}/{record.Id}")), "only the error itself");
    }

    [TestMethod]
    public void UT_STORE_03_Deleting_an_error_that_is_already_gone_is_a_success()
    {
        var ilp = new FakeIlp();

        Assert.IsTrue(new IlpErrorStore(ilp).TryDelete(CreateRecord()));
        Assert.AreEqual(0, ilp.Sent.Count);
    }

    [TestMethod]
    public void UT_STORE_04_Load_reads_all_months_and_skips_broken_errors()
    {
        var ilp = new FakeIlp();
        var errors = new DictionaryState<StringState, IBaseState>(Constants.Ilp.Paths.ERRORS);
        var first = CreateRecord(1, 1_763_900_000_000);
        var second = CreateRecord(2, 1_766_500_000_000);
        var broken = CreateRecord(3, 1_766_500_000_000);
        var firstMonth = new DictionaryState<StringState, IBaseState>();
        firstMonth[new StringState(first.Id.ToString())] = ErrorRecordIlpMapper.ToIlpState(first);
        var secondMonth = new DictionaryState<StringState, IBaseState>();
        secondMonth[new StringState(second.Id.ToString())] = ErrorRecordIlpMapper.ToIlpState(second);
        var brokenState = ErrorRecordIlpMapper.ToIlpState(broken);
        brokenState[new StringState("Status")] = new StringState("???");
        secondMonth[new StringState(broken.Id.ToString())] = brokenState;
        errors[new StringState(ErrorRecordIlpMapper.GetMonthKey(first.ReportedAt))] = firstMonth;
        errors[new StringState(ErrorRecordIlpMapper.GetMonthKey(second.ReportedAt))] = secondMonth;
        ilp.Put(errors);

        Assert.IsTrue(new IlpErrorStore(ilp).TryLoadAll(out var records));

        CollectionAssert.AreEquivalent(new[] { first.Id, second.Id }, records.Select(r => r.Id).ToArray());
    }

    [TestMethod]
    public void UT_STORE_05_Base_states_are_created_once()
    {
        var ilp = new FakeIlp();
        var store = new IlpErrorStore(ilp);

        Assert.IsTrue(store.TryEnsureRoots());

        var created = ilp.Sent.Single().Changes.OfType<CreateState>().Select(c => c.FutureState.Key).ToList();
        CollectionAssert.IsSubsetOf(new[] { "Emt", "Emt/Errors", "Emt/Sequence", SEQUENCE_PATH }, created);

        ilp.Put(new DictionaryState<StringState, IBaseState>("Emt"));
        ilp.Put(new DictionaryState<StringState, IBaseState>("Emt/Errors"));
        ilp.Put(new DictionaryState<StringState, IBaseState>("Emt/Sequence"));
        ilp.Put(new LongState(SEQUENCE_PATH, 0));
        ilp.Sent.Clear();
        Assert.IsTrue(store.TryEnsureRoots());
        Assert.AreEqual(0, ilp.Sent.Single().Changes.Count, "nothing to create");
    }

    [TestMethod]
    public void UT_STORE_06_The_counter_is_only_raised_never_lowered()
    {
        var ilp = new FakeIlp();
        ilp.Put(new LongState(SEQUENCE_PATH, 10));
        var store = new IlpErrorStore(ilp);

        Assert.IsTrue(store.TryEnsureSequenceAtLeast(5));
        Assert.AreEqual(0, ilp.Sent.Count);

        Assert.IsTrue(store.TryEnsureSequenceAtLeast(12));
        Assert.AreEqual(12L, ((LongState)ilp.Sent.Single().Changes.OfType<ChangeState>().Single().FutureState).Value);
    }

    [TestMethod]
    public void UT_STORE_07_Error_month_is_the_utc_month_of_the_report()
    {
        Assert.AreEqual("202511", ErrorRecordIlpMapper.GetMonthKey(new DateTimeOffset(2025, 11, 30, 23, 59, 59, TimeSpan.Zero).ToUnixTimeMilliseconds()));
        Assert.AreEqual("202512", ErrorRecordIlpMapper.GetMonthKey(new DateTimeOffset(2025, 12, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()));
    }
}
