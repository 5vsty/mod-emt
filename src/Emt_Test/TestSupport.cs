using FSDEV.FES.Server.Client;
using FSDEV.FES.Server.Client.Sessions;
using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Emt.Emt_Server;
using FSDEV.MOD.Emt.Emt_Server.Catalog;
using FSDEV.MOD.Emt.Emt_Server.Errors;
using FSDEV.MOD.Emt.Emt_Server.Locations;
using FSDEV.MOD.Emt.Emt_Server.Security;
using FSDEV.MOD.Emt.Emt_Server.Storage;
using FSDEV.MOD.Emt.Emt_Server.Tables;
using FSDEV.Common.Translations;
using Newtonsoft.Json.Linq;
using Constants = FSDEV.MOD.Emt.Emt.Constants;

namespace FSDEV.MOD.Emt.Emt_Test;

internal class FakeClock : IClock
{
    public long UtcNowMs { get; set; } = 1_763_900_000_000;

    public void AdvanceDays(int days) => UtcNowMs += days * 24L * 60 * 60 * 1000;
}

internal class FakeErrorStore : IErrorStore
{
    public Dictionary<Guid, ErrorRecord> Records { get; } = new();
    public long Sequence { get; set; }
    public bool FailWrites { get; set; }
    public int CreateCalls { get; private set; }
    public List<(string ErrorNumber, long SequenceValue)> Created { get; } = [];
    public List<Guid> Deleted { get; } = [];

    public bool TryEnsureRoots() => true;

    public bool TryLoadAll(out List<ErrorRecord> records)
    {
        records = Records.Values.Select(r => r.Clone()).ToList();
        return true;
    }

    public bool TryReadSequence(out long value)
    {
        value = Sequence;
        return true;
    }

    public bool TryEnsureSequenceAtLeast(long value)
    {
        Sequence = Math.Max(Sequence, value);
        return true;
    }

    public bool TryCreate(ErrorRecord record, long newSequenceValue)
    {
        CreateCalls++;
        if (FailWrites)
        {
            return false;
        }

        // counter and error are written together
        Sequence = newSequenceValue;
        Records[record.Id] = record.Clone();
        Created.Add((record.ErrorNumber, newSequenceValue));
        return true;
    }

    public bool TryUpdate(ErrorRecord record)
    {
        if (FailWrites)
        {
            return false;
        }

        Records[record.Id] = record.Clone();
        return true;
    }

    public bool TryDelete(ErrorRecord record)
    {
        if (FailWrites)
        {
            return false;
        }

        _ = Records.Remove(record.Id);
        Deleted.Add(record.Id);
        return true;
    }
}

internal class FakePermissionSource : IPermissionSource
{
    public bool Available { get; set; } = true;
    public Dictionary<string, HashSet<Guid>> Granted { get; } = new();
    public Dictionary<Guid, List<Guid>> GroupsOfUser { get; } = new();
    public Dictionary<Guid, List<Guid>> GroupMembers { get; } = new();
    public List<EmtUser> Users { get; } = [];

    public bool TryGetGrantedIds(string permission, out IReadOnlyCollection<Guid> ids)
    {
        ids = Granted.TryGetValue(permission, out var granted) ? granted : [];
        return Available;
    }

    public IReadOnlyCollection<Guid> GetGroupIdsOfUser(Guid userId) => GroupsOfUser.TryGetValue(userId, out var groups) ? groups : [];

    public bool TryGetGroupMembers(Guid groupId, out IReadOnlyCollection<Guid> members)
    {
        members = [];
        if (!GroupMembers.TryGetValue(groupId, out var found))
        {
            return false;
        }

        members = found;
        return true;
    }

    public bool TryGetUsers(out IReadOnlyList<EmtUser> users)
    {
        users = Users;
        return Available;
    }

    public void Grant(string permission, Guid id)
    {
        if (!Granted.TryGetValue(permission, out var set))
        {
            set = [];
            Granted[permission] = set;
        }

        _ = set.Add(id);
    }

    public void Revoke(string permission, Guid id)
    {
        if (Granted.TryGetValue(permission, out var set))
        {
            _ = set.Remove(id);
        }
    }
}

internal class FakeCatalogStorage : ICatalogStorage
{
    public Dictionary<Guid, ErrorCode> Codes { get; } = new();
    public bool FailSave { get; set; }

    public bool TryLoadAll(out List<ErrorCode> codes)
    {
        codes = Codes.Values.Select(c => c.Clone()).ToList();
        return true;
    }

    public bool TrySave(ErrorCode code)
    {
        if (FailSave)
        {
            return false;
        }

        Codes[code.Id] = code.Clone();
        return true;
    }

    public bool TryDelete(Guid codeId) => Codes.Remove(codeId) || true;
}

internal class FakeSettingsStorage : ISettingsStorage
{
    public EmtSettings? Stored { get; set; }

