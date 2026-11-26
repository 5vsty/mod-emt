using FSDEV.MOD.Emt.Emt.Domain;

namespace FSDEV.MOD.Emt.Emt_Server.Catalog;

/// <summary>Persistence of the error code catalog (Config/Modules/Emt/errorCodes.json).</summary>
public interface ICatalogStorage
{
    bool TryLoadAll(out List<ErrorCode> codes);

    /// <summary>Creates or replaces the code.</summary>
    bool TrySave(ErrorCode code);

    bool TryDelete(Guid codeId);
}

/// <summary>Persistence of the settings (Config/Modules/Emt/settings.json).</summary>
public interface ISettingsStorage
{
    /// <summary>Returns false if the settings could not be read; <paramref name="settings"/> is null if there are none yet.</summary>
    bool TryLoad(out EmtSettings? settings);

    bool TrySave(EmtSettings settings);
}
