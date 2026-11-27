using FSDEV.Common.Logger;
using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Emt.Emt.Validation;
using Constants = FSDEV.MOD.Emt.Emt.Constants;

namespace FSDEV.MOD.Emt.Emt_Server.Catalog;

/// <summary>
/// The error code catalog: uniqueness (BR-01), locking of used codes (BR-02, BR-08), the protected "other" code (BR-22)
/// and versioning for optimistic concurrency. EMT is the only writer, so one in-process lock is enough.
/// </summary>
public class CatalogStore
{
    private const string LOG_PREFIX = $"[{nameof(CatalogStore)}]";

    private readonly object Lock = new();
    private readonly Dictionary<Guid, ErrorCode> Codes = new();
    private readonly ICatalogStorage Storage;
    private readonly Func<bool> OtherCodeEnabled;
    private readonly Func<Guid, bool> IsUsedByError;

    private long ChangeCounter;

    /// <summary>Raised after every change of what the catalog shows (the catalog table refreshes itself with it).</summary>
    public event Action? Changed;

    public CatalogStore(ICatalogStorage storage, Func<bool> otherCodeEnabled, Func<Guid, bool> isUsedByError)
    {
        Storage = storage;
        OtherCodeEnabled = otherCodeEnabled;
        IsUsedByError = isUsedByError;
    }

    /// <summary>Changes with every change of the catalog or of the active state of the "other" code (07 funcErrorCodesChanged).</summary>
    public long Version
    {
        get
        {
            lock (Lock)
            {
                return ChangeCounter;
            }
        }
    }

    public bool Initialize(long now)
    {
        lock (Lock)
        {
            if (!Storage.TryLoadAll(out var codes))
            {
                return false;
            }

            Codes.Clear();
            foreach (var code in codes)
            {
                Codes[code.Id] = code;
            }

            if (!Codes.ContainsKey(Guid.Parse(Constants.Catalog.OTHER_CODE_ID)))
            {
                var other = CreateOtherCode(now);
                if (!Storage.TrySave(other))
                {
                    return false;
                }

                Codes[other.Id] = other;
            }

            return true;
        }
    }

    /// <summary>
    /// Reads the catalog from the storage again: somebody else can change it (the configuration manager that fills the states from its files after EMT
    /// started, an administrator). Only a real difference raises <see cref="Changed"/>.
    /// </summary>
    /// <returns>true when the codes were read; <paramref name="changed"/> tells whether they differ from the ones in memory.</returns>
    public bool TryReload(out bool changed)
    {
        changed = false;
        if (!Storage.TryLoadAll(out var codes))
        {
            return false;
        }

        lock (Lock)
        {
            var loaded = codes.ToDictionary(code => code.Id);
            if (!loaded.ContainsKey(Guid.Parse(Constants.Catalog.OTHER_CODE_ID)) && Codes.TryGetValue(Guid.Parse(Constants.Catalog.OTHER_CODE_ID), out var other))
            {
                loaded[other.Id] = other;
            }

            changed = loaded.Count != Codes.Count || loaded.Any(entry => !Codes.TryGetValue(entry.Key, out var known) || known.Version != entry.Value.Version || known.ChangedAt != entry.Value.ChangedAt);
            if (!changed)
            {
                return true;
            }

            Codes.Clear();
            foreach (var entry in loaded)
            {
                Codes[entry.Key] = entry.Value;
            }

            ChangeCounter++;
            Changed?.Invoke();
            return true;
        }
    }

    public static ErrorCode CreateOtherCode(long now) => new()
    {
        Id = Guid.Parse(Constants.Catalog.OTHER_CODE_ID),
        Code = Constants.Catalog.OTHER_CODE,
        Title = "Sonstiges",
        Description = string.Empty,
        Instructions = string.Empty,
        Group = string.Empty,
        DefaultSeverity = Severity.Warning,
        Active = true,
        IsOther = true,
        Version = 1,
        ChangedAt = now,
        ChangedBy = string.Empty,
    };

    /// <summary>The "other" code can be switched off in the settings, regardless of its own Active flag.</summary>
    public bool IsEffectivelyActive(ErrorCode code) => code.Active && (!code.IsOther || OtherCodeEnabled());

    public List<ErrorCode> GetAll()
    {
        lock (Lock)
        {
            return Codes.Values.OrderBy(code => code.Code, StringComparer.OrdinalIgnoreCase).Select(WithEffectiveActive).ToList();
        }
    }

    /// <summary>The codes the report form offers.</summary>
    public List<ErrorCode> GetActiveForReport()
    {
        lock (Lock)
        {
            return Codes.Values.Where(IsEffectivelyActive).OrderBy(code => code.Code, StringComparer.OrdinalIgnoreCase).Select(code => code.Clone()).ToList();
        }
    }

    public bool TryGet(Guid id, out ErrorCode code)
    {
        lock (Lock)
        {
            if (Codes.TryGetValue(id, out var found))
            {
                code = WithEffectiveActive(found);
                return true;
            }

            code = new ErrorCode();
            return false;
        }
    }

