using FSDEV.FES.Server.Client;
using FSDEV.MOD.Emt.Emt;
using FSDEV.MOD.Emt.Emt.Domain;
using Newtonsoft.Json.Linq;
using Constants = FSDEV.MOD.Emt.Emt.Constants;

namespace FSDEV.MOD.Emt.Emt_Server.MessageHandler;

internal partial class MessageHandler
{
    private void HandleGetErrorCodes(in WsMessageContext<EmtSession, EmtWebsocket> ctx, JObject message, JToken token)
    {
        const string response = Constants.Frontend.Responses.GET_ERROR_CODES;
        var result = Admin.GetCodes(ctx.session.UserGuid);
        if (!result.Successful)
        {
            Send(ctx, CreateFailure(response, token, message, result));
            return;
        }

        var reply = CreateResponse(response, token, message, true);
        reply["ErrorCodes"] = new JArray(result.Value!.Select(code => code.ToFullJson()));
        Send(ctx, reply);
    }

    private void HandleSaveErrorCode(in WsMessageContext<EmtSession, EmtWebsocket> ctx, JObject message, JToken token)
    {
        const string response = Constants.Frontend.Responses.SAVE_ERROR_CODE;

        if (!TryGetOptionalGuid(message, "ErrorCodeId", out var errorCodeId))
        {
            Send(ctx, CreateInvalidPayload(response, token, message, "ErrorCodeId"));
            return;
        }

        if (!TryGetOptionalInt(message, "ExpectedVersion", out var expectedVersion))
        {
            Send(ctx, CreateInvalidPayload(response, token, message, "ExpectedVersion"));
            return;
        }

        if (!TryGetOptionalString(message, "Code", out var code) || !TryGetOptionalString(message, "Group", out var group) || !TryGetOptionalString(message, "DefaultSeverity", out var severity))
        {
            Send(ctx, CreateInvalidPayload(response, token, message, "Code"));
            return;
        }

        if (!TryGetOptionalString(message, "Title", out var title))
        {
            Send(ctx, CreateInvalidPayload(response, token, message, "Title"));
            return;
        }

        if (!TryGetOptionalString(message, "Description", out var description))
        {
            Send(ctx, CreateInvalidPayload(response, token, message, "Description"));
            return;
        }

        if (!TryGetOptionalString(message, "Instructions", out var instructions))
        {
            Send(ctx, CreateInvalidPayload(response, token, message, "Instructions"));
            return;
        }

        if (!message.TryGetValue<bool>("Active", JTokenType.Boolean, out var active, out _))
        {
            Send(ctx, CreateInvalidPayload(response, token, message, "Active"));
            return;
        }

        var result = Admin.SaveCode(ctx.session.UserGuid, errorCodeId, expectedVersion, code, title, description, instructions, group, severity, active);
        if (!result.Successful)
        {
            var failure = CreateFailure(response, token, message, result);
            if (result.Value is not null)
            {
                failure["ErrorCode"] = result.Value.ToFullJson();
            }

            Send(ctx, failure);
            return;
        }

        var reply = CreateResponse(response, token, message, true);
        reply["ErrorCode"] = result.Value!.ToFullJson();
        Send(ctx, reply);
    }

    private void HandleDeleteErrorCode(in WsMessageContext<EmtSession, EmtWebsocket> ctx, JObject message, JToken token)
    {
        const string response = Constants.Frontend.Responses.DELETE_ERROR_CODE;

        if (!message.TryGetGuid("ErrorCodeId", out var errorCodeId, out _))
        {
            Send(ctx, CreateInvalidPayload(response, token, message, "ErrorCodeId"));
            return;
        }

        var result = Admin.DeleteCode(ctx.session.UserGuid, errorCodeId);
        Send(ctx, result.Successful ? CreateResponse(response, token, message, true) : CreateFailure(response, token, message, result));
    }

    private void HandleGetSettings(in WsMessageContext<EmtSession, EmtWebsocket> ctx, JObject message, JToken token)
    {
        const string response = Constants.Frontend.Responses.GET_SETTINGS;
        var result = Admin.GetSettings(ctx.session.UserGuid);
        if (!result.Successful)
        {
            Send(ctx, CreateFailure(response, token, message, result));
            return;
        }

        var reply = CreateResponse(response, token, message, true);
        reply["Settings"] = result.Value!.Settings.ToJson();
        reply["Health"] = result.Value.Health;
        Send(ctx, reply);
    }

    private void HandleSaveSettings(in WsMessageContext<EmtSession, EmtWebsocket> ctx, JObject message, JToken token)
    {
        const string response = Constants.Frontend.Responses.SAVE_SETTINGS;

        if (!message.TryGetValue<int>("RetentionDays", JTokenType.Integer, out var retentionDays, out _)
            || !message.TryGetValue<bool>("OtherCodeEnabled", JTokenType.Boolean, out var otherCodeEnabled, out _)
            || !message.TryGetValue<int>("ExpectedVersion", JTokenType.Integer, out var expectedVersion, out _))
        {
            Send(ctx, CreateInvalidPayload(response, token, message, "RetentionDays"));
            return;
        }

        var result = Admin.SaveSettings(ctx.session.UserGuid, retentionDays, otherCodeEnabled, expectedVersion);
        if (!result.Successful)
        {
            var failure = CreateFailure(response, token, message, result);
            if (result.Value is not null)
            {
                failure["Settings"] = result.Value.Settings.ToJson();
            }

            Send(ctx, failure);
            return;
        }

        var reply = CreateResponse(response, token, message, true);
        reply["Settings"] = result.Value!.Settings.ToJson();
        Send(ctx, reply);
    }
}
