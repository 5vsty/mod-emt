using FSDEV.Common.Logger;
using FSDEV.Common.Translations;
using FSDEV.FES.Server.Client;
using FSDEV.ILP.Intralogistikplattform.SystemState.Client;
using FSDEV.ILP.Intralogistikplattform.SystemState.Deserializer;
using FSDEV.ILP.Intralogistikplattform.SystemState.Messages;
using FSDEV.ILP.Intralogistikplattform.SystemState.Serializer;
using FSDEV.ILP.Intralogistikplattform.SystemState.States.PrimitiveStates;
using FSDEV.ILP.Intralogistikplattform.SystemState_Client;
using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Emt.Emt.ModuleConfig;
using FSDEV.MOD.Emt.Emt_Server.Catalog;
using FSDEV.MOD.Emt.Emt_Server.Errors;
using FSDEV.MOD.Emt.Emt_Server.Locations;
using FSDEV.MOD.Emt.Emt_Server.Notifications;
using FSDEV.MOD.Emt.Emt_Server.Security;
using FSDEV.MOD.Emt.Emt_Server.Storage;
using FSDEV.MOD.Emt.Emt_Server.Tables;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using Constants = FSDEV.MOD.Emt.Emt.Constants;
using IClient = FSDEV.ILP.Intralogistikplattform.SystemState.Client.IClient;

namespace FSDEV.MOD.Emt.Emt_Server;

public class EmtServer
{
    private const string LOG_PREFIX = $"[{nameof(EmtServer)}]";
    private static readonly TimeSpan LocationCacheMaxAge = TimeSpan.FromMinutes(10);

    private readonly IClient IlpClient;
    private readonly Client<EmtSession, EmtWebsocket> FsClient;
    private readonly ModuleConfig Config;
    private readonly CancellationToken StoppingToken;

    private RetentionJob? Retention;

    private EmtServer(IClient ilpClient, Client<EmtSession, EmtWebsocket> fsClient, ModuleConfig config, CancellationToken stoppingToken)
    {
        IlpClient = ilpClient;
        FsClient = fsClient;
        Config = config;
        StoppingToken = stoppingToken;
    }

    private static IClient SetupIlpClient(CancellationToken stoppingToken)
    {
        var clientStateController = new ClientController();
        var clientNetworking = new ClientNetworking(stoppingToken);
        var serializer = new Serializer();
        var deserializer = new Deserializer();
        var messageQueue = new BlockingCollection<IMessage>();
        var internalState = new Dictionary<string, IPrimitiveState>();
        var changeReplies = new BlockingCollection<ChangeReply>();

        var ilpClient = new Client();
        ilpClient.Initialize(Constants.Service.APP_NAME, clientStateController, clientNetworking, messageQueue, internalState, changeReplies, serializer, deserializer);

        Logging.Log(LogLevel.Info, $"{LOG_PREFIX} IlpClient initialized");
        return ilpClient;
    }

    public static async Task<EmtServer?> Initialize(CancellationToken stoppingToken)
    {
        Logging.Log(LogLevel.Info, $"{LOG_PREFIX} Registering ILP client...");

        var ilpClient = SetupIlpClient(stoppingToken);
        _ = stoppingToken.Register(ilpClient.Stop);

        _ = ilpClient.RegisterClient();

        Logging.Log(LogLevel.Info, $"{LOG_PREFIX} Registering ILP client done.");

        var timeStampBegin = DateTime.Now;
        while (!stoppingToken.IsCancellationRequested && !ilpClient.IsRegistered())
        {
            await Task.Delay(50, stoppingToken);
        }

        var timeNeededForSynchronization = DateTime.Now - timeStampBegin;
        Logging.Log(LogLevel.Debug, $"{LOG_PREFIX} Sync of IlpClient finished after {timeNeededForSynchronization:c}");

        await Task.Delay(5_000, CancellationToken.None);

        var fsClient = Client<EmtSession, EmtWebsocket>.BuildClient(new LoggerWrapper(), session =>
            EmtSession.CreateSession(session), stoppingToken);

        // parse config
        if (!ModuleConfig.TryReadFromIlp(ilpClient.ClientController(), out var config))
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Cannot read ModuleConfig!");
            return null;
        }

        var emt = new EmtServer(ilpClient, fsClient, config, stoppingToken);

        // the frontend always gets LoadModule, so it knows the module and its Active flag
        _ = emt.FsClient.TryAddNewWebsocketHandler(Constants.Session.NEW_SESSION_HANDLER_ID, emt.HandleNewSession);

        if (!config.ModuleActive)
        {
            Logging.Log(LogLevel.Warning, $"{LOG_PREFIX} Module is not active, only sending LoadModule to the frontend");
            return emt;
        }

