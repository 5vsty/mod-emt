using FSDEV.Common.Logger;
using FSDEV.ILP.Intralogistikplattform.SystemState;
using FSDEV.ILP.Intralogistikplattform.SystemState.Client;
using FSDEV.ILP.Intralogistikplattform.SystemState.Messages;
using FSDEV.ILP.Intralogistikplattform.SystemState.States;
using FSDEV.ILP.Intralogistikplattform.SystemState.States.CollectionStates;
using FSDEV.ILP.Intralogistikplattform.SystemState.States.PrimitiveStates;
using FSDEV.ILP.Intralogistikplattform.SystemState_Client.Helpers;
using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Emt.Emt_Server.Storage;
using System.Diagnostics.CodeAnalysis;
using Constants = FSDEV.MOD.Emt.Emt.Constants;

namespace FSDEV.MOD.Emt.Emt_Server.Catalog;

/// <summary>
/// errorCodes.json: a dictionary with one entry per error code (key = code id), same layout as notificationTypes.json / devices.json.
/// </summary>
public class IlpCatalogStorage : ICatalogStorage
{
    private const string LOG_PREFIX = $"[{nameof(IlpCatalogStorage)}]";

    private const string ID = "Id";
    private const string CODE = "Code";
    private const string TITLE = "Title";
    private const string DESCRIPTION = "Description";
    private const string INSTRUCTIONS = "Instructions";
    private const string GROUP = "Group";
    private const string DEFAULT_SEVERITY = "DefaultSeverity";
    private const string ACTIVE = "Active";
    private const string IS_OTHER = "IsOther";
    private const string SCOPE = "Scope";
    private const string USED_AT = "UsedAt";
    private const string VERSION = "Version";
    private const string CHANGED_AT = "ChangedAt";
    private const string CHANGED_BY = "ChangedBy";

    private readonly IClientController Ilp;

    public IlpCatalogStorage(IClientController ilp)
    {
        Ilp = ilp;
    }

    public bool TryLoadAll(out List<ErrorCode> codes)
    {
        codes = [];

        if (!TryGetOrCreateRoot(out var root))
        {
            return false;
        }

        foreach (var state in root.Values)
        {
            if (TryParse(state, out var code))
            {
                codes.Add(code);
            }
            else
            {
                Logging.Log(LogLevel.Warning, $"{LOG_PREFIX} The error code {state.Key} could not be read and is skipped");
            }
        }

        return true;
    }

    public bool TrySave(ErrorCode code)
    {
        if (!TryGetOrCreateRoot(out var root))
        {
            return false;
        }

        root[new StringState(code.Id.ToString())] = ToState(code);

        var changeRequest = new ChangeRequest(Ilp);
        changeRequest.ChangeState(root);
        return Send(changeRequest, $"save error code {code.Code}");
    }

    public bool TryDelete(Guid codeId)
    {
        if (!TryGetOrCreateRoot(out var root))
        {
            return false;
        }

        if (!root.TryGetValue(new StringState(codeId.ToString()), out var state))
        {
            return true;
        }

        var changeRequest = new ChangeRequest(Ilp);
        changeRequest.DeleteState(state);
        return Send(changeRequest, $"delete error code {codeId}");
    }

