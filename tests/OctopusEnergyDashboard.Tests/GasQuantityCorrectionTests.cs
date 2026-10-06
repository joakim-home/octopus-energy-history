using OctopusEnergyDashboard.Domain;
using OctopusEnergyDashboard.Infrastructure;
using Microsoft.Data.Sqlite;

namespace OctopusEnergyDashboard.Tests;

public sealed class GasQuantityCorrectionTests
{
    [Fact]
    public void StandardGasConversionReconcilesJanuaryStatement()
    {
        Assert.Equal(11.220633333333333333333333333m, GasQuantityConversion.CubicMetresToKwhFactor);
        var kwh = 180.463m * GasQuantityConversion.CubicMetresToKwhFactor;
        var cost = kwh * 6.194475m / 100m;
        Assert.Equal(2024.909153m, decimal.Round(kwh, 6));
        Assert.Equal(125.43m, decimal.Round(cost, 2));
    }

    [Fact]
    public async Task LegacyRawGasCanBeCorrectedWithoutMutatingRawEvidence()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gas-correction-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteDashboardRepository(path, new TestSecretProtector());
            await repository.InitializeAsync();
            var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
            await repository.UpsertOctopusRawReadingsAsync([
                new(start, start.AddMinutes(30), EnergyFlowType.Gas, 180.463m, 11.178748m,
                    "GAS-DEMO", "SERIAL-DEMO", "G-1R-DEMO", "legacy-gas")
            ]);
            await repository.SetSettingAsync("octopus.gasV13CutoverId", "1");
            await repository.SetSettingAsync("octopus.gasV13LegacyCubic", "false");
            await repository.PrepareGasQuantityCorrectionsAsync(true);
            await repository.RebuildOctopusRollupsAsync();

            var dashboard = await repository.GetEnergyDashboardAsync();
            var january = Assert.Single(dashboard.Monthly, x => x.Period == new DateOnly(2026, 1, 1));
            Assert.Equal(2024.909153m, decimal.Round(january.GasKwh, 6));
            Assert.Equal(125.43m, decimal.Round(january.GasCost, 2));

            await using var connection = new SqliteConnection($"Data Source={path}"); await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT quantity_kwh,cost_gbp FROM octopus_raw_readings WHERE external_id='legacy-gas'";
            await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
            Assert.Equal(180.463m, reader.GetDecimal(0)); Assert.Equal(11.178748m, reader.GetDecimal(1));
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public void SupplierMeasurementParserUsesDailyKwhAndInclusivePenceCost()
    {
        const string json = """
        {"data":{"account":{"properties":[{"measurements":{"pageInfo":{"hasNextPage":false,"endCursor":null},"edges":[{"node":{
          "readAt":"2026-01-15T00:00:00+00:00","source":"gas_day","value":"2024.909153","unit":"kWh",
          "metaData":{"statistics":[{"type":"CONSUMPTION_COST","value":"2024.909153","costInclTax":{"estimatedAmount":"12543","costCurrency":"GBP"}}]}
        }}]}}]}}}
        """;
        var page = GasMeasurementImporter.Parse(json);
        var value = Assert.Single(page.Measurements);
        Assert.Equal(2024.909153m, value.Kwh);
        Assert.Equal(125.43m, value.CostGbp);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task SupplierDailyMeasurementOverridesQuantityAndCostWithoutChangingRawRows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gas-supplier-measurement-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteDashboardRepository(path, new TestSecretProtector()); await repository.InitializeAsync();
            var start = DateTimeOffset.Parse("2026-01-15T00:00:00Z");
            await repository.UpsertOctopusRawReadingsAsync([
                new(start,start.AddMinutes(30),EnergyFlowType.Gas,100m,6m,"GAS-DEMO","SERIAL-DEMO","G-1R-DEMO","gas-1"),
                new(start.AddMinutes(30),start.AddHours(1),EnergyFlowType.Gas,80m,4.8m,"GAS-DEMO","SERIAL-DEMO","G-1R-DEMO","gas-2")
            ]);
            var store = new SupplierAllocationStore(path);
            var corrected = await store.SaveGasDailyMeasurementAsync("GAS-DEMO",new DateOnly(2026,1,15),2020m,125.43m,"gas_day","{}");
            Assert.Equal(2, corrected); await repository.RebuildOctopusRollupsAsync();
            var dashboard = await repository.GetEnergyDashboardAsync(); var january = Assert.Single(dashboard.Monthly,x=>x.Period==new DateOnly(2026,1,1));
            Assert.Equal(2020m,decimal.Round(january.GasKwh,6)); Assert.Equal(125.43m,decimal.Round(january.GasCost,2)); Assert.True(january.GasCostExact);
            await using var connection = new SqliteConnection($"Data Source={path}"); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText="SELECT SUM(quantity_kwh),SUM(cost_gbp) FROM octopus_raw_readings WHERE flow_type=2";
            await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync()); Assert.Equal(180m,reader.GetDecimal(0)); Assert.Equal(10.8m,reader.GetDecimal(1));
        }
        finally { SqliteConnection.ClearAllPools(); if(File.Exists(path)) File.Delete(path); }
    }

}
