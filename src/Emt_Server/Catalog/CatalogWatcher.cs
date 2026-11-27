using FSDEV.Common.Logger;
using FSDEV.ILP.Intralogistikplattform.SystemState.Client;
using Constants = FSDEV.MOD.Emt.Emt.Constants;

namespace FSDEV.MOD.Emt.Emt_Server.Catalog;

/// <summary>
/// Keeps the catalog in memory equal to the states on ILP. EMT reads the catalog once when it starts; codes that are added later by somebody else
/// (the configuration manager fills missing states from its files, an administrator) would stay invisible until the next restart.
/// An ILP subscription is called on the main thread of the ILP client, which must never wait for ILP (a change request from there never returns),
/// so the callback only schedules the reload on the thread pool; changes that come close together are read once.
/// </summary>
public sealed class CatalogWatcher
{
    private const string LOG_PREFIX = $"[{nameof(CatalogWatcher)}]";
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);

    private readonly CatalogStore Catalog;
    private readonly CancellationToken StoppingToken;
    private int Pending;

    public CatalogWatcher(CatalogStore catalog, CancellationToken stoppingToken)
    {
        Catalog = catalog;
        StoppingToken = stoppingToken;
    }

    public bool TryStart(IClientController ilp)
    {
        var result = ilp.RegisterSubscription(Constants.Ilp.Paths.ERROR_CODES, _ => Schedule(), true, out _);
        if (result != FSDEV.ILP.Intralogistikplattform.SystemState.ExecutionResponse.SUCCEEDED)
        {
            Logging.Log(LogLevel.Warning, $"{LOG_PREFIX} Could not watch the catalog on ILP ({result}), changes of others are only seen after a restart");
            return false;
        }

        return true;
    }

    /// <summary>Called from the subscription: returns at once.</summary>
    public void Schedule()
    {
        if (Interlocked.Exchange(ref Pending, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Debounce, StoppingToken);
                _ = Interlocked.Exchange(ref Pending, 0);
                Reload();
            }
            catch (OperationCanceledException)
            {
                // EMT stops
            }
        }, CancellationToken.None);
    }

    /// <summary>Reads the catalog now.</summary>
    public void Reload()
    {
        try
        {
            if (Catalog.TryReload(out var changed) && changed)
            {
                Logging.Log(LogLevel.Info, $"{LOG_PREFIX} The catalog on ILP changed, it was read again");
            }
        }
        catch (Exception exception)
        {
            Logging.Log(LogLevel.Error, exception, $"{LOG_PREFIX} The catalog could not be read again");
        }
    }
}
