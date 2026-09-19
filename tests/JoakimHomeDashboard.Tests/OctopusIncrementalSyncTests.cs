using System.Net;
using System.Text;
using JoakimHomeDashboard.Infrastructure;

namespace JoakimHomeDashboard.Tests;

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
