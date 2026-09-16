using System.Net;
using System.Text;
using System.Text.Json;
using JoakimHomeDashboard.Application;
using JoakimHomeDashboard.Domain;
using JoakimHomeDashboard.Infrastructure;

namespace JoakimHomeDashboard.Tests;

public sealed class SupplierAllocationSyncTests
{
    [Theory]
    [InlineData("2026-09-01T00:00:00+01:00")]
    [InlineData("2026-10-25T01:00:00+01:00")]
    [InlineData("2026-10-25T01:00:00+00:00")]
    public async Task SharedSyncCycleRetriesPendingAllocationAfterMeterStep_AndUsesExactUtcHalfOpenQuery(string timestamp)
    {
        var path = Path.Combine(Path.GetTempPath(), $"allocation-cycle-{Guid.NewGuid():N}.db");
        try
        {
            var start = DateTimeOffset.Parse(timestamp);
            var repository = new SqliteDashboardRepository(path);
            await repository.InitializeAsync();
            await repository.SetSettingAsync("octopus.apiKey", "fixture");
            var tariff = new OctopusTariffPeriod("A", 1, "electricity", false, "IMP", "FOUR", "FOUR", start, start.AddMinutes(30));
            await repository.ReplaceOctopusConfigurationAsync(new("A", 1, [], [], [tariff]));
            var events = new List<string>();
            var handler = new Handler(start, events);
            using var http = new HttpClient(handler);
            var store = new SupplierAllocationStore(path);
            var importer = new SupplierAllocationImporter(repository, repository, store, http);
            var connector = new SupplierAllocationSyncConnector(importer, repository);
            var coordinator = new SyncCoordinator([new MeterStep(repository, start, events), connector], repository);

            var partial = await coordinator.SyncAllAsync();
            Assert.Equal(new[] { "meter", "allocation" }, events);
            Assert.Equal(0, partial.Last().RecordsImported);
            Assert.False(partial.Last().Succeeded);
            Assert.False(Assert.Single(await store.AuditAsync(true)).Complete);
            Assert.Null(Assert.Single(await repository.GetOctopusIntervalsAsync(DateOnly.FromDateTime(start.Date))).CostGbp);
            Assert.Contains((await repository.GetEnergyDashboardAsync()).Warnings, w => w.Contains("supplier allocation pending", StringComparison.OrdinalIgnoreCase));

            handler.Complete = true;
            events.Clear();
            var repaired = await coordinator.SyncAllAsync();
            Assert.Equal(new[] { "meter", "allocation" }, events);
            Assert.True(repaired.Last().Succeeded);
            Assert.Equal(1, repaired.Last().RecordsImported);
            Assert.True(Assert.Single(await store.AuditAsync(true)).Complete);
            Assert.Equal(0.21m, Assert.Single(await repository.GetOctopusIntervalsAsync(DateOnly.FromDateTime(start.Date))).CostGbp);
            Assert.True(Assert.Single((await repository.GetEnergyDashboardAsync()).Monthly).ImportCostExact);
            Assert.DoesNotContain((await repository.GetEnergyDashboardAsync()).Warnings, w => w.Contains("supplier allocation pending", StringComparison.OrdinalIgnoreCase));
            Assert.All(handler.Windows, w => { Assert.Equal(start.ToUniversalTime(), w.Start); Assert.Equal(start.AddMinutes(30).ToUniversalTime(), w.End); });
            Assert.All(handler.AuthHeaders, auth => Assert.Equal("JWT fixture-token", auth));
            await coordinator.SyncAllAsync();
            Assert.True(Assert.Single(await store.AuditAsync(true)).Complete);
            var statuses = await coordinator.GetStatusesAsync();
            Assert.True(statuses.Single(s => s.Source == connector.Name).Succeeded);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task NetworkFailurePersistsPendingCoverageAndRestartRetriesWithoutChangingConsumption()
    {
        var path = Path.Combine(Path.GetTempPath(), $"allocation-restart-{Guid.NewGuid():N}.db");
        try
        {
            var start = DateTimeOffset.Parse("2026-09-01T00:00:00+01:00");
            var repository = new SqliteDashboardRepository(path);
            await repository.InitializeAsync();
            await repository.SetSettingAsync("octopus.apiKey", "fixture");
            var tariff = new OctopusTariffPeriod("A", 1, "electricity", false, "IMP", "FOUR", "FOUR", start, start.AddMinutes(30));
            await repository.ReplaceOctopusConfigurationAsync(new("A", 1, [], [], [tariff]));
            var events = new List<string>();
            await new MeterStep(repository, start, events).SyncAsync(default);
            var handler = new Handler(start, events) { ThrowOnAllocation = true };
            using var http = new HttpClient(handler);
            var store = new SupplierAllocationStore(path);
            var connector = new SupplierAllocationSyncConnector(new(repository, repository, store, http), repository, store);
            var failed = await connector.SyncAsync(default);
            Assert.False(failed.Succeeded);
            var pending = await store.GetHealthAsync();
            Assert.Equal(1m, pending.UnclassifiedKwh);
            Assert.Null(pending.LastFullyReconciledAt);
            Assert.NotNull(pending.LastAttemptAt);
            Assert.Null(Assert.Single(await repository.GetOctopusIntervalsAsync(DateOnly.FromDateTime(start.Date))).CostGbp);

            // Recreate repository, store and connector to prove retry state survives a process lifetime.
            var restartedRepository = new SqliteDashboardRepository(path);
            var restartedStore = new SupplierAllocationStore(path);
            Assert.Equal(pending.LastAttemptAt, (await restartedStore.GetHealthAsync()).LastAttemptAt);
            handler.ThrowOnAllocation = false;
            handler.Complete = true;
            var restarted = new SupplierAllocationSyncConnector(new(restartedRepository, restartedRepository, restartedStore, http), restartedRepository, restartedStore);
            Assert.True((await restarted.SyncAsync(default)).Succeeded);
            var healthy = await restartedStore.GetHealthAsync();
            Assert.Equal(1, healthy.ExpectedIntervals);
            Assert.Equal(1, healthy.ReconciledIntervals);
            Assert.Equal(0m, healthy.UnclassifiedKwh);
            Assert.NotNull(healthy.LastFullyReconciledAt);
            Assert.Equal("complete", healthy.RetryState);
            Assert.Equal(1m, Assert.Single(await restartedRepository.GetOctopusIntervalsAsync(DateOnly.FromDateTime(start.Date))).QuantityKwh);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private sealed class MeterStep(SqliteDashboardRepository repository, DateTimeOffset start, List<string> events) : IProviderConnector
    {
        public string Name => "Octopus";
        public Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ConnectionTestResult(true, "fixture"));
        public async Task<SyncResult> SyncAsync(CancellationToken cancellationToken)
        {
            events.Add("meter");
            await repository.InsertMissingOctopusRawReadingsAsync([new(start, start.AddMinutes(30), EnergyFlowType.ElectricityImport, 1, 99, "IMP", "M1", "FOUR", "raw", ImportRateBand.Peak, 99)], cancellationToken);
            return new(Name, true, 0, "fixture");
        }
    }

    private sealed class Handler(DateTimeOffset start, List<string> events) : HttpMessageHandler
    {
        public bool Complete;
        public bool ThrowOnAllocation;
        public List<(DateTimeOffset Start, DateTimeOffset End)> Windows { get; } = [];
        public List<string?> AuthHeaders { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/products/FOUR/") return OctopusMetadataFixture.Response("FOUR", "FOUR", "four_rate_ev_electricity_tariffs");
            if (path == "/v1/accounts/A/") return Ok("""{"properties":[{"id":1,"electricity_meter_points":[{"mpan":"IMP","is_export":false,"meters":[{"serial_number":"M1"}],"agreements":[]}]}]}""");
            if (path.Contains("unit-rates")) return Ok(JsonSerializer.Serialize(new { next = (string?)null, results = new[] { new { value_exc_vat = 20, value_inc_vat = 21, valid_from = start, valid_to = start.AddMinutes(30) } } }));
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var query = body.RootElement.GetProperty("query").GetString()!;
            if (query.Contains("obtainKrakenToken")) return Ok("""{"data":{"obtainKrakenToken":{"token":"fixture-token"}}}""");
            if (query == SupplierAllocationImporter.AllocationQuery)
            {
                events.Add("allocation");
                if (ThrowOnAllocation) throw new HttpRequestException("fixture supplier unavailable");
                var period = body.RootElement.GetProperty("variables").GetProperty("periods")[0];
                Windows.Add((period.GetProperty("start").GetDateTimeOffset(), period.GetProperty("end").GetDateTimeOffset()));
                AuthHeaders.Add(request.Headers.Authorization?.ToString());
                if (!Complete) return Ok("""{"data":{"gbrCostOfUsage":{"periods":[]}}}""");
                return Ok(JsonSerializer.Serialize(new { data = new { gbrCostOfUsage = new { periods = new[] { new { totalConsumption = 1, totalCost = 20, consumptionUnit = "kilowatt_hour", currency = "GBP_PENCE", intervals = new[] { new { period = new { start, end = start.AddMinutes(30) }, consumption = 1, consumptionUnit = "kilowatt_hour", currency = "GBP_PENCE", rateApplied = 20, cost = 20, bandSubcategory = "ECO7_DAY" } } } } } } }));
            }
            return Ok("{\"data\":{}}");
        }
        private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
