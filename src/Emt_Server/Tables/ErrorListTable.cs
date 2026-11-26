using FSDEV.AMF.Controls.CSharpControls.Table;
using FSDEV.Common.Logger;
using FSDEV.Common.Translations;
using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Emt.Emt_Server.Errors;
using System.Diagnostics.CodeAnalysis;
using Constants = FSDEV.MOD.Emt.Emt.Constants;
using IClient = FSDEV.FES.Server.Client.IClient;

namespace FSDEV.MOD.Emt.Emt_Server.Tables;

/// <summary>
/// The error list of the "Danh sách lỗi" page: a manual AMF table that is filled from the error index. Every change of an error
/// updates its row, the table pushes the update to the open frontends (debounced, NFR-01).
/// </summary>
public class ErrorListTable : IErrorObserver
{
    private const string LOG_PREFIX = $"[{nameof(ErrorListTable)}]";

    public const string ERROR_NUMBER = "ErrorNumber";
    public const string NUMBER = "Number";
    public const string REPORTED_AT = "ReportedAt";
    public const string LOCATION_PATH = "LocationPath";
    public const string ERROR_CODE = "ErrorCode";
    public const string CODE_TITLE = "CodeTitle";
    public const string SEVERITY = "Severity";
    public const string STATUS = "Status";
    public const string REPORTER_NAME = "ReporterName";
    public const string ASSIGNEE_NAME = "AssigneeName";

    private ManualDictTable? Table;

    public static List<ColumnDefinitionAttribute> GetColumns() =>
    [
        new(true, ERROR_NUMBER),
        new(false, NUMBER, true),
        new(true, REPORTED_AT, true, false, CsvExportFormat.DateTime),
        new(true, LOCATION_PATH),
        new(true, ERROR_CODE),
        new(true, CODE_TITLE),
        new(true, SEVERITY),
        new(true, STATUS),
        new(true, REPORTER_NAME),
        new(true, ASSIGNEE_NAME),
    ];

    public static Dictionary<string, object> ToRow(ErrorRecord error) => new()
    {
        [ERROR_NUMBER] = error.ErrorNumber,
        [NUMBER] = error.Number,
        [REPORTED_AT] = error.ReportedAt,
        [LOCATION_PATH] = error.LocationPath,
        [ERROR_CODE] = error.ErrorCode,
        [CODE_TITLE] = error.CodeTitle,
        [SEVERITY] = error.Severity.ToWire(),
        [STATUS] = error.Status.ToWire(),
        [REPORTER_NAME] = error.ReporterName,
        [ASSIGNEE_NAME] = error.AssigneeName,
    };

    /// <param name="fesClient">The client of the table; it has to be the authorizing client, the table checks no permissions itself.</param>
    public bool Initialize(IClient fesClient, ITranslations translations, int debounceMs)
    {
        if (!ManualDictTable.TryCreateManualTable(Guid.Parse(Constants.Tables.ERROR_LIST), GetColumns(), fesClient, translations, out var table))
        {
            Logging.Log(LogLevel.Fatal, $"{LOG_PREFIX} Error while trying to create the error list table.");
            return false;
        }

        table.LiveFrontendUpdates = true;
        table.UseDebounceTimer = true;
        table.SetUpdateDebounce(TimeSpan.FromMilliseconds(debounceMs));
        Table = table;
        return true;
    }

    public void Load(IEnumerable<ErrorRecord> errors)
    {
        foreach (var error in errors)
        {
            Upsert(error);
        }
    }

    public void Upsert(ErrorRecord error)
    {
        if (Table is null)
        {
            return;
        }

        var row = ToRow(error);
        if (Table.ContainsRow(error.Id))
        {
            if (!Table.TryUpdateRow(error.Id, row))
            {
                Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Could not update the row of error {error.ErrorNumber}");
            }
        }
        else if (!Table.TryAddRow(error.Id, row))
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Could not add the row of error {error.ErrorNumber}");
        }
    }

    public void Remove(Guid errorId)
    {
        _ = Table?.TryRemoveRow(errorId, out _);
    }

    public bool ContainsRow(Guid errorId) => Table?.ContainsRow(errorId) ?? false;

    public void OnErrorChanged(ErrorRecord error, ErrorChangeType changeType)
    {
        if (changeType == ErrorChangeType.Deleted)
        {
            Remove(error.Id);
            return;
        }

        Upsert(error);
    }
}