    private bool TryGetOrCreateRoot([NotNullWhen(true)] out DictionaryState<StringState, IBaseState>? root)
    {
        root = null;

        if (!Ilp.TryGetState(Constants.Ilp.Paths.ERROR_CODES, out var state))
        {
            var changeRequest = new ChangeRequest(Ilp);
            changeRequest.CreateState(new DictionaryState<StringState, IBaseState>(Constants.Ilp.Paths.ERROR_CODES));
            if (!Send(changeRequest, "create the catalog") || !Ilp.TryGetState(Constants.Ilp.Paths.ERROR_CODES, out state))
            {
                return false;
            }
        }

        if (state is not DictionaryState<StringState, IBaseState> dict)
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} {Constants.Ilp.Paths.ERROR_CODES} is not a dictState");
            return false;
        }

        root = dict;
        return true;
    }

    private static DictionaryState<StringState, IBaseState> ToState(ErrorCode code)
    {
        var state = new DictionaryState<StringState, IBaseState>(code.Id.ToString())
        {
            [new StringState(ID)] = new StringState(code.Id.ToString()),
            [new StringState(CODE)] = new StringState(code.Code),
            [new StringState(TITLE)] = new StringState(code.Title),
            [new StringState(DESCRIPTION)] = new StringState(code.Description),
            [new StringState(INSTRUCTIONS)] = new StringState(code.Instructions),
            [new StringState(GROUP)] = new StringState(code.Group),
            [new StringState(DEFAULT_SEVERITY)] = new StringState(code.DefaultSeverity.ToWire()),
            [new StringState(ACTIVE)] = new BoolState(code.Active),
            [new StringState(IS_OTHER)] = new BoolState(code.IsOther),
            [new StringState(SCOPE)] = new StringState(code.Scope),
            [new StringState(USED_AT)] = new LongState(code.UsedAt),
            [new StringState(VERSION)] = new IntState(code.Version),
            [new StringState(CHANGED_AT)] = new LongState(code.ChangedAt),
            [new StringState(CHANGED_BY)] = new StringState(code.ChangedBy),
        };

        return state;
    }

    private static bool TryParse(IBaseState state, [NotNullWhen(true)] out ErrorCode? code)
    {
        code = null;

        if (state is not DictionaryState<StringState, IBaseState> dict)
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Cannot parse error code, expecting dictState: {state.Key}");
            return false;
        }

        var allPresent = true;
        allPresent &= dict.TryGetString(ID, out var id);
        allPresent &= dict.TryGetString(CODE, out var codeValue);
        allPresent &= TryGetText(dict, TITLE, out var title);
        allPresent &= TryGetText(dict, DESCRIPTION, out var description);
        allPresent &= TryGetText(dict, INSTRUCTIONS, out var instructions);
        allPresent &= dict.TryGetString(GROUP, out var group);
        allPresent &= dict.TryGetString(DEFAULT_SEVERITY, out var severity);
        allPresent &= dict.TryGetBool(ACTIVE, out var active);
        allPresent &= dict.TryGetBool(IS_OTHER, out var isOther);
        allPresent &= dict.TryGetString(SCOPE, out var scope);
        allPresent &= dict.TryGetLongTolerant(USED_AT, out var usedAt);
        allPresent &= dict.TryGetIntTolerant(VERSION, out var version);
        allPresent &= dict.TryGetLongTolerant(CHANGED_AT, out var changedAt);
        allPresent &= dict.TryGetString(CHANGED_BY, out var changedBy);

        if (!allPresent)
        {
            return false;
        }

        if (!Guid.TryParse(id, out var codeId) || !ErrorEnumExtensions.TryParseSeverity(severity, out var defaultSeverity))
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Cannot parse error code {state.Key}, invalid id or severity");
            return false;
        }

        code = new ErrorCode
        {
            Id = codeId,
            Code = codeValue,
            Title = title,
            Description = description,
            Instructions = instructions,
            Group = group,
            DefaultSeverity = defaultSeverity,
            Active = active,
            IsOther = isOther,
            Scope = scope,
            UsedAt = usedAt,
            Version = version,
            ChangedAt = changedAt,
            ChangedBy = changedBy,
        };
        return true;
    }

    /// <summary>A text is a string; codes saved before texts became single-language hold a dictionary per language instead.</summary>
    private static bool TryGetText(DictionaryState<StringState, IBaseState> dict, string key, out string text)
    {
        if (dict.TryGetString(key, out var value))
        {
            text = value;
            return true;
        }

        if (dict.TryGetDict(key, out var perLanguage))
        {
            text = LegacyLocalizedText.ToSingleText(ToMap(perLanguage));
            return true;
        }

        text = string.Empty;
        return false;
    }

    private static Dictionary<string, string> ToMap(DictionaryState<StringState, IBaseState> state)
    {
        var result = new Dictionary<string, string>();
        foreach (var (language, value) in state)
        {
            if (value is StringState text)
            {
                result[language.Value] = text.Value;
            }
        }

        return result;
    }

    private bool Send(ChangeRequest changeRequest, string description)
    {
        var result = Ilp.SendChangeRequest(changeRequest);
        if (result != ExecutionResponse.SUCCEEDED)
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Cannot {description} on ilp: {result}");
            return false;
        }

        return true;
    }
}

/// <summary>settings.json: RetentionDays, OtherCodeEnabled, Version.</summary>
public class IlpSettingsStorage : ISettingsStorage
{
    private const string LOG_PREFIX = $"[{nameof(IlpSettingsStorage)}]";

    private const string RETENTION_DAYS = "RetentionDays";
    private const string OTHER_CODE_ENABLED = "OtherCodeEnabled";
    private const string VERSION = "Version";

    private readonly IClientController Ilp;

    public IlpSettingsStorage(IClientController ilp)
    {
        Ilp = ilp;
    }

    public bool TryLoad(out EmtSettings? settings)
    {
        settings = null;

        if (!Ilp.TryGetState(Constants.Ilp.Paths.SETTINGS, out var state))
        {
            return true;
        }

        if (state is not DictionaryState<StringState, IBaseState> dict)
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} {Constants.Ilp.Paths.SETTINGS} is not a dictState");
            return false;
        }

        if (dict.Count == 0)
        {
            return true;
        }

        var allPresent = true;
        allPresent &= dict.TryGetIntTolerant(RETENTION_DAYS, out var retentionDays);
        allPresent &= dict.TryGetBool(OTHER_CODE_ENABLED, out var otherCodeEnabled);
        allPresent &= dict.TryGetIntTolerant(VERSION, out var version);
        if (!allPresent)
        {
            return false;
        }

        settings = new EmtSettings { RetentionDays = retentionDays, OtherCodeEnabled = otherCodeEnabled, Version = version };
        return true;
    }

    public bool TrySave(EmtSettings settings)
    {
        var changeRequest = new ChangeRequest(Ilp);

        if (Ilp.TryGetState(Constants.Ilp.Paths.SETTINGS, out var state) && state is DictionaryState<StringState, IBaseState> dict)
        {
            Apply(dict, settings);
            changeRequest.ChangeState(dict);
        }
        else
        {
            var created = new DictionaryState<StringState, IBaseState>(Constants.Ilp.Paths.SETTINGS);
            Apply(created, settings);
            changeRequest.CreateState(created);
        }

        var result = Ilp.SendChangeRequest(changeRequest);
        if (result != ExecutionResponse.SUCCEEDED)
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Cannot save settings on ilp: {result}");
            return false;
        }

        return true;
    }

    private static void Apply(DictionaryState<StringState, IBaseState> dict, EmtSettings settings)
    {
        dict[new StringState(RETENTION_DAYS)] = new IntState(settings.RetentionDays);
        dict[new StringState(OTHER_CODE_ENABLED)] = new BoolState(settings.OtherCodeEnabled);
        dict[new StringState(VERSION)] = new IntState(settings.Version);
    }
}
