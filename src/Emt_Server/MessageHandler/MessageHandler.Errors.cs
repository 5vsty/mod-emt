using FSDEV.FES.Server.Client;
using FSDEV.MOD.Emt.Emt;
using FSDEV.MOD.Emt.Emt.Domain;
using Newtonsoft.Json.Linq;
using Constants = FSDEV.MOD.Emt.Emt.Constants;

namespace FSDEV.MOD.Emt.Emt_Server.MessageHandler;

internal partial class MessageHandler
{
    private void HandleGetReportFormData(in WsMessageContext<EmtSession, EmtWebsocket> ctx, JObject message, JToken token)
    {
        const string response = Constants.Frontend.Responses.GET_REPORT_FORM_DATA;
        var result = Errors.GetReportFormData(ctx.session.UserGuid);
        if (!result.Successful)
        {
            Send(ctx, CreateFailure(response, token, message, result));
            return;
        }

        var reply = CreateResponse(response, token, message, true);
        reply["ErrorCodes"] = new JArray(result.Value!.Codes.Select(code => code.ToOptionJson()));
        reply["Locations"] = new JArray(result.Value.Locations.Select(location => location.ToJson()));
        reply["NoActiveCodes"] = result.Value.NoActiveCodes;
        reply["LocationEnabled"] = result.Value.LocationEnabled;
        Send(ctx, reply);
    }

    private void HandleSubmitError(in WsMessageContext<EmtSession, EmtWebsocket> ctx, JObject message, JToken token)
    {
        const string response = Constants.Frontend.Responses.SUBMIT_ERROR;

        if (!message.TryGetGuid("ClientRequestId", out var clientRequestId, out _))
        {
            Send(ctx, CreateInvalidPayload(response, token, message, "ClientRequestId"));
            return;
        }

        if (!TryGetOptionalString(message, "LocationId", out var locationId))
        {
            Send(ctx, CreateInvalidPayload(response, token, message, "LocationId"));
            return;
        }

        if (!message.TryGetGuid("ErrorCodeId", out var errorCodeId, out _))
        {
            Send(ctx, CreateInvalidPayload(response, token, message, "ErrorCodeId"));
            return;
        }

        if (!TryGetOptionalString(message, "Description", out var description))
        {
            Send(ctx, CreateInvalidPayload(response, token, message, "Description"));
            return;
        }

        var result = Errors.Submit(ctx.session.UserGuid, clientRequestId, locationId, errorCodeId, description);
        if (!result.Successful)
        {
            Send(ctx, CreateFailure(response, token, message, result));
            return;
        }

        var reply = CreateResponse(response, token, message, true);
        reply["ErrorId"] = result.Value!.Error.Id.ToString();
        reply["ErrorNumber"] = result.Value.Error.ErrorNumber;
        reply["Duplicate"] = result.Value.Duplicate;
        reply["NoManagersConfigured"] = result.Value.NoManagersConfigured;
        Send(ctx, reply);
    }

    private void HandleGetMyErrors(in WsMessageContext<EmtSession, EmtWebsocket> ctx, JObject message, JToken token)
    {
        const string response = Constants.Frontend.Responses.GET_MY_ERRORS;
        var result = Errors.GetMyErrors(ctx.session.UserGuid);
        if (!result.Successful)
        {
            Send(ctx, CreateFailure(response, token, message, result));
            return;
        }

        var reply = CreateResponse(response, token, message, true);
        reply["Errors"] = new JArray(result.Value!.Select(error => error.ToSummaryJson()));

        // the report page shows the same errors in the user's own table
        if (MyErrors.TryOpen(ctx.session.UserGuid, out var tableModelId))
        {
            reply["TableModelId"] = tableModelId.ToString();
        }

        Send(ctx, reply);
    }

    private void HandleDeleteError(in WsMessageContext<EmtSession, EmtWebsocket> ctx, JObject message, JToken token)
    {
        const string response = Constants.Frontend.Responses.DELETE_ERROR;

        if (!message.TryGetGuid("ErrorId", out var errorId, out _))
        {
            Send(ctx, CreateInvalidPayload(response, token, message, "ErrorId"));
            return;
        }

        var result = Errors.Delete(ctx.session.UserGuid, errorId);
        Send(ctx, result.Successful ? CreateResponse(response, token, message, true) : CreateFailure(response, token, message, result));
    }

    private void HandleCancelError(in WsMessageContext<EmtSession, EmtWebsocket> ctx, JObject message, JToken token)
    {
        if (!TryGetErrorId(message, token, out var errorId))
        {
            Send(ctx, CreateInvalidPayload(Constants.Frontend.Responses.CHANGE_ERROR, token, message, "ErrorId"));
            return;
        }

        if (!message.TryGetValue<int>("ExpectedVersion", JTokenType.Integer, out var expectedVersion, out _) || !TryGetOptionalString(message, "Text", out var text))
        {
            Send(ctx, CreateInvalidPayload(Constants.Frontend.Responses.CHANGE_ERROR, token, message, "ExpectedVersion"));
            return;
        }

        SendChangeResponse(ctx, message, errorId, Errors.Cancel(ctx.session.UserGuid, errorId, expectedVersion, text));
    }

