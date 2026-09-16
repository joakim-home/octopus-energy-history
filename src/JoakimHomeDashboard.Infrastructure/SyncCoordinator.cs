using JoakimHomeDashboard.Application;
using JoakimHomeDashboard.Domain;
using Microsoft.Extensions.Hosting;

namespace JoakimHomeDashboard.Infrastructure;

public sealed class SyncCoordinator(IEnumerable<IProviderConnector> connectors, IDashboardRepository repository) : ISyncCoordinator
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<IReadOnlyList<SyncResult>> SyncAllAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken); var results = new List<SyncResult>();
        try
        {
            foreach (var connector in connectors)
            {
                var started = DateTimeOffset.UtcNow; SyncResult result;
                try { result = await connector.SyncAsync(cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await repository.RecordSyncResultAsync(new(connector.Name, false, 0, "Sync interrupted; committed readings are preserved and unresolved allocation is retried on the next sync."), started, CancellationToken.None);
                    throw;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { result = new(connector.Name, false, 0, ex.Message); }
                await repository.RecordSyncResultAsync(result, started, cancellationToken); results.Add(result);
            }
            return results;
        }
        finally { gate.Release(); }
    }
    public Task<IReadOnlyList<ProviderSyncStatus>> GetStatusesAsync(CancellationToken cancellationToken = default) => repository.GetSyncStatusesAsync(cancellationToken);
}

public sealed class SyncBackgroundService(ISyncCoordinator coordinator) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await coordinator.SyncAllAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        while (await timer.WaitForNextTickAsync(stoppingToken)) await coordinator.SyncAllAsync(stoppingToken);
    }
}
