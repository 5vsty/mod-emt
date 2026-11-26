using FSDEV.AMF.Controls.CSharpControls.Table;
using FSDEV.Common.Logger;
using FSDEV.Common.Translations;
using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Emt.Emt_Server.Catalog;
using Constants = FSDEV.MOD.Emt.Emt.Constants;
using IClient = FSDEV.FES.Server.Client.IClient;

namespace FSDEV.MOD.Emt.Emt_Server.Tables;

/// <summary>
/// The catalog table of the "Danh mục mã lỗi / Cấu hình" page. It is rebuilt from the catalog after every change
/// (the catalog is small and changes rarely).
/// </summary>
public class ErrorCodeTable
{
    private const string LOG_PREFIX = $"[{nameof(ErrorCodeTable)}]";

    public const string CODE = "Code";
    public const string TITLE = "Title";
    public const string GROUP = "Group";
    public const string DEFAULT_SEVERITY = "DefaultSeverity";
    public const string ACTIVE = "Active";
    public const string IS_USED = "IsUsed";
    public const string IS_OTHER = "IsOther";

    private readonly CatalogStore Catalog;
    private readonly object Lock = new();
    private readonly HashSet<Guid> RowIds = [];
    private ManualDictTable? Table;

    public ErrorCodeTable(CatalogStore catalog)
    {
        Catalog = catalog;
    }

    public static List<ColumnDefinitionAttribute> GetColumns() =>
    [
        new(true, CODE),
        new(true, TITLE),
        new(true, GROUP),
        new(true, DEFAULT_SEVERITY),
        new(true, ACTIVE),
        new(true, IS_USED, true),
        new(true, IS_OTHER, true),
    ];

    public static Dictionary<string, object> ToRow(ErrorCode code) => new()
    {
        [CODE] = code.Code,
        [TITLE] = code.Title,
        [GROUP] = code.Group,
        [DEFAULT_SEVERITY] = code.DefaultSeverity.ToWire(),
        [ACTIVE] = code.Active,
        [IS_USED] = code.IsUsed,
        [IS_OTHER] = code.IsOther,
    };

    /// <param name="fesClient">The client of the table; it has to be the authorizing client, the table checks no permissions itself.</param>
    public bool Initialize(IClient fesClient, ITranslations translations)
    {
        if (!ManualDictTable.TryCreateManualTable(Guid.Parse(Constants.Tables.ERROR_CODES), GetColumns(), fesClient, translations, out var table))
        {
            Logging.Log(LogLevel.Fatal, $"{LOG_PREFIX} Error while trying to create the error code table.");
            return false;
        }

        table.LiveFrontendUpdates = true;
        table.UseDebounceTimer = true;
        table.SetUpdateDebounce(TimeSpan.FromSeconds(1));
        Table = table;
        Refresh();
        return true;
    }

    /// <summary>Makes the rows match the catalog.</summary>
    public void Refresh()
    {
        lock (Lock)
        {
            if (Table is null)
            {
                return;
            }

            var codes = Catalog.GetAll();
            var currentIds = codes.Select(code => code.Id).ToHashSet();

            foreach (var staleId in RowIds.Where(id => !currentIds.Contains(id)).ToList())
            {
                _ = Table.TryRemoveRow(staleId, out _);
                _ = RowIds.Remove(staleId);
            }

            foreach (var code in codes)
            {
                var row = ToRow(code);
                var saved = Table.ContainsRow(code.Id) ? Table.TryUpdateRow(code.Id, row) : Table.TryAddRow(code.Id, row);
                if (saved)
                {
                    _ = RowIds.Add(code.Id);
                }
                else
                {
                    Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Could not write the row of error code {code.Code}");
                }
            }
        }
    }
}
