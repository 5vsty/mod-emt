using FSDEV.MOD.Emt.Emt.Domain;

namespace FSDEV.MOD.Emt.Emt_Server.Storage;

/// <summary>
/// Persistence of the errors. The only implementation writes to ILP (ADR-002 / D-11: everything stays on ILP).
/// Every method returns false when the change was not applied.
/// </summary>
public interface IErrorStore
{
    /// <summary>Creates the base states (Emt, Emt/Errors, Emt/Sequence, Emt/Sequence/ErrorNumber) if they are missing.</summary>
    bool TryEnsureRoots();

    bool TryLoadAll(out List<ErrorRecord> records);

    bool TryReadSequence(out long value);

    /// <summary>Raises the persisted counter to at least <paramref name="value"/>.</summary>
    bool TryEnsureSequenceAtLeast(long value);

    /// <summary>Writes the counter and the new error in one change request.</summary>
    bool TryCreate(ErrorRecord record, long newSequenceValue);

    bool TryUpdate(ErrorRecord record);

    /// <summary>Deletes the error, together with its month dictionary when it was the last error of that month.</summary>
    bool TryDelete(ErrorRecord record);
}
