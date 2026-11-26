using FSDEV.Common.Logger;
using FSDEV.FES.Server.Client;
using FSDEV.MOD.Emt.Emt;
using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Emt.Emt.Validation;
using FSDEV.MOD.Emt.Emt_Server.Catalog;
using FSDEV.MOD.Emt.Emt_Server.Errors;
using FSDEV.MOD.Emt.Emt_Server.Tables;
using Newtonsoft.Json.Linq;
using Constants = FSDEV.MOD.Emt.Emt.Constants;

namespace FSDEV.MOD.Emt.Emt_Server.MessageHandler;

internal partial class MessageHandler
{
    private delegate void MessageHandlerDelegate(in WsMessageContext<EmtSession, EmtWebsocket> messageContext, JObject message, JToken token);

    private readonly ErrorService Errors;
    private readonly CatalogAdminService Admin;
    private readonly MyErrorsTables MyErrors;
    private Dictionary<string, MessageHandlerDelegate> MessageHandlers = new();
    private const string LOG_PREFIX = $"[{nameof(MessageHandler)}]";

    public MessageHandler(ErrorService errors, CatalogAdminService admin, MyErrorsTables myErrors)
    {
        Errors = errors;
        Admin = admin;
        MyErrors = myErrors;
    }

    internal bool TryInitialize(Client<EmtSession, EmtWebsocket> FsConnector)
    {
        if (!FsConnector.TryAddMessageHandler(LOG_PREFIX, HandleMessage))
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Could not add websocket message handler!");
            return false;
        }

