using System.Net;
using System.Text;
using JoakimHomeDashboard.Domain;
using JoakimHomeDashboard.Infrastructure;
using Microsoft.Data.Sqlite;

namespace JoakimHomeDashboard.Tests;

public sealed class OctopusGasSyncTests
{
    [Fact]
    public async Task GasOnlySync_PersistsNormalizedKwhCostStandingChargeAndTariff()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), $"joakim-gas-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteDashboardRepository(path); await repository.InitializeAsync();
            await repository.SetSettingAsync("octopus.apiKey", "fixture-key", true); await repository.SetSettingAsync("octopus.gasMprn", "1234567890");
            await repository.SetSettingAsync("octopus.gasMeterSerial", "GAS123"); await repository.SetSettingAsync("octopus.gasProductCode", "FLEX-26-01");
            await repository.SetSettingAsync("octopus.gasTariffCode", "G-1R-FLEX-26-01-A"); await repository.SetSettingAsync("octopus.gasReadingsInCubicMetres", "False");
            using var client = new HttpClient(new OctopusFixtureHandler()); var connector = new OctopusEnergyDataSource(repository, repository, repository, client);
            var result = await connector.SyncAsync(CancellationToken.None); Assert.True(result.Succeeded, result.Message); Assert.Equal(1, result.RecordsImported);
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = "SELECT quantity_kwh,cost_gbp,standing_charge_gbp,tariff_code,source,flow_type FROM energy_records";
            await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync()); Assert.Equal(1.5m, reader.GetDecimal(0)); Assert.Equal(0.72m, reader.GetDecimal(1));
            Assert.Equal(0.30m, reader.GetDecimal(2)); Assert.Equal("G-1R-FLEX-26-01-A", reader.GetString(3)); Assert.Equal("Octopus", reader.GetString(4)); Assert.Equal((int)EnergyFlowType.Gas, reader.GetInt32(5));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task DiscoveredMultipleGasMeters_AggregateWithoutDuplicatingStandingCharge()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), $"joakim-multigas-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteDashboardRepository(path); await repository.InitializeAsync(); await repository.SetSettingAsync("octopus.apiKey", "fixture-key", true);
            var meters = new[]
            {
                new JoakimHomeDashboard.Application.OctopusMeterPoint("A-TEST", 1, "gas", false, "1234567890", "GAS1", "G-1R-FLEX-26-01-A", "FLEX-26-01", null, null),
                new JoakimHomeDashboard.Application.OctopusMeterPoint("A-TEST", 1, "gas", false, "1234567890", "GAS2", "G-1R-FLEX-26-01-A", "FLEX-26-01", null, null)
            };
            await repository.ReplaceOctopusConfigurationAsync(new("A-TEST", 1, meters, []));
            using var client = new HttpClient(new OctopusFixtureHandler()); var connector = new OctopusEnergyDataSource(repository, repository, repository, client); var result = await connector.SyncAsync(CancellationToken.None);
            Assert.True(result.Succeeded, result.Message);
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = """
                SELECT COALESCE(SUM(quantity_kwh),0),COALESCE(SUM(cost_gbp),0)
                FROM energy_records
                WHERE flow_type=@flow AND period_start>=@start AND period_start<@end
                """;
            command.Parameters.AddWithValue("@flow", (int)EnergyFlowType.Gas); command.Parameters.AddWithValue("@start", "2026-06-18T00:00:00.0000000Z"); command.Parameters.AddWithValue("@end", "2026-06-19T00:00:00.0000000Z");
            await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync()); Assert.Equal(3m, reader.GetDecimal(0)); Assert.Equal(1.14m, reader.GetDecimal(1));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private sealed class OctopusFixtureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme); var path = request.RequestUri?.AbsolutePath ?? ""; string json;
            if (path == "/v1/products/FLEX-26-01/") return Task.FromResult(OctopusMetadataFixture.Response("FLEX-26-01", "G-1R-FLEX-26-01-A", "gas_tariffs", "Flexible Octopus"));
            if (path.Contains("/consumption/", StringComparison.Ordinal))
                json = """{"next":null,"results":[{"consumption":1.5,"interval_start":"2026-06-18T00:00:00Z","interval_end":"2026-06-18T00:30:00Z"}]}""";
            else if (path.Contains("/standing-charges/", StringComparison.Ordinal))
                json = """{"results":[{"value_inc_vat":30,"valid_from":"2026-01-01T00:00:00Z","valid_to":null}]}""";
            else if (path.Contains("/standard-unit-rates/", StringComparison.Ordinal))
                json = """{"results":[{"value_inc_vat":28,"valid_from":"2026-01-01T00:00:00Z","valid_to":null}]}""";
            else return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