    public bool TryLoad(out EmtSettings? settings)
    {
        settings = Stored;
        return true;
    }

    public bool TrySave(EmtSettings settings)
    {
        Stored = new EmtSettings { RetentionDays = settings.RetentionDays, OtherCodeEnabled = settings.OtherCodeEnabled, Version = settings.Version };
        return true;
    }
}

internal class FakeLocationSource : ILocationSource
{
    public List<LocationOption> Locations { get; } = [];
    public bool Available { get; set; } = true;
    public int Calls { get; private set; }

    public bool TryGetLocations(out List<LocationOption> locations)
    {
        Calls++;
        locations = Locations.ToList();
        return Available;
    }
}

internal class FakePushSender : IPushSender
{
    public List<(IReadOnlyCollection<Guid> Users, JArray Message)> Sent { get; } = [];

    public void SendToUsers(IReadOnlyCollection<Guid> userIds, JArray message) => Sent.Add((userIds.ToList(), message));
}

internal class RecordingObserver : IErrorObserver
{
    public List<(ErrorRecord Error, ErrorChangeType Type)> Changes { get; } = [];

    public void OnErrorChanged(ErrorRecord error, ErrorChangeType changeType) => Changes.Add((error, changeType));
}

/// <summary>
/// Wires the services of the module with fakes for ILP, UserManager and the facility structure.
/// Users as in 08: anna and chris report, ben and gil manage errors, dora has no permission, fay manages the catalog, hana has all three.
/// </summary>
internal class TestEnvironment
{
    public static readonly Guid Anna = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    public static readonly Guid Chris = Guid.Parse("a0000000-0000-0000-0000-000000000002");
    public static readonly Guid Ben = Guid.Parse("a0000000-0000-0000-0000-000000000003");
    public static readonly Guid Gil = Guid.Parse("a0000000-0000-0000-0000-000000000004");
    public static readonly Guid Dora = Guid.Parse("a0000000-0000-0000-0000-000000000005");
    public static readonly Guid Fay = Guid.Parse("a0000000-0000-0000-0000-000000000006");
    public static readonly Guid Hana = Guid.Parse("a0000000-0000-0000-0000-000000000007");

    public static readonly string Hall1 = "11111111-1111-1111-1111-111111111111";
    public static readonly string Hall2 = "22222222-2222-2222-2222-222222222222";

    public FakeClock Clock { get; } = new();
    public FakeErrorStore Store { get; } = new();
    public FakePermissionSource Source { get; } = new();
    public FakeCatalogStorage CatalogStorage { get; } = new();
    public FakeSettingsStorage SettingsStorage { get; } = new();
    public FakeLocationSource LocationSource { get; } = new();
    public FakePushSender Sender { get; } = new();
    public RecordingObserver Observer { get; } = new();

    public EmtPermissionService Permissions { get; private set; } = null!;
    public ErrorIndex Index { get; private set; } = null!;
    public SequenceService Sequence { get; private set; } = null!;
    public SettingsStore Settings { get; private set; } = null!;
    public CatalogStore Catalog { get; private set; } = null!;
    public LocationProvider Locations { get; private set; } = null!;
    public ErrorService Errors { get; private set; } = null!;
    public ErrorPushService Push { get; private set; } = null!;
    public RetentionJob Retention { get; private set; } = null!;
    public CatalogAdminService Admin { get; private set; } = null!;
    public FakeFesClient Fes { get; } = new();
    public MyErrorsTables MyErrors { get; private set; } = null!;

    public Guid ScanCode { get; private set; }
    public Guid GenCode { get; private set; }
    public Guid InactiveCode { get; private set; }

    public TestEnvironment()
    {
        foreach (var (id, name) in new[] { (Anna, "anna"), (Chris, "chris"), (Ben, "ben"), (Gil, "gil"), (Dora, "dora"), (Fay, "fay"), (Hana, "hana") })
        {
            Source.Users.Add(new EmtUser(id, name));
        }

        Source.Grant(Constants.Permissions.REPORT_ERRORS, Anna);
        Source.Grant(Constants.Permissions.REPORT_ERRORS, Chris);
        Source.Grant(Constants.Permissions.MANAGE_ERRORS, Ben);
        Source.Grant(Constants.Permissions.MANAGE_ERRORS, Gil);
        Source.Grant(Constants.Permissions.MANAGE_CATALOG, Fay);
        foreach (var permission in new[] { Constants.Permissions.REPORT_ERRORS, Constants.Permissions.MANAGE_ERRORS, Constants.Permissions.MANAGE_CATALOG })
        {
            Source.Grant(permission, Hana);
        }

        LocationSource.Locations.Add(new LocationOption(Hall1, "Hall 1 / Wareneingang", "Room"));
        LocationSource.Locations.Add(new LocationOption(Hall2, "Hall 2 / Versand", "Room"));

        Build();
    }

