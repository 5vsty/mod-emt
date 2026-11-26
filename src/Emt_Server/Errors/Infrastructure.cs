using FSDEV.FES.Server.Client;
using FSDEV.MOD.Emt.Emt.Domain;
using Newtonsoft.Json.Linq;
using IClient = FSDEV.FES.Server.Client.IClient;

namespace FSDEV.MOD.Emt.Emt_Server.Errors;

public interface IClock
{
    /// <summary>UTC time as unix epoch in milliseconds.</summary>
    long UtcNowMs { get; }
}

public class SystemClock : IClock
{
    public long UtcNowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public enum ErrorChangeType
{
    Created,
    Updated,
    Deleted,
}

/// <summary>
/// Gets told about every change of an error after it was written successfully (table rows, push messages).
/// An observer must not throw; a failing observer never fails the operation.
/// </summary>
public interface IErrorObserver
{
    void OnErrorChanged(ErrorRecord error, ErrorChangeType changeType);
}

/// <summary>Sends a message to a list of users through FES.</summary>
public interface IPushSender
{
    void SendToUsers(IReadOnlyCollection<Guid> userIds, JArray message);
}

public class FesPushSender : IPushSender
{
    private readonly IClient FesClient;

    public FesPushSender(IClient fesClient)
    {
        FesClient = fesClient;
    }

    public void SendToUsers(IReadOnlyCollection<Guid> userIds, JArray message) => FesClient.SendMessageToUsers(userIds.ToList(), message);
}
