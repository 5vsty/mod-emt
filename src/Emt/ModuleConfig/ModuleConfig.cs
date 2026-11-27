using FSDEV.Common.Logger;
using FSDEV.ILP.Intralogistikplattform.SystemState.Client;
using FSDEV.ILP.Intralogistikplattform.SystemState.States;
using FSDEV.ILP.Intralogistikplattform.SystemState.States.CollectionStates;
using FSDEV.ILP.Intralogistikplattform.SystemState.States.PrimitiveStates;
using FSDEV.ILP.Intralogistikplattform.SystemState_Client.Helpers;
using Newtonsoft.Json.Linq;
using System.Diagnostics.CodeAnalysis;

namespace FSDEV.MOD.Emt.Emt.ModuleConfig;

public class ModuleConfig
{
    private const string LOG_PREFIX = $"[{nameof(ModuleConfig)}]";

    public Guid ModuleId { get; private init; }
    public bool ModuleActive { get; private init; }
    public string ModuleVersion { get; private init; }
    public string ModuleInstanceName { get; private init; }
    public string TranslationsPath { get; private init; }
    public int CleanupIntervalHours { get; private init; }
    /// <summary>When false the facility structure is not read and a report needs no location. Default true (BR-19).</summary>
    public bool LocationEnabled { get; private init; }
    public int LocationFdfVersion { get; private init; }
    public string LocationProjectPath { get; private init; }
    public List<string> SelectableAreaTypes { get; private init; }
    public string ErrorNumberPrefix { get; private init; }
    public int ErrorNumberMinDigits { get; private init; }
    public int LiveUpdateDebounceMs { get; private init; }

    /// <summary>When true every new error creates a notification (FR-24). Default false: the Notifications module is optional (NFR-07).</summary>
    public bool NotificationsEnabled { get; private init; }
    /// <summary>The facility the notifications of EMT belong to; the notifications table of the Notifications module shows only notifications of a known facility.</summary>
    public Guid NotificationsFacilityId { get; private init; }

    /// <summary>
    /// The merged config that is sent to the frontend with LoadModule. It contains only values the frontend may see.
    /// </summary>
    public JObject JsonRepresentation { get; private init; }

    private ModuleConfig(Guid moduleId, bool moduleActive, string moduleVersion, string moduleInstanceName, string translationsPath, int cleanupIntervalHours,
        bool locationEnabled, int locationFdfVersion, string locationProjectPath, List<string> selectableAreaTypes, string errorNumberPrefix, int errorNumberMinDigits, int liveUpdateDebounceMs, bool notificationsEnabled, Guid notificationsFacilityId, JObject mergedConfig)
    {
        NotificationsEnabled = notificationsEnabled;
        NotificationsFacilityId = notificationsFacilityId;
        LocationEnabled = locationEnabled;
        ModuleId = moduleId;
        ModuleActive = moduleActive;
        ModuleVersion = moduleVersion;
        ModuleInstanceName = moduleInstanceName;
        TranslationsPath = translationsPath;
        CleanupIntervalHours = cleanupIntervalHours;
        LocationFdfVersion = locationFdfVersion;
        LocationProjectPath = locationProjectPath;
        SelectableAreaTypes = selectableAreaTypes;
        ErrorNumberPrefix = errorNumberPrefix;
        ErrorNumberMinDigits = errorNumberMinDigits;
        LiveUpdateDebounceMs = liveUpdateDebounceMs;
        JsonRepresentation = mergedConfig;
    }

    public static bool TryReadFromIlp(IClientController ilp, [NotNullWhen(true)] out ModuleConfig? moduleConfig)
    {
        moduleConfig = null;

        var configExists = ilp.TryGetState(Constants.Ilp.ModuleConfig.ILP_PATH, out var configBaseState);
        if (!configExists)
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Cannot initialize moduleConfig from ILP, base path does not exist: {Constants.Ilp.ModuleConfig.ILP_PATH}");
            return false;
        }

        if (configBaseState is not DictionaryState<StringState, IBaseState> configEntryDictState)
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Cannot initialize moduleConfig from ILP, base path has wrong type, expecting dictState: {Constants.Ilp.ModuleConfig.ILP_PATH}");
            return false;
        }

        var moduleIdString = configEntryDictState.FirstOrDefault().Key;
        if (moduleIdString is null || !Guid.TryParse(moduleIdString.Value, out var moduleId))
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Cannot initialize moduleConfig from ILP, moduleId is no guid ({moduleIdString?.Value}): {Constants.Ilp.ModuleConfig.ILP_PATH}");
            return false;
        }

        var actualConfig = configEntryDictState.FirstOrDefault().Value;
        if (actualConfig is null)
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Cannot initialize moduleConfig from ILP, base path is missing an entry for the moduleConfig: {Constants.Ilp.ModuleConfig.ILP_PATH}");
            return false;
        }

        if (actualConfig is not DictionaryState<StringState, IBaseState> configDictState)
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Cannot initialize moduleConfig from ILP, base path has wrong type, expecting dictState: {Constants.Ilp.ModuleConfig.ILP_PATH} / first entry");
            return false;
        }

        if (!IlpJsonConvertorExtension.TryToJson(configDictState, out var jsonToken) || jsonToken.Type != JTokenType.Object)
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Could not convert moduleConfig from ILP to json! Frontend will be missing its config...");
            return false;
        }

        using var reader = new StreamReader(Constants.Ilp.ModuleConfig.DEFAULT_MODULE_CONFIG);
        if (!TryCreate(moduleId, (JObject)jsonToken, JObject.Parse(reader.ReadToEnd()), out moduleConfig))
        {
            return false;
        }

        Logging.Log(LogLevel.Info, $"{LOG_PREFIX} Successfully received ModuleConfig from ILP");

        return true;
    }

    /// <summary>
    /// Merges the config from ILP over the default config and parses it. Arrays from ILP replace the default arrays.
    /// </summary>
    public static bool TryCreate(Guid moduleId, JObject ilpConfig, JObject defaultConfig, [NotNullWhen(true)] out ModuleConfig? moduleConfig)
    {
        moduleConfig = null;

        if (defaultConfig.DeepClone() is not JObject mergedConfig)
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} There was an error when cloning the default config!");
            return false;
        }

        var mergeSettings = new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace };
        mergedConfig.Merge(ilpConfig, mergeSettings);

        return TryParse(moduleId, mergedConfig, out moduleConfig);
    }

    private static bool TryParse(Guid moduleId, JObject jsonConfig, [NotNullWhen(true)] out ModuleConfig? moduleConfig)
    {
        moduleConfig = null;

        var allPresent = true;
        allPresent &= jsonConfig.TryGetValue<string>(Constants.Ilp.ModuleConfig.MODULE_VERSION, JTokenType.String, out var moduleVersion, out var error);
        LogMissing(error);
        allPresent &= jsonConfig.TryGetValue<string>(Constants.Ilp.ModuleConfig.MODULE_INSTANCE_NAME, JTokenType.String, out var instanceName, out error);
        LogMissing(error);
        allPresent &= jsonConfig.TryGetValue<bool>(Constants.Ilp.ModuleConfig.ACTIVE, JTokenType.Boolean, out var moduleActive, out error);
        LogMissing(error);
        allPresent &= jsonConfig.TryGetValue<string>(Constants.Ilp.ModuleConfig.TRANSLATIONS_PATH, JTokenType.String, out var translationsPath, out error);
        LogMissing(error);

        allPresent &= TryGetSection(jsonConfig, Constants.Ilp.ModuleConfig.RETENTION, out var retention);
        allPresent &= TryGetSection(jsonConfig, Constants.Ilp.ModuleConfig.LOCATION, out var location);
        allPresent &= TryGetSection(jsonConfig, Constants.Ilp.ModuleConfig.ERROR_NUMBER, out var errorNumber);
        allPresent &= TryGetSection(jsonConfig, Constants.Ilp.ModuleConfig.LIST, out var list);

        if (!allPresent)
        {
            return false;
        }

        allPresent &= retention!.TryGetValue<int>(Constants.Ilp.ModuleConfig.CLEANUP_INTERVAL_HOURS, JTokenType.Integer, out var cleanupIntervalHours, out error);
        LogMissing(error);
        allPresent &= location!.TryGetValue<int>(Constants.Ilp.ModuleConfig.FDF_VERSION, JTokenType.Integer, out var fdfVersion, out error);
        LogMissing(error);
        allPresent &= location!.TryGetValue<string>(Constants.Ilp.ModuleConfig.PROJECT_PATH, JTokenType.String, out var projectPath, out error);
        LogMissing(error);
        allPresent &= location!.TryGetValue<List<string>>(Constants.Ilp.ModuleConfig.SELECTABLE_AREA_TYPES, JTokenType.Array, out var areaTypes, out error);
        LogMissing(error);
        allPresent &= errorNumber!.TryGetValue<string>(Constants.Ilp.ModuleConfig.PREFIX, JTokenType.String, out var prefix, out error);
        LogMissing(error);
        allPresent &= errorNumber!.TryGetValue<int>(Constants.Ilp.ModuleConfig.MIN_DIGITS, JTokenType.Integer, out var minDigits, out error);
        LogMissing(error);
        allPresent &= list!.TryGetValue<int>(Constants.Ilp.ModuleConfig.LIVE_UPDATE_DEBOUNCE_MS, JTokenType.Integer, out var debounceMs, out error);
        LogMissing(error);

        if (!allPresent)
        {
            return false;
        }

        if (cleanupIntervalHours < 1 || minDigits < 1 || debounceMs < 0)
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} ModuleConfig contains out of range values (CleanupIntervalHours >= 1, MinDigits >= 1, LiveUpdateDebounceMs >= 0 expected)");
            return false;
        }

        // optional switch: missing or not a boolean = enabled, so the location stays mandatory as approved (BR-19)
        var locationEnabled = !location!.TryGetValue(Constants.Ilp.ModuleConfig.LOCATION_ENABLED, out var enabledToken) || enabledToken.Type != JTokenType.Boolean || enabledToken.Value<bool>();

        // optional section: missing = no notifications; enabled without a valid facility id is switched off with a warning
        var notificationsEnabled = false;
        var facilityId = Guid.Empty;
        if (jsonConfig.TryGetValue(Constants.Ilp.ModuleConfig.NOTIFICATIONS, out var notificationsToken) && notificationsToken is JObject notifications)
        {
            notificationsEnabled = notifications.TryGetValue(Constants.Ilp.ModuleConfig.NOTIFICATIONS_ENABLED, out var notificationsEnabledToken) && notificationsEnabledToken.Type == JTokenType.Boolean && notificationsEnabledToken.Value<bool>();
            if (notificationsEnabled && (!notifications.TryGetValue(Constants.Ilp.ModuleConfig.NOTIFICATIONS_FACILITY_ID, out var facilityToken) || !Guid.TryParse(facilityToken.Value<string>(), out facilityId) || facilityId == Guid.Empty))
            {
                Logging.Log(LogLevel.Warning, $"{LOG_PREFIX} Notifications.Enabled is true but Notifications.FacilityId is not a guid, the notifications are switched off");
                notificationsEnabled = false;
            }
        }

        moduleConfig = new ModuleConfig(moduleId, moduleActive, moduleVersion!, instanceName!, translationsPath!, cleanupIntervalHours, locationEnabled, fdfVersion, projectPath!, areaTypes!, prefix!, minDigits, debounceMs, notificationsEnabled, facilityId, jsonConfig);
        return true;
    }

    private static bool TryGetSection(JObject json, string name, [NotNullWhen(true)] out JObject? section)
    {
        section = null;
        if (!json.TryGetValue(name, out var token) || token is not JObject sectionObject)
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} ModuleConfig section '{name}' missing or not an object");
            return false;
        }

        section = sectionObject;
        return true;
    }

    private static void LogMissing(string? error)
    {
        if (error is not null)
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} {error}");
        }
    }
}
