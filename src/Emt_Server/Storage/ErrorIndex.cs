using FSDEV.MOD.Emt.Emt.Domain;
using System.Diagnostics.CodeAnalysis;

namespace FSDEV.MOD.Emt.Emt_Server.Storage;

/// <summary>
/// In-memory copy of all errors within the retention time. Source for the error list table, "my errors", idempotency
/// (ClientRequestId) and the used-check of the catalog. All reads return copies, so callers cannot change the index by accident.
/// </summary>
public class ErrorIndex
{
    private readonly object Lock = new();
    private readonly Dictionary<Guid, ErrorRecord> ErrorsById = new();
    private readonly Dictionary<Guid, Guid> ErrorIdByClientRequestId = new();

    public void Load(IEnumerable<ErrorRecord> records)
    {
        lock (Lock)
        {
            ErrorsById.Clear();
            ErrorIdByClientRequestId.Clear();
            foreach (var record in records)
            {
                AddInternal(record);
            }
        }
    }

    public void Set(ErrorRecord record)
    {
        lock (Lock)
        {
            AddInternal(record);
        }
    }

    public bool Remove(Guid errorId)
    {
        lock (Lock)
        {
            if (!ErrorsById.Remove(errorId, out var removed))
            {
                return false;
            }

            _ = ErrorIdByClientRequestId.Remove(removed.ClientRequestId);
            return true;
        }
    }

    public bool TryGet(Guid errorId, [NotNullWhen(true)] out ErrorRecord? record)
    {
        lock (Lock)
        {
            record = ErrorsById.TryGetValue(errorId, out var found) ? found.Clone() : null;
            return record is not null;
        }
    }

    public bool TryGetByClientRequestId(Guid clientRequestId, [NotNullWhen(true)] out ErrorRecord? record)
    {
        lock (Lock)
        {
            record = null;
            return ErrorIdByClientRequestId.TryGetValue(clientRequestId, out var errorId) && TryGet(errorId, out record);
        }
    }

    public List<ErrorRecord> GetAll()
    {
        lock (Lock)
        {
            return ErrorsById.Values.Select(e => e.Clone()).ToList();
        }
    }

    public int Count
    {
        get
        {
            lock (Lock)
            {
                return ErrorsById.Count;
            }
        }
    }

    public long HighestNumber
    {
        get
        {
            lock (Lock)
            {
                return ErrorsById.Count == 0 ? 0 : ErrorsById.Values.Max(e => e.Number);
            }
        }
    }

    public bool IsCodeUsed(Guid errorCodeId)
    {
        lock (Lock)
        {
            return ErrorsById.Values.Any(e => e.ErrorCodeId == errorCodeId);
        }
    }

    /// <summary>The errors reported by the user that are still relevant: not final, resolved, or final since at most <paramref name="recentDays"/> days (07 funcGetMyErrors).</summary>
    public List<ErrorRecord> GetOfReporter(Guid userId, long nowMs, int recentDays, int maxCount)
    {
        var cutoff = nowMs - (recentDays * 24L * 60 * 60 * 1000);
        lock (Lock)
        {
            return ErrorsById.Values
                .Where(e => e.ReporterUserId == userId && (!ErrorLifecycle.IsFinal(e.Status) || e.FinalizedAt >= cutoff))
                .OrderByDescending(e => e.ReportedAt)
                .Take(maxCount)
                .Select(e => e.Clone())
                .ToList();
        }
    }

    private void AddInternal(ErrorRecord record)
    {
        var copy = record.Clone();
        ErrorsById[copy.Id] = copy;
        ErrorIdByClientRequestId[copy.ClientRequestId] = copy.Id;
    }
}