        RegisterHandlers();
        return true;
    }

    internal void RegisterHandlers()
    {
        MessageHandlers = new()
        {
            [Constants.Frontend.Requests.GET_REPORT_FORM_DATA] = HandleGetReportFormData,
            [Constants.Frontend.Requests.SUBMIT_ERROR] = HandleSubmitError,
            [Constants.Frontend.Requests.GET_MY_ERRORS] = HandleGetMyErrors,
            [Constants.Frontend.Requests.CANCEL_ERROR] = HandleCancelError,
            [Constants.Frontend.Requests.DELETE_ERROR] = HandleDeleteError,
            [Constants.Frontend.Requests.GET_ERROR_DETAIL] = HandleGetErrorDetail,
            [Constants.Frontend.Requests.CHANGE_ERROR_STATUS] = HandleChangeErrorStatus,
            [Constants.Frontend.Requests.ASSIGN_ERROR] = HandleAssignError,
            [Constants.Frontend.Requests.CHANGE_ERROR_SEVERITY] = HandleChangeErrorSeverity,
            [Constants.Frontend.Requests.ADD_ERROR_NOTE] = HandleAddErrorNote,
            [Constants.Frontend.Requests.GET_ASSIGNABLE_USERS] = HandleGetAssignableUsers,
            [Constants.Frontend.Requests.GET_ERROR_CODES] = HandleGetErrorCodes,
            [Constants.Frontend.Requests.SAVE_ERROR_CODE] = HandleSaveErrorCode,
            [Constants.Frontend.Requests.DELETE_ERROR_CODE] = HandleDeleteErrorCode,
            [Constants.Frontend.Requests.GET_SETTINGS] = HandleGetSettings,
            [Constants.Frontend.Requests.SAVE_SETTINGS] = HandleSaveSettings,
        };
    }

    internal void HandleMessage(in WsMessageContext<EmtSession, EmtWebsocket> messageContext)
    {
        var messages = messageContext.IncomingMessage;
        var userId = messageContext.session.UserGuid;

        foreach (var messageToken in messages)
        {
            if (messageToken.Type != JTokenType.Object)
            {
                Logging.Log(LogLevel.Warning, $"{LOG_PREFIX} Cannot handle message, not an object:\n{messageToken}");
                continue;
            }

            var message = messageToken.ToObject<JObject>();
            if (message is null)
            {
                Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Do not handle WS message from user {userId}. Message could not be converted to JObject");
                continue;
            }

            if (!message.TryGetValue("ID", out var idToken) || idToken.Type != JTokenType.String || idToken.ToString() is not string id)
            {
                Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Do not handle WS message from user {userId}. Message does not contain correct 'ID'");
                continue;
            }

            if (id == Constants.Frontend.FRONTEND_MODULES_GUID)
            {
                HandleFrontendModulesMessage(messageContext, userId, message);
                continue;
            }

            if (id != Constants.Frontend.EMT_GUID)
            {
                continue;
            }

            foreach (var (handlerKey, handler) in MessageHandlers)
            {
                if (!message.TryGetValue(handlerKey, out var token))
                {
                    continue;
                }

                handler(messageContext, message, token);
                break;
            }
        }
    }

    private void HandleFrontendModulesMessage(WsMessageContext<EmtSession, EmtWebsocket> messageContext, Guid userId, JObject message)
    {
        if (!message.TryGetValue("ModuleName", out var moduleNameToken) || moduleNameToken.Type != JTokenType.String || moduleNameToken.ToString() != Constants.Service.APP_NAME)
        {
            return;
        }

        if (!message.TryGetValue("Action", out var actionToken) || actionToken.Type != JTokenType.String || actionToken.ToString() != "StateUpdate")
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Do not handle WS message from user {userId}. Message does not contain correct 'Action'");
            return;
        }

        if (!message.TryGetValue("State", out var stateToken) || stateToken.Type != JTokenType.String || stateToken.ToString() != "INITIALIZED")
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Do not handle WS message from user {userId}. Message does not contain correct 'State'");
            return;
        }

        SendInitialMessage(messageContext);
    }

    private static void SendInitialMessage(in WsMessageContext<EmtSession, EmtWebsocket> messageContext)
    {
        // EMT sends no data when the plugin starts; the pages request what they need
    }

    // ---- helpers shared by the handler files ----

    /// <summary>Starts a response: ID, the response key with the token of the request, Successful.</summary>
    private static JObject CreateResponse(string responseKey, JToken requestToken, JObject request, bool successful)
    {
        var response = new JObject
        {
            [Constants.Frontend.ID] = Constants.Frontend.EMT_GUID,
            [responseKey] = requestToken.DeepClone(),
            ["Successful"] = successful,
        };

        // the pages match responses to requests with the request id
        if (request.TryGetValue("RequestId", out var requestId))
        {
            response["RequestId"] = requestId.DeepClone();
        }

        return response;
    }

    private static JObject CreateFailure<T>(string responseKey, JToken requestToken, JObject request, OperationResult<T> result)
    {
        var response = CreateResponse(responseKey, requestToken, request, false);
        response["Reason"] = result.Reason;
        if (result.ValidationErrors.Count > 0)
        {
            response["ValidationErrors"] = new JArray(result.ValidationErrors.Select(error => error.ToJson()));
        }

        if (result.ConflictByName is not null)
        {
            response["ConflictByName"] = result.ConflictByName;
        }

        return response;
    }

    private static JObject CreateInvalidPayload(string responseKey, JToken requestToken, JObject request, string field)
    {
        var response = CreateResponse(responseKey, requestToken, request, false);
        response["Reason"] = Reasons.VALIDATION_FAILED;
        response["ValidationErrors"] = new JArray(new ValidationError(field, ValidationKeys.INVALID).ToJson());
        return response;
    }

    private static void Send(in WsMessageContext<EmtSession, EmtWebsocket> messageContext, JObject response) =>
        messageContext.AddResponse([response]);

    /// <summary>An optional string: absent or null is accepted (null result), anything else than a string is not.</summary>
    private static bool TryGetOptionalString(JObject message, string key, out string? value)
    {
        value = null;
        if (!message.TryGetValue(key, out var token) || token.Type == JTokenType.Null)
        {
            return true;
        }

        if (token.Type != JTokenType.String)
        {
            return false;
        }

        value = token.ToObject<string>();
        return true;
    }

    private static bool TryGetOptionalGuid(JObject message, string key, out Guid? value)
    {
        value = null;
        if (!message.TryGetValue(key, out var token) || token.Type == JTokenType.Null)
        {
            return true;
        }

        if (token.Type != JTokenType.String || !Guid.TryParse(token.ToObject<string>(), out var guid))
        {
            return false;
        }

        value = guid;
        return true;
    }

    private static bool TryGetOptionalInt(JObject message, string key, out int? value)
    {
        value = null;
        if (!message.TryGetValue(key, out var token) || token.Type == JTokenType.Null)
        {
            return true;
        }

        if (token.Type != JTokenType.Integer)
        {
            return false;
        }

        value = token.ToObject<int>();
        return true;
    }

    /// <summary>The id of the error: the field ErrorId, or the token of the request if that is an id.</summary>
    private static bool TryGetErrorId(JObject message, JToken token, out Guid errorId)
    {
        if (message.TryGetGuid("ErrorId", out errorId, out _) && errorId != Guid.Empty)
        {
            return true;
        }

        return token.Type == JTokenType.String && Guid.TryParse(token.ToObject<string>(), out errorId);
    }
}
