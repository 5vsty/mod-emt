using FSDEV.Common.Logger;
using FSDEV.MOD.IdManager.IdManager_Client;
using System.Diagnostics.CodeAnalysis;

namespace FSDEV.MOD.Emt.Emt_Server.Catalog;

/// <summary>Where the codes of new error codes come from: the user does not type them.</summary>
public interface IErrorCodeGenerator
{
    /// <summary>Makes sure the series exists; must have succeeded before the first code is asked for.</summary>
    bool TryRegister();

    /// <summary>The next code of the series. False when the IdManager does not answer in time.</summary>
    bool TryGenerate([NotNullWhen(true)] out string? code);
}

/// <summary>
/// Codes of the error code catalog drawn from a series of their own in the IdManager (prefix <see cref="PREFIX"/>, e.g. EC10000001), so a code is
/// unique over the whole system without EMT counting itself. The IdManager client uses the ILP client singleton of the process.
/// </summary>
public class IdManagerErrorCodeGenerator : IErrorCodeGenerator
{
    public const string PREFIX = "EC";

    private const string LOG_PREFIX = $"[{nameof(IdManagerErrorCodeGenerator)}]";
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);

    private readonly CancellationToken StoppingToken;
    private readonly string Prefix;

    /// <param name="prefix">The series in the IdManager; the error codes of the catalog have <see cref="PREFIX"/>, the numbers of the errors a series of their own.</param>
    public IdManagerErrorCodeGenerator(CancellationToken stoppingToken, string prefix = PREFIX)
    {
        StoppingToken = stoppingToken;
        Prefix = prefix;
    }

    public bool TryRegister()
    {
        if (IdManager_Client.TryRegisterType(Prefix, StoppingToken))
        {
            Logging.Log(LogLevel.Debug, $"{LOG_PREFIX} The series '{Prefix}' is registered");
            return true;
        }

        Logging.Log(LogLevel.Error, $"{LOG_PREFIX} The series '{Prefix}' could not be registered; new error codes cannot be created until the IdManager knows it");
        return false;
    }

    public bool TryGenerate([NotNullWhen(true)] out string? code)
    {
        // registering is idempotent and cheap; doing it again here also covers an IdManager that was not there at the start of EMT
        if (!TryRegister())
        {
            code = null;
            return false;
        }

        if (IdManager_Client.TryGetNextNumber(Prefix, StoppingToken, QueryTimeout, out code))
        {
            return true;
        }

        Logging.Log(LogLevel.Error, $"{LOG_PREFIX} No code from the IdManager within {QueryTimeout.TotalSeconds}s (cancelled={StoppingToken.IsCancellationRequested})");
        return false;
    }
}