        return emt.TryStartModule() ? emt : null;
    }

    private bool TryStartModule()
    {
        var ilp = IlpClient.ClientController();
        var clock = new SystemClock();

        var permissionSource = new IlpPermissionSource(ilp);
        if (!permissionSource.Initialize())
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Cannot initialize the permission source!");
            return false;
        }

        var permissions = new EmtPermissionService(permissionSource);

        var store = new IlpErrorStore(ilp);
        if (!store.TryEnsureRoots())
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Cannot create the base states on ILP!");
            return false;
        }

        var settings = new SettingsStore(new IlpSettingsStorage(ilp));
        if (!settings.Initialize())
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Cannot initialize the settings!");
            return false;
        }

        var index = new ErrorIndex();
        if (!store.TryLoadAll(out var records) || !store.TryReadSequence(out var persistedSequence))
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Cannot read the errors from ILP!");
            return false;
        }

        index.Load(records);

        // the counter must never be behind the highest number in use
        var sequence = new SequenceService(Config.ErrorNumberPrefix, Config.ErrorNumberMinDigits);
        sequence.Initialize(persistedSequence, index.HighestNumber);
        if (!store.TryEnsureSequenceAtLeast(sequence.Value))
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Cannot correct the error number counter on ILP!");
            return false;
        }

        var catalog = new CatalogStore(new IlpCatalogStorage(ilp), () => settings.Current.OtherCodeEnabled, index.IsCodeUsed);
        if (!catalog.Initialize(clock.UtcNowMs))
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Cannot initialize the error code catalog!");
            return false;
        }

        var locations = new LocationProvider(new IlpParserLocationSource(Config.LocationProjectPath, Config.LocationFdfVersion), Config.SelectableAreaTypes, LocationCacheMaxAge);
        if (!Config.LocationEnabled)
        {
            Logging.Log(LogLevel.Info, $"{LOG_PREFIX} Location.Enabled is false: the facility structure is not read and reports need no location");
        }
        else if (!locations.TryGetAll(out _))
        {
            Logging.Log(LogLevel.Warning, $"{LOG_PREFIX} Locations are not available yet, they are read again when the report form is opened");
        }

        var translations = new Translations(Config.TranslationsPath);
        if (!translations.Initialize())
        {
            Logging.Log(LogLevel.Warning, $"{LOG_PREFIX} Translations could not be read from '{Config.TranslationsPath}', table headers are not translated");
        }

        // the AMF tables do not check permissions, so they only get a client that does
        var errorListTable = new ErrorListTable();
        var errorListClient = new AuthorizingFesClient(FsClient, Guid.Parse(Constants.Tables.ERROR_LIST), userId => permissions.HasPermission(userId, Constants.Permissions.MANAGE_ERRORS));
        if (!errorListTable.Initialize(errorListClient, translations, Config.LiveUpdateDebounceMs))
        {
            return false;
        }

        errorListTable.Load(index.GetAll());

        var errorCodeTable = new ErrorCodeTable(catalog);
        var errorCodeClient = new AuthorizingFesClient(FsClient, Guid.Parse(Constants.Tables.ERROR_CODES), userId => permissions.HasPermission(userId, Constants.Permissions.MANAGE_CATALOG));
        if (!errorCodeTable.Initialize(errorCodeClient, translations))
        {
            return false;
        }

        catalog.Changed += errorCodeTable.Refresh;

        var push = new ErrorPushService(new FesPushSender(FsClient), permissions);
        var errors = new ErrorService(store, index, sequence, catalog, locations, permissions, clock, Config.LocationEnabled);
        var myErrorsTables = new MyErrorsTables(FsClient, permissions, index, clock, translations, Config.LiveUpdateDebounceMs);

        errors.AddObserver(errorListTable);
        errors.AddObserver(myErrorsTables);
        errors.AddObserver(push);

        if (Config.NotificationsEnabled)
        {
            // FR-24, FR-25: the notification of an error is created and archived in the background, EMT works without the Notifications module
            errors.AddObserver(new NotificationObserver(new IlpNotificationPublisher(ilp, Constants.Service.APP_NAME, Config.NotificationsFacilityId), errors, StoppingToken));
            Logging.Log(LogLevel.Info, $"{LOG_PREFIX} Notifications are enabled for facility {Config.NotificationsFacilityId}");
        }

        Retention = new RetentionJob(store, index, settings, clock, error =>
        {
            errorListTable.OnErrorChanged(error, ErrorChangeType.Deleted);
            myErrorsTables.OnErrorChanged(error, ErrorChangeType.Deleted);
        });

        var admin = new CatalogAdminService(catalog, settings, permissions, index, Retention, push, clock, Config.NotificationsEnabled ? "Enabled" : "Disabled");

        var wsMessageHandler = new MessageHandler.MessageHandler(errors, admin, myErrorsTables);
        if (!wsMessageHandler.TryInitialize(FsClient))
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Cannot initialize Message Handler!");
            return false;
        }

        return true;
    }

    private List<JArray> HandleNewSession(EmtSession session, EmtWebsocket websocket) =>
    [
        [
            new JObject
            {
                ["ID"] = Constants.Frontend.FRONTEND_MODULES_GUID,
                ["Action"] = "LoadModule",
                ["ModuleName"] = Constants.Service.APP_NAME,
                ["ModuleVersion"] = Config.ModuleVersion,
                ["ModuleConfig"] = new JObject
                    {
                        [Config.ModuleId.ToString()] = Config.JsonRepresentation
                    }
            }
        ]
    ];

    public void StartOperating()
    {
        FsClient.StartOperating();

        if (Retention is not null)
        {
            _ = Retention.RunAsync(TimeSpan.FromHours(Config.CleanupIntervalHours), StoppingToken);
        }

        Logging.Log(LogLevel.Info, $"{LOG_PREFIX} Start Operating!");
    }
}
