using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Emt.Emt.Validation;

namespace FSDEV.MOD.Emt.Emt_Server.Catalog;

/// <summary>
/// RetentionDays (at least 30, default 365) and OtherCodeEnabled. Versioned like the catalog entries.
/// </summary>
public class SettingsStore
{
    private readonly object Lock = new();
    private readonly ISettingsStorage Storage;
    private EmtSettings Settings = new();

    public SettingsStore(ISettingsStorage storage)
    {
        Storage = storage;
    }

    public bool Initialize()
    {
        lock (Lock)
        {
            if (!Storage.TryLoad(out var loaded))
            {
                return false;
            }

            if (loaded is null)
            {
                var defaults = new EmtSettings();
                if (!Storage.TrySave(defaults))
                {
                    return false;
                }

                loaded = defaults;
            }

            Settings = loaded;
            return true;
        }
    }

    public EmtSettings Current
    {
        get
        {
            lock (Lock)
            {
                return new EmtSettings { RetentionDays = Settings.RetentionDays, OtherCodeEnabled = Settings.OtherCodeEnabled, Version = Settings.Version };
            }
        }
    }

    public OperationResult<EmtSettings> Save(int retentionDays, bool otherCodeEnabled, int expectedVersion)
    {
        var errors = SettingsValidator.Validate(retentionDays);
        if (errors.Count > 0)
        {
            return OperationResult<EmtSettings>.Invalid(errors);
        }

        lock (Lock)
        {
            if (expectedVersion != Settings.Version)
            {
                return OperationResult<EmtSettings>.Conflicted(null, Current);
            }

            var updated = new EmtSettings { RetentionDays = retentionDays, OtherCodeEnabled = otherCodeEnabled, Version = Settings.Version + 1 };
            if (!Storage.TrySave(updated))
            {
                return OperationResult<EmtSettings>.Fail(Reasons.UNAVAILABLE);
            }

            Settings = updated;
            return OperationResult<EmtSettings>.Ok(Current);
        }
    }
}
