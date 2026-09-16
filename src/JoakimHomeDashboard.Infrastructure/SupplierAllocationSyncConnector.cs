using JoakimHomeDashboard.Application;

namespace JoakimHomeDashboard.Infrastructure;

// Registered after meter ingestion so scheduled and manual sync share the coordinator's gate.
public sealed class SupplierAllocationSyncConnector(SupplierAllocationImporter importer, IOctopusReadingStore readings, SupplierAllocationStore? store = null) : IProviderConnector
{
    public string Name => "Octopus supplier allocations";

    public Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new ConnectionTestResult(false, "Use the Octopus account connection test for credentials; allocation coverage is verified during sync."));

    public async Task<SyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        if (store is not null) await store.CaptureHealthAsync("running", cancellationToken);
        try
        {
            var result = await importer.SyncIncrementalAsync(cancellationToken);
            if (store is not null) await store.CaptureHealthAsync("complete", cancellationToken);
            var health = store is null ? null : await store.GetHealthAsync(cancellationToken);
            return new(Name, result.Intervals == result.Reconciled && (health is null || health.RetryState == "complete"), result.Reconciled,
                $"Supplier allocation sync: {result.Reconciled}/{result.Intervals} intervals reconciled. " + string.Join(" ", result.Warnings));
        }
        catch { if (store is not null) await store.CaptureHealthAsync("failed", CancellationToken.None); throw; }
        finally { await readings.RebuildOctopusRollupsAsync(cancellationToken); }
    }
}
