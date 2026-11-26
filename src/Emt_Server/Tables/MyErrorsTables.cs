using FSDEV.AMF.Controls.CSharpControls.Table;
using FSDEV.Common.Logger;
using FSDEV.Common.Translations;
using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Emt.Emt_Server.Errors;
using FSDEV.MOD.Emt.Emt_Server.Security;
using FSDEV.MOD.Emt.Emt_Server.Storage;
using System.Security.Cryptography;
using System.Text;
using Constants = FSDEV.MOD.Emt.Emt.Constants;
using IClient = FSDEV.FES.Server.Client.IClient;

namespace FSDEV.MOD.Emt.Emt_Server.Tables;

/// <summary>
/// The "my errors" list of the report page as an AMF table. AMF tables do not filter rows per user, so every reporter gets an own
/// table: its model id is derived from the user id, only that user may query it, and it holds the same errors as funcGetMyErrors
/// (open, resolved, or final since at most <see cref="Constants.Limits.MY_ERRORS_RECENT_DAYS"/> days). A table is created when the
/// user first asks for it and is updated whenever one of the user's errors changes.
/// </summary>
public class MyErrorsTables : IErrorObserver
{
    private const string LOG_PREFIX = $"[{nameof(MyErrorsTables)}]";
    private const string MODEL_ID_SEED = "FSDEV.MOD.Emt.MyErrors:";

    private readonly IClient FesClient;
    private readonly EmtPermissionService Permissions;
    private readonly ErrorIndex Index;
    private readonly IClock Clock;
    private readonly ITranslations Translations;
    private readonly int DebounceMs;

    private readonly object Lock = new();
    private readonly Dictionary<Guid, UserTable> TablesByUser = [];

    /// <summary>ManualDictTable cannot list its rows, so the ids are kept alongside (as in ErrorCodeTable).</summary>
    private sealed record UserTable(ManualDictTable Table, HashSet<Guid> RowIds);

    public MyErrorsTables(IClient fesClient, EmtPermissionService permissions, ErrorIndex index, IClock clock, ITranslations translations, int debounceMs)
    {
        FesClient = fesClient;
        Permissions = permissions;
        Index = index;
        Clock = clock;
        Translations = translations;
        DebounceMs = debounceMs;
    }

    /// <summary>The model id of the user's table: the same for a user across restarts, different for every user.</summary>
    public static Guid GetModelId(Guid userId)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(MODEL_ID_SEED + userId.ToString("D")));
        return new Guid(hash);
    }

    /// <summary>The errors the user's table shows, newest first.</summary>
    public List<ErrorRecord> GetErrorsOf(Guid userId) => Index.GetOfReporter(userId, Clock.UtcNowMs, Constants.Limits.MY_ERRORS_RECENT_DAYS, Constants.Limits.MAX_MY_ERRORS);

    /// <summary>Creates the user's table if needed, brings its rows up to date and returns its model id.</summary>
    public bool TryOpen(Guid userId, out Guid modelId)
    {
        modelId = GetModelId(userId);
        lock (Lock)
        {
            if (!TablesByUser.TryGetValue(userId, out var table))
            {
                var ownerOnly = new AuthorizingFesClient(FesClient, modelId, requester => requester == userId && Permissions.HasPermission(requester, Constants.Permissions.REPORT_ERRORS));
                if (!ManualDictTable.TryCreateManualTable(modelId, ErrorListTable.GetColumns(), ownerOnly, Translations, out var created))
                {
                    Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Could not create the table of user {userId}");
                    return false;
                }

                created.LiveFrontendUpdates = true;
                created.UseDebounceTimer = true;
                created.SetUpdateDebounce(TimeSpan.FromMilliseconds(DebounceMs));
                table = new UserTable(created, []);
                TablesByUser[userId] = table;
            }

            Sync(userId, table);
            return true;
        }
    }

    public void OnErrorChanged(ErrorRecord error, ErrorChangeType changeType)
    {
        lock (Lock)
        {
            if (TablesByUser.TryGetValue(error.ReporterUserId, out var table))
            {
                Sync(error.ReporterUserId, table);
            }
        }
    }

    /// <summary>Makes the rows match <see cref="GetErrorsOf"/>: an error can also leave the list without being deleted (final for too long).</summary>
    private void Sync(Guid userId, UserTable userTable)
    {
        var (table, rowIds) = userTable;
        var errors = GetErrorsOf(userId);
        var wanted = errors.Select(error => error.Id).ToHashSet();

        foreach (var staleId in rowIds.Where(id => !wanted.Contains(id)).ToList())
        {
            _ = table.TryRemoveRow(staleId, out _);
            _ = rowIds.Remove(staleId);
        }

        foreach (var error in errors)
        {
            var row = ErrorListTable.ToRow(error);
            var saved = table.ContainsRow(error.Id) ? table.TryUpdateRow(error.Id, row) : table.TryAddRow(error.Id, row);
            if (saved)
            {
                _ = rowIds.Add(error.Id);
            }
            else
            {
                Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Could not write the row of error {error.ErrorNumber} for user {userId}");
            }
        }
    }
}
