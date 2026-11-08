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
                .SelectMany(day => DailyIntervals(day, EnergyFlowType.ElectricityImport,
                    meter.MeterPoint, meter.MeterSerial)).ToArray();
            await repository.UpsertOctopusRawReadingsAsync(seeded);

            Assert.Equal(missingDay, await repository.GetEarliestIncompleteOctopusDayAsync(
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
            Assert.Contains("Retried recent incomplete-day backfill", result.Message, StringComparison.Ordinal);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task PartialGasDay_RetriesMissingIntervalsWithoutRewritingTheOthers()
    {
        var path = Path.Combine(Path.GetTempPath(), $"octopus-partial-gas-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteDashboardRepository(path, new TestSecretProtector());
            await repository.InitializeAsync();
            var day = new DateOnly(2026, 10, 29);
            var readings = Enumerable.Range(0, 3)
                .Select(offset => day.AddDays(offset - 1))
                .SelectMany(date => DailyIntervals(date, EnergyFlowType.Gas, "GAS-METER", "SERIAL")
                    .Where((_, index) => date != day || index is not (10 or 11 or 12)))
                .ToArray();
            await repository.UpsertOctopusRawReadingsAsync(readings);
            Assert.Equal(day, await repository.GetEarliestIncompleteOctopusDayAsync(
                "GAS-METER", "SERIAL", EnergyFlowType.Gas,
                day.AddDays(-1), day.AddDays(2)));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task BstDay_ExpectsFiftyReadingsNotFortyEight()
    {
        var path = Path.Combine(Path.GetTempPath(), $"octopus-bst-gap-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteDashboardRepository(path, new TestSecretProtector());
            await repository.InitializeAsync();
            var changeDay = new DateOnly(2026, 10, 25);
            var readings = Enumerable.Range(0, 3)
                .Select(offset => changeDay.AddDays(offset - 1))
                .SelectMany(date => DailyIntervals(date, EnergyFlowType.ElectricityImport,
                    "IMPORT-METER", "SERIAL")
                    .Where((_, index) => date != changeDay || index != 12))
                .ToArray();
            await repository.UpsertOctopusRawReadingsAsync(readings);
            Assert.Equal(49, readings.Count(reading =>
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
                    reading.PeriodStart, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime) == changeDay));
            Assert.Equal(changeDay, await repository.GetEarliestIncompleteOctopusDayAsync(
                "IMPORT-METER", "SERIAL", EnergyFlowType.ElectricityImport,
                changeDay.AddDays(-1), changeDay.AddDays(2)));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static IEnumerable<OctopusRawReading> DailyIntervals(
        DateOnly day, EnergyFlowType flow, string point, string serial)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var utcStart = TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), zone);
        var utcEnd = TimeZoneInfo.ConvertTimeToUtc(day.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);
        var count = (int)((utcEnd - utcStart).TotalMinutes / 30);
        for (var index = 0; index < count; index++)
        {
            var start = new DateTimeOffset(utcStart.AddMinutes(index * 30), TimeSpan.Zero);
            yield return new OctopusRawReading(start, start.AddMinutes(30), flow,
                0.5m, null, point, serial, "", $"{point}-{day:yyyyMMdd}-{index:D2}");
        }
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