    public OperationResult<ErrorCode> Save(Guid? id, int? expectedVersion, string? code, string? title, string? description,
        string? instructions, string? group, string? defaultSeverity, bool active, string userName, long now)
    {
        var errors = ErrorCodeValidator.Validate(code, title, description, instructions, group);
        if (!ErrorEnumExtensions.TryParseSeverity(defaultSeverity, out var severity))
        {
            errors.Add(new ValidationError("DefaultSeverity", ValidationKeys.INVALID));
        }

        if (errors.Count > 0)
        {
            return OperationResult<ErrorCode>.Invalid(errors);
        }

        var trimmedCode = code!.Trim();

        lock (Lock)
        {
            if (Codes.Values.Any(existing => existing.Id != id && string.Equals(existing.Code, trimmedCode, StringComparison.OrdinalIgnoreCase)))
            {
                return OperationResult<ErrorCode>.Fail(Reasons.CODE_EXISTS);
            }

            ErrorCode toSave;
            if (id is null)
            {
                if (expectedVersion is not null)
                {
                    return OperationResult<ErrorCode>.Invalid([new ValidationError("ExpectedVersion", ValidationKeys.INVALID)]);
                }

                toSave = new ErrorCode { Id = Guid.NewGuid(), Version = 1 };
            }
            else
            {
                if (!Codes.TryGetValue(id.Value, out var existing))
                {
                    return OperationResult<ErrorCode>.Fail(Reasons.NOT_FOUND);
                }

                if (existing.IsOther)
                {
                    return OperationResult<ErrorCode>.Fail(Reasons.OTHER_CODE_PROTECTED);
                }

                if (expectedVersion is null)
                {
                    return OperationResult<ErrorCode>.Invalid([new ValidationError("ExpectedVersion", ValidationKeys.REQUIRED)]);
                }

                if (expectedVersion != existing.Version)
                {
                    return OperationResult<ErrorCode>.Conflicted(existing.ChangedBy, WithEffectiveActive(existing));
                }

                if (!string.Equals(existing.Code, trimmedCode, StringComparison.Ordinal) && (existing.IsUsed || IsUsedByError(existing.Id)))
                {
                    return OperationResult<ErrorCode>.Fail(Reasons.CODE_LOCKED);
                }

                toSave = existing.Clone();
                toSave.Version = existing.Version + 1;
            }

            toSave.Code = trimmedCode;
            toSave.Title = title!.Trim();
            toSave.Description = description?.Trim() ?? string.Empty;
            toSave.Instructions = instructions?.Trim() ?? string.Empty;
            toSave.Group = group?.Trim() ?? string.Empty;
            toSave.DefaultSeverity = severity;
            toSave.Active = active;
            toSave.ChangedAt = now;
            toSave.ChangedBy = userName;

            if (!Storage.TrySave(toSave))
            {
                return OperationResult<ErrorCode>.Fail(Reasons.UNAVAILABLE);
            }

            Codes[toSave.Id] = toSave;
            ChangeCounter++;
            Changed?.Invoke();
            return OperationResult<ErrorCode>.Ok(WithEffectiveActive(toSave));
        }
    }

    public OperationResult<bool> Delete(Guid id)
    {
        lock (Lock)
        {
            if (!Codes.TryGetValue(id, out var code))
            {
                return OperationResult<bool>.Fail(Reasons.NOT_FOUND);
            }

            if (code.IsOther)
            {
                return OperationResult<bool>.Fail(Reasons.OTHER_CODE_PROTECTED);
            }

            if (code.IsUsed || IsUsedByError(id))
            {
                return OperationResult<bool>.Fail(Reasons.CODE_IN_USE);
            }

            if (!Storage.TryDelete(id))
            {
                return OperationResult<bool>.Fail(Reasons.UNAVAILABLE);
            }

            _ = Codes.Remove(id);
            ChangeCounter++;
            Changed?.Invoke();
            return OperationResult<bool>.Ok(true);
        }
    }

    /// <summary>Remembers that the code was used by an error. This does not change the version of the code.</summary>
    public bool TryMarkUsed(Guid id, long at)
    {
        lock (Lock)
        {
            if (!Codes.TryGetValue(id, out var code))
            {
                return false;
            }

            if (code.IsUsed)
            {
                return true;
            }

            var updated = code.Clone();
            updated.UsedAt = at;
            if (!Storage.TrySave(updated))
            {
                Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Cannot persist the first use of error code {code.Code}");
                return false;
            }

            Codes[id] = updated;
            Changed?.Invoke();
            return true;
        }
    }

    /// <summary>Called when the settings change the availability of the "other" code.</summary>
    public void NotifyOtherCodeChanged()
    {
        lock (Lock)
        {
            ChangeCounter++;
            Changed?.Invoke();
        }
    }

    private ErrorCode WithEffectiveActive(ErrorCode code)
    {
        var copy = code.Clone();
        copy.Active = IsEffectivelyActive(code);
        return copy;
    }
}