    private void HandleGetErrorDetail(in WsMessageContext<EmtSession, EmtWebsocket> ctx, JObject message, JToken token)
    {
        const string response = Constants.Frontend.Responses.GET_ERROR_DETAIL;

        if (!TryGetErrorId(message, token, out var errorId))
        {
            Send(ctx, CreateInvalidPayload(response, token, message, "ErrorId"));
            return;
        }

        var result = Errors.GetDetail(ctx.session.UserGuid, errorId);
        if (!result.Successful)
        {
            Send(ctx, CreateFailure(response, token, message, result));
            return;
        }

        var reply = CreateResponse(response, token, message, true);
        reply["Error"] = result.Value!.Error.ToSummaryJson(true);
        reply["Events"] = new JArray(result.Value.Error.Events.Select(e => e.ToJson()));
        reply["AllowedActions"] = new JArray(result.Value.AllowedActions.Select(action => action.ToWire()));

        // all errors stay on ILP, there is no archive (Gate 2 condition D-11); the field is part of the contract
        reply["Archived"] = false;
        Send(ctx, reply);
    }

    private void HandleChangeErrorStatus(in WsMessageContext<EmtSession, EmtWebsocket> ctx, JObject message, JToken token)
    {
        if (!TryGetErrorId(message, token, out var errorId)
            || !message.TryGetValue<string>("Action", JTokenType.String, out var action, out _)
            || !message.TryGetValue<int>("ExpectedVersion", JTokenType.Integer, out var expectedVersion, out _)
            || !TryGetOptionalString(message, "Text", out var text))
        {
            Send(ctx, CreateInvalidPayload(Constants.Frontend.Responses.CHANGE_ERROR, token, message, "ErrorId"));
            return;
        }

        SendChangeResponse(ctx, message, errorId, Errors.ChangeStatus(ctx.session.UserGuid, errorId, action, expectedVersion, text));
    }

    private void HandleAssignError(in WsMessageContext<EmtSession, EmtWebsocket> ctx, JObject message, JToken token)
    {
        if (!TryGetErrorId(message, token, out var errorId)
            || !TryGetOptionalGuid(message, "AssigneeUserId", out var assignee)
            || !message.TryGetValue<int>("ExpectedVersion", JTokenType.Integer, out var expectedVersion, out _))
        {
            Send(ctx, CreateInvalidPayload(Constants.Frontend.Responses.CHANGE_ERROR, token, message, "ErrorId"));
            return;
        }

        SendChangeResponse(ctx, message, errorId, Errors.Assign(ctx.session.UserGuid, errorId, assignee, expectedVersion));
    }

    private void HandleChangeErrorSeverity(in WsMessageContext<EmtSession, EmtWebsocket> ctx, JObject message, JToken token)
    {
        if (!TryGetErrorId(message, token, out var errorId)
            || !message.TryGetValue<string>("Severity", JTokenType.String, out var severity, out _)
            || !message.TryGetValue<int>("ExpectedVersion", JTokenType.Integer, out var expectedVersion, out _))
        {
            Send(ctx, CreateInvalidPayload(Constants.Frontend.Responses.CHANGE_ERROR, token, message, "ErrorId"));
            return;
        }

        SendChangeResponse(ctx, message, errorId, Errors.ChangeSeverity(ctx.session.UserGuid, errorId, severity, expectedVersion));
    }

    private void HandleAddErrorNote(in WsMessageContext<EmtSession, EmtWebsocket> ctx, JObject message, JToken token)
    {
        if (!TryGetErrorId(message, token, out var errorId) || !TryGetOptionalString(message, "Text", out var text))
        {
            Send(ctx, CreateInvalidPayload(Constants.Frontend.Responses.CHANGE_ERROR, token, message, "ErrorId"));
            return;
        }

        SendChangeResponse(ctx, message, errorId, Errors.AddNote(ctx.session.UserGuid, errorId, text));
    }

    private void HandleGetAssignableUsers(in WsMessageContext<EmtSession, EmtWebsocket> ctx, JObject message, JToken token)
    {
        const string response = Constants.Frontend.Responses.GET_ASSIGNABLE_USERS;
        var result = Errors.GetAssignableUsers(ctx.session.UserGuid);
        if (!result.Successful)
        {
            Send(ctx, CreateFailure(response, token, message, result));
            return;
        }

        var reply = CreateResponse(response, token, message, true);
        reply["Users"] = new JArray(result.Value!.Select(user => new JObject { ["UserId"] = user.UserId.ToString(), ["DisplayName"] = user.DisplayName }));
        Send(ctx, reply);
    }

    /// <summary>
    /// funcResponseChangeError: the value is the ErrorId. On success and on a conflict the response carries the current error,
    /// so the frontend can show what changed (AC-61).
    /// </summary>
    private static void SendChangeResponse(in WsMessageContext<EmtSession, EmtWebsocket> ctx, JObject message, Guid errorId, OperationResult<ErrorRecord> result)
    {
        var response = result.Successful
            ? CreateResponse(Constants.Frontend.Responses.CHANGE_ERROR, errorId.ToString(), message, true)
            : CreateFailure(Constants.Frontend.Responses.CHANGE_ERROR, errorId.ToString(), message, result);

        if (result.Value is not null)
        {
            response["Error"] = result.Value.ToSummaryJson();
        }

        Send(ctx, response);
    }
}
