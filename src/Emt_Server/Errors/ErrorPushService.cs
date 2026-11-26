using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Emt.Emt_Server.Security;
using Newtonsoft.Json.Linq;
using Constants = FSDEV.MOD.Emt.Emt.Constants;

namespace FSDEV.MOD.Emt.Emt_Server.Errors;

/// <summary>
/// Pushes changes to the users who may see them: the error managers and the reporter of the error. Nobody else gets the data (NFR-03).
/// </summary>
public class ErrorPushService : IErrorObserver
{
    private readonly IPushSender Sender;
    private readonly EmtPermissionService Permissions;

    public ErrorPushService(IPushSender sender, EmtPermissionService permissions)
    {
        Sender = sender;
        Permissions = permissions;
    }

    public void OnErrorChanged(ErrorRecord error, ErrorChangeType changeType)
    {
        var recipients = Permissions.GetUsersWithPermission(Constants.Permissions.MANAGE_ERRORS).ToHashSet();

        // the reporter sees the own error read only, but only while the reporter may still report
        if (Permissions.HasPermission(error.ReporterUserId, Constants.Permissions.REPORT_ERRORS))
        {
            _ = recipients.Add(error.ReporterUserId);
        }

        if (recipients.Count == 0)
        {
            return;
        }

        Sender.SendToUsers(recipients, new JArray
        {
            new JObject
            {
                [Constants.Frontend.ID] = Constants.Frontend.EMT_GUID,
                [Constants.Frontend.Push.ERROR_CHANGED] = error.Id.ToString(),
                ["ErrorId"] = error.Id.ToString(),
                ["ChangeType"] = changeType.ToString(),
                ["Error"] = error.ToSummaryJson(),
            }
        });
    }

    /// <summary>Tells everybody who uses the catalog (report form, catalog page) that it changed.</summary>
    public void SendErrorCodesChanged(long version)
    {
        var recipients = Permissions.GetUsersWithPermission(Constants.Permissions.REPORT_ERRORS).ToHashSet();
        recipients.UnionWith(Permissions.GetUsersWithPermission(Constants.Permissions.MANAGE_CATALOG));

        if (recipients.Count == 0)
        {
            return;
        }

        Sender.SendToUsers(recipients, new JArray
        {
            new JObject
            {
                [Constants.Frontend.ID] = Constants.Frontend.EMT_GUID,
                [Constants.Frontend.Push.ERROR_CODES_CHANGED] = version,
            }
        });
    }
}
