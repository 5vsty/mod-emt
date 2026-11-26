using FSDEV.Common.Logger;
using FSDEV.FES.Server.Client;
using FSDEV.FES.Server.Client.Sessions;

namespace FSDEV.MOD.Emt.Emt_Server;

public class EmtWebsocket : Websocket
{
    public EmtWebsocket()
    {
    }
}

public class EmtSession : Session
{
    public static EmtSession CreateSession(Session session) => new(session);

    private EmtSession(Session session) : base(session)
    {
    }
}

public class LoggerWrapper : ILogger
{
    public void SendMessage(LogLevel logLevel, string message) =>
        Logging.Log(logLevel, message);
}
