using FSDEV.AMF.Controls.CSharpControls.Table;
using FSDEV.Common.Logger;
using FSDEV.FES.Server.Client;
using FSDEV.FES.Server.Client.Sessions;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using IClient = FSDEV.FES.Server.Client.IClient;
using TableConstants = FSDEV.AMF.Controls.CSharpControls.Table.Constants;

namespace FSDEV.MOD.Emt.Emt_Server.Security;

/// <summary>
/// Decorator around the FES client that is handed to an AMF table. The AMF table does not check permissions itself
/// (TableBase), so this client only forwards table requests of one table model to the table if the user has the permission,
/// and blocks messages to websockets whose user lost the permission (NFR-03, R-T1).
/// </summary>
public class AuthorizingFesClient : IClient
{
    private const string LOG_PREFIX = $"[{nameof(AuthorizingFesClient)}]";

    private static readonly string[] TableFunctions = ["funcRequestEntries", "funcRequestCsv", "funcStopUpdates", "funcCancelQuery"];

    private readonly IClient Inner;
    private readonly Func<Guid, bool> HasPermission;
    private readonly Guid TableModelId;
    private readonly ConcurrentDictionary<Guid, Guid> UserByWebsocketId = new();

    /// <param name="inner">The real FES client.</param>
    /// <param name="tableModelId">The frontend model id of the table this client is used for.</param>
    /// <param name="hasPermission">Whether the user may use the table.</param>
    public AuthorizingFesClient(IClient inner, Guid tableModelId, Func<Guid, bool> hasPermission)
    {
        Inner = inner;
        TableModelId = tableModelId;
        HasPermission = hasPermission;
    }

    public IClient.FrontendServerAdded? OnFrontendServerAdded
    {
        get => Inner.OnFrontendServerAdded;
        set => Inner.OnFrontendServerAdded = value;
    }

    public bool TryAddMessageHandler(string handlerId, IClient.HandleFrontendMessageBase handler) =>
        Inner.TryAddMessageHandler(handlerId, (in WsMessageContext<Session, Websocket> context) => HandleMessage(context, handler));

    public bool TryRemoveMessageHandler(string handlerId) => Inner.TryRemoveMessageHandler(handlerId);

    public bool TryAddSessionRemovedHandler(string handlerId, IClient.HandleSessionRemovedBase handler) =>
        Inner.TryAddSessionRemovedHandler(handlerId, (oldSession, oldWebsocket) =>
        {
            _ = UserByWebsocketId.TryRemove(oldWebsocket.WebsocketId, out _);
            handler(oldSession, oldWebsocket);
        });

    public bool TryRemoveSessionRemovedHandler(string handlerId) => Inner.TryRemoveSessionRemovedHandler(handlerId);

    public bool TryAddNewWebsocketHandler(string handlerId, IClient.HandleNewSessionBase handler) => Inner.TryAddNewWebsocketHandler(handlerId, handler);

    public bool TryRemoveNewSessionHandler(string handlerId) => Inner.TryRemoveNewSessionHandler(handlerId);

    public bool TrySendToWebsocket(Guid websocketId, JArray message)
    {
        if (!UserByWebsocketId.TryGetValue(websocketId, out var userId))
        {
            Logging.Log(LogLevel.Warning, $"{LOG_PREFIX} Not sending table {TableModelId} data to websocket {websocketId}, no authorized request was seen on it");
            return false;
        }

        if (!HasPermission(userId))
        {
            _ = UserByWebsocketId.TryRemove(websocketId, out _);
            Logging.Log(LogLevel.Info, $"{LOG_PREFIX} Not sending table {TableModelId} data to websocket {websocketId}, user {userId} has lost the permission");
            return false;
        }

        return Inner.TrySendToWebsocket(websocketId, message);
    }

    public void BroadcastMessage(JArray message) => Inner.BroadcastMessage(message);

    public void SendMessageToUsers(List<Guid> userIds, JArray message) => Inner.SendMessageToUsers(userIds, message);

    public bool TrySendToSession(Session session, JArray message) => Inner.TrySendToSession(session, message);

    public void RegisterProxy(string prefix, int port, string[] allowedHeaders, string[] exposedHeaders) => Inner.RegisterProxy(prefix, port, allowedHeaders, exposedHeaders);

    public void UnregisterProxy(string prefix) => Inner.UnregisterProxy(prefix);

    public List<Session> GetAllBaseSessions() => Inner.GetAllBaseSessions();

    public List<Websocket> GetAllBaseWebsockets() => Inner.GetAllBaseWebsockets();

    private void HandleMessage(WsMessageContext<Session, Websocket> context, IClient.HandleFrontendMessageBase handler)
    {
        if (!ContainsTableRequest(context.IncomingMessage))
        {
            handler(context);
            return;
        }

        var userId = context.session.UserGuid;
        if (!HasPermission(userId))
        {
            _ = UserByWebsocketId.TryRemove(context.websocket.WebsocketId, out _);
            Logging.Log(LogLevel.Warning, $"{LOG_PREFIX} Denied table {TableModelId} request of user {userId}, permission missing");
            return;
        }

        UserByWebsocketId[context.websocket.WebsocketId] = userId;
        handler(context);
    }

    private bool ContainsTableRequest(JArray messages)
    {
        foreach (var token in messages)
        {
            if (token is not JObject message
                || !message.TryGetValue(TableConstants.ID, out var idToken) || idToken.Type != JTokenType.String
                || idToken.ToObject<string>() != TableConstants.AMF_TABLE_ID)
            {
                continue;
            }

            foreach (var function in TableFunctions)
            {
                // the value of the function key is the model id of the table
                if (message.TryGetValue(function, out var modelToken) && modelToken.Type == JTokenType.String && Guid.TryParse(modelToken.ToObject<string>(), out var modelId) && modelId == TableModelId)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
