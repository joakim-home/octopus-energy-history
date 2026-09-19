using JoakimHomeDashboard.Application;
using JoakimHomeDashboard.Infrastructure;

namespace JoakimHomeDashboard.Tests;

public sealed class SyncCoordinatorTests
{
    [Fact]
    public async Task Coordinator_IsolatesProviderFailureAndPersistsBothResults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"joakim-sync-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteDashboardRepository(path, new TestSecretProtector()); await repository.InitializeAsync();
            var coordinator = new SyncCoordinator([new FakeConnector("Working", false), new FakeConnector("Broken", true)], repository);
            var results = await coordinator.SyncAllAsync(); var statuses = await coordinator.GetStatusesAsync();
            Assert.Equal(2, results.Count); Assert.Contains(results, x => x.Source == "Working" && x.Succeeded); Assert.Contains(results, x => x.Source == "Broken" && !x.Succeeded);
            Assert.Equal(2, statuses.Count); Assert.Contains(statuses, x => x.Source == "Working" && x.RecordsImported == 3);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private sealed class FakeConnector(string name, bool throws) : IProviderConnector
    {
        public string Name => name;
        public Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ConnectionTestResult(!throws, "fixture"));
        public Task<SyncResult> SyncAsync(CancellationToken cancellationToken)
            => throws ? throw new InvalidOperationException("fixture failure") : Task.FromResult(new SyncResult(name, true, 3, "fixture success"));
    }
}