    /// <summary>Creates all services again on the same fake storage, like a restart of the module.</summary>
    public void Build()
    {
        Permissions = new EmtPermissionService(Source);
        Index = new ErrorIndex();
        Assert.IsTrue(Store.TryLoadAll(out var records));
        Index.Load(records);

        Sequence = new SequenceService("EMT-", 6);
        Sequence.Initialize(Store.Sequence, Index.HighestNumber);

        Settings = new SettingsStore(SettingsStorage);
        Assert.IsTrue(Settings.Initialize());

        Catalog = new CatalogStore(CatalogStorage, () => Settings.Current.OtherCodeEnabled, Index.IsCodeUsed);
        Assert.IsTrue(Catalog.Initialize(Clock.UtcNowMs));

        Locations = new LocationProvider(LocationSource, [], TimeSpan.FromMinutes(10));
        Errors = new ErrorService(Store, Index, Sequence, Catalog, Locations, Permissions, Clock);
        Errors.AddObserver(Observer);
        Push = new ErrorPushService(Sender, Permissions);
        Errors.AddObserver(Push);
        MyErrors = new MyErrorsTables(Fes, Permissions, Index, Clock, new Translations("none"), 1000);
        Errors.AddObserver(MyErrors);
        Retention = new RetentionJob(Store, Index, Settings, Clock, _ => { });
        Admin = new CatalogAdminService(Catalog, Settings, Permissions, Index, Retention, Push, Clock);

        ScanCode = EnsureCode("SCAN-01", "Scanner liest nicht", Severity.Warning, true);
        GenCode = EnsureCode("GEN-01", "Geraet antwortet nicht", Severity.Error, true);
        InactiveCode = EnsureCode("GEN-02", "Alt", Severity.Info, false);
    }

    public Guid EnsureCode(string code, string title, Severity severity, bool active)
    {
        var existing = Catalog.GetAll().FirstOrDefault(c => c.Code == code);
        if (existing is not null)
        {
            return existing.Id;
        }

        var result = Catalog.Save(null, null, code, title, string.Empty, string.Empty, "General", severity.ToWire(), active, "fay", Clock.UtcNowMs);
        Assert.IsTrue(result.Successful, result.Reason);
        return result.Value!.Id;
    }

    public ErrorRecord Submit(Guid? reporter = null, Guid? codeId = null, string? locationId = null, string description = "Test")
    {
        var result = Errors.Submit(reporter ?? Anna, Guid.NewGuid(), locationId ?? Hall1, codeId ?? ScanCode, description);
        Assert.IsTrue(result.Successful, result.Reason);
        return result.Value!.Error;
    }

    public static WsMessageContext<EmtSession, EmtWebsocket> CreateContext(Guid userId, params JObject[] messages)
    {
        var session = EmtSession.CreateSession(new Session(Guid.NewGuid(), userId, "test"));
        var websocket = new EmtWebsocket();
        websocket.InitializeWebsocket(session.SessionId, Guid.NewGuid());
        return new WsMessageContext<EmtSession, EmtWebsocket>(session, websocket, new JArray(messages));
    }
}

internal class FakeFesClient : IClient
{
    public IClient.FrontendServerAdded? OnFrontendServerAdded { get; set; }
    public Dictionary<string, IClient.HandleFrontendMessageBase> MessageHandlers { get; } = new();
    public Dictionary<string, IClient.HandleSessionRemovedBase> SessionRemovedHandlers { get; } = new();
    public List<Guid> SentToWebsockets { get; } = [];

    public bool TryAddMessageHandler(string handlerId, IClient.HandleFrontendMessageBase handler)
    {
        MessageHandlers[handlerId] = handler;
        return true;
    }

    public bool TryRemoveMessageHandler(string handlerId) => MessageHandlers.Remove(handlerId);

    public bool TryAddSessionRemovedHandler(string handlerId, IClient.HandleSessionRemovedBase handler)
    {
        SessionRemovedHandlers[handlerId] = handler;
        return true;
    }

    public bool TryRemoveSessionRemovedHandler(string handlerId) => SessionRemovedHandlers.Remove(handlerId);

    public bool TryAddNewWebsocketHandler(string handlerId, IClient.HandleNewSessionBase handler) => true;

    public bool TryRemoveNewSessionHandler(string handlerId) => true;

    public bool TrySendToWebsocket(Guid websocketId, JArray message)
    {
        SentToWebsockets.Add(websocketId);
        return true;
    }

    public void BroadcastMessage(JArray message)
    {
    }

    public void SendMessageToUsers(List<Guid> userIds, JArray message)
    {
    }

    public bool TrySendToSession(Session session, JArray message) => true;

    public void RegisterProxy(string prefix, int port, string[] allowedHeaders, string[] exposedHeaders)
    {
    }

    public void UnregisterProxy(string prefix)
    {
    }

    public List<Session> GetAllBaseSessions() => [];

    public List<Websocket> GetAllBaseWebsockets() => [];
}

