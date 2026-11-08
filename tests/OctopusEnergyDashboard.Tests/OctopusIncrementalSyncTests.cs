using System.Net;
using System.Text;
using OctopusEnergyDashboard.Application;
using OctopusEnergyDashboard.Domain;
using OctopusEnergyDashboard.Infrastructure;

namespace OctopusEnergyDashboard.Tests;

public sealed class OctopusIncrementalSyncTests
{
    [Fact]
    public async Task SecondSync_UsesStoredCursorAndDoesNotCountOverlapAsNew()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), $"joakim-incremental-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteDashboardRepository(path, new TestSecretProtector()); await repository.InitializeAsync(); await repository.SetSettingAsync("octopus.apiKey", "fixture-key", true);
            await repository.SetSettingAsync("octopus.mpan", "MPAN-DEMO-IMPORT"); await repository.SetSettingAsync("octopus.meterSerial", "IMP1");
            var handler = new CursorHandler(); using var client = new HttpClient(handler); var connector = new OctopusEnergyDataSource(repository, repository, repository, client);
            var first = await connector.SyncAsync(CancellationToken.None); var second = await connector.SyncAsync(CancellationToken.None);
            Assert.Equal(1, first.RecordsImported); Assert.Equal(0, second.RecordsImported); Assert.Equal(2, handler.PeriodFrom.Count);
            Assert.StartsWith("2010-01-01", handler.PeriodFrom[0], StringComparison.Ordinal); Assert.StartsWith("2026-06-16", handler.PeriodFrom[1], StringComparison.Ordinal);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task OlderRecentGap_IsIncludedInIncrementalSyncWithoutOverwritingHistory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"octopus-gap-recovery-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteDashboardRepository(path, new TestSecretProtector());
            await repository.InitializeAsync();
            await repository.SetSettingAsync("octopus.apiKey", "fixture-key", true);
            var meter = new OctopusMeterPoint("ACCOUNT", 1, "electricity", false,
                "IMPORT-METER", "SERIAL", "", "", null, null);
            await repository.ReplaceOctopusConfigurationAsync(
                new OctopusDiscoveryResult("ACCOUNT", 1, [meter], []));
            var window = OctopusReportingCompleteness.RecentExpectedWindow(DateOnly.FromDateTime(DateTime.Now));
            var missingDay = window.From.AddDays(7);
            var seeded = Enumerable.Range(0, window.ExpectedDays)
                .Select(offset => window.From.AddDays(offset))
                .Where(day => day != missingDay)
                .Select(day =>
                {
                    var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                    return new OctopusRawReading(start, start.AddMinutes(30), EnergyFlowType.ElectricityImport,
                        0.5m, null, meter.MeterPoint, meter.MeterSerial, "", $"seed-{day:yyyyMMdd}");
                }).ToArray();
            await repository.UpsertOctopusRawReadingsAsync(seeded);

            Assert.Equal(missingDay, await repository.GetEarliestMissingOctopusDayAsync(
                meter.MeterPoint, meter.MeterSerial, EnergyFlowType.ElectricityImport,
                window.From, window.ToExclusive));
            var latestBefore = await repository.GetLatestOctopusReadingAsync(
                meter.MeterPoint, meter.MeterSerial, EnergyFlowType.ElectricityImport);

            var handler = new EmptyConsumptionHandler();
            using var client = new HttpClient(handler);
            var connector = new OctopusEnergyDataSource(repository, repository, repository, client);
            var result = await connector.SyncAsync(CancellationToken.None);

            Assert.True(result.Succeeded, result.Message);
            Assert.StartsWith(missingDay.AddDays(-1).ToString("yyyy-MM-dd"),
                Assert.Single(handler.PeriodFrom), StringComparison.Ordinal);
            Assert.Equal(latestBefore, await repository.GetLatestOctopusReadingAsync(
                meter.MeterPoint, meter.MeterSerial, EnergyFlowType.ElectricityImport));
            Assert.Contains("Retried recent missing-day backfill", result.Message, StringComparison.Ordinal);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private sealed class EmptyConsumptionHandler : HttpMessageHandler
    {
        public List<string> PeriodFrom { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/consumption/", StringComparison.Ordinal) != true)
                throw new InvalidOperationException($"Unexpected API endpoint: {request.RequestUri?.AbsolutePath}");
            var query = request.RequestUri.Query;
            var value = query.TrimStart('?').Split('&')
                .Select(part => part.Split('=', 2))
                .First(part => part[0] == "period_from");
            PeriodFrom.Add(Uri.UnescapeDataString(value[1]));
            const string json = """{"next":null,"results":[]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class CursorHandler : HttpMessageHandler
    {
        public List<string> PeriodFrom { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri?.Query ?? ""; var value = query.Split('&').Select(x => x.TrimStart('?').Split('=',2)).FirstOrDefault(x => x[0] == "period_from"); PeriodFrom.Add(value is null ? "" : Uri.UnescapeDataString(value[1]));
            const string json = """{"next":null,"results":[{"consumption":0.5,"interval_start":"2026-06-18T00:00:00Z","interval_end":"2026-06-18T00:30:00Z"}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
