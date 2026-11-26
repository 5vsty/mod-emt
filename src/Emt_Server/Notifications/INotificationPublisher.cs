using FSDEV.MOD.Emt.Emt.Domain;
using System.Diagnostics.CodeAnalysis;

namespace FSDEV.MOD.Emt.Emt_Server.Notifications;

/// <summary>The channel to the Notifications module (FR-24, FR-25). Implementations never throw; a failure is a return value.</summary>
public interface INotificationPublisher
{
    /// <summary>Creates the notification of a new error and returns its id.</summary>
    bool TryPublish(ErrorRecord error, [NotNullWhen(true)] out string? notificationId);

    /// <summary>Moves the notification from the active list to the history.</summary>
    bool TryArchive(string notificationId);
}

/// <summary>What the observer needs to know about an error and where it keeps the notification of it (the error service).</summary>
public interface INotificationStateStore
{
    bool TryGet(Guid errorId, [NotNullWhen(true)] out ErrorRecord? error);

    /// <summary>Writes the notification id and state of the error without counting it as a change of the error.</summary>
    bool TrySetNotification(Guid errorId, string notificationId, string state);
}