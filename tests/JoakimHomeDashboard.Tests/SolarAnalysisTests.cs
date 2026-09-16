using JoakimHomeDashboard.Application;
using JoakimHomeDashboard.Domain;
using JoakimHomeDashboard.Infrastructure;
using Microsoft.Data.Sqlite;

namespace JoakimHomeDashboard.Tests;

public sealed class SolarAnalysisTests
{
    [Fact]
    public void Detector_IgnoresIsolatedExportSpikes()
    {
        var observations = Enumerable.Range(0, 8).Select(index => new DailyExportObservation(new DateOnly(2025, 1, 1).AddDays(index * 10), 4m));
        var result = SolarStartDetector.Detect(observations);
        Assert.Null(result.StartDate);
        Assert.Equal(SolarDetectionConfidence.None, result.Confidence);
    }

    [Fact]
    public void Detector_FindsFirstFiveOfSevenSustainedExportPeriod()
    {
        var observations = new[] { 0, 1, 2, 4, 6 }.Select(day => new DailyExportObservation(new DateOnly(2025, 8, 1).AddDays(day), 1m));
        var result = SolarStartDetector.Detect(observations);
        Assert.Equal(new DateOnly(2025, 8, 1), result.StartDate);
        Assert.Equal(SolarDetectionConfidence.Medium, result.Confidence);
        Assert.Contains("5 of 7", result.Method, StringComparison.Ordinal);
    }

    [Fact]
    public void Detector_AssignsHighConfidenceWhenRollingMonthAlsoSupportsStart()
    {
        var start = new DateOnly(2025, 8, 1);
        var observations = Enumerable.Range(0, 30).Where(day => day % 3 != 0).Select(day => new DailyExportObservation(start.AddDays(day), 1m));
        var result = SolarStartDetector.Detect(observations);
        Assert.Equal(start.AddDays(1), result.StartDate);
        Assert.Equal(SolarDetectionConfidence.High, result.Confidence);
        Assert.Contains("20 of 30", result.Method, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualOverride_PersistsAndAlwaysTakesPrecedence()
    {
        var path = Path.Combine(Path.GetTempPath(), $"joakim-solar-config-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteDashboardRepository(path); await repository.InitializeAsync();
            var start = DateTimeOffset.Parse("2025-08-01T00:00:00Z");
            await repository.UpsertOctopusRawReadingsAsync(Enumerable.Range(0, 5).Select(day => new OctopusRawReading(start.AddDays(day), start.AddDays(day).AddMinutes(30), EnergyFlowType.ElectricityExport, 1m, -0.15m, "EXP", "E1", "EXPORT", $"export-{day}")).ToArray());
            await repository.RebuildOctopusRollupsAsync();
            Assert.Equal(new DateOnly(2025, 8, 1), (await repository.GetSolarAnalysisConfigurationAsync()).DetectedStartDate);

            await repository.SetSolarManualOverrideAsync(new(2025, 7, 15));
            var manual = await repository.GetSolarAnalysisConfigurationAsync();
            Assert.Equal(new DateOnly(2025, 7, 15), manual.EffectiveStartDate);
            Assert.True(manual.IsManualOverride);

            await repository.SetSolarManualOverrideAsync(null);
            var automatic = await repository.GetSolarAnalysisConfigurationAsync();
            Assert.Equal(new DateOnly(2025, 8, 1), automatic.EffectiveStartDate);
            Assert.False(automatic.IsManualOverride);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void BaselineAnalysis_ProvidesBeforeAfterDifferenceSameMonthAndCombinedCost()
    {
        var monthly = Insights(
            Point(new(2025, 5, 1), 100m, 0m, 30m, 25m, 0m, 3m),
            Point(new(2025, 6, 1), 120m, 0m, 20m, 30m, 0m, 2m),
            Point(new(2025, 9, 1), 70m, 40m, 25m, 18m, 6m, 2.5m),
            Point(new(2026, 5, 1), 60m, 50m, 20m, 16m, 7.5m, 2m),
            Point(new(2026, 6, 1), 80m, 45m, 15m, 20m, 6.75m, 1.5m));
        var configuration = new SolarAnalysisConfiguration(new(2025, 8, 15), SolarDetectionConfidence.High, "test", null, DateTimeOffset.UtcNow);
        var analysis = EnergyComparisonEngine.AnalyzeSolar(monthly, configuration, [], false);

        var import = Assert.Single(analysis.Metrics, value => value.Metric == "Average monthly import");
        Assert.Equal(110m, import.Before);
        Assert.Equal(70m, import.After);
        var combined = Assert.Single(analysis.Metrics, value => value.Metric == "Average monthly combined utility cost");
        Assert.Equal(30m, combined.Before);
        Assert.Equal(13.25m, combined.After);
        var may = Assert.Single(analysis.SameMonthComparisons, value => value.AfterMonth == new DateOnly(2026, 5, 1));
        Assert.Equal(-40m, may.ImportChangeKwh);
        Assert.Equal(50m, may.ExportChangeKwh);
        Assert.Equal(-90m, may.NetGridChangeKwh);
        Assert.Equal(-17.5m, may.CombinedUtilityCostChangeGbp);
    }

    [Fact]
    public void HolidayMonths_AreExcludedOnlyWhenRequested()
    {
        var monthly = Insights(Point(new(2025, 5, 1), 20m, 0m, 0m, 5m, 0m, 0m), Point(new(2025, 6, 1), 100m, 0m, 0m, 25m, 0m, 0m), Point(new(2025, 9, 1), 50m, 10m, 0m, 12m, 1.5m, 0m));
        var config = new SolarAnalysisConfiguration(new(2025, 8, 1), SolarDetectionConfidence.High, "test", null, null);
        var marker = new HomeEvent(1, new(2025, 5, 10), "Away", "Holiday");
        var included = EnergyComparisonEngine.AnalyzeSolar(monthly, config, [marker], false);
        var excluded = EnergyComparisonEngine.AnalyzeSolar(monthly, config, [marker], true);
        Assert.Equal(60m, Assert.Single(included.Metrics, value => value.Metric == "Average monthly import").Before);
        Assert.Equal(100m, Assert.Single(excluded.Metrics, value => value.Metric == "Average monthly import").Before);
        Assert.Equal([new DateOnly(2025, 5, 1)], excluded.ExcludedAwayMonths);
    }

    [Fact]
    public void AnyMarker_CanProduceAnOnDemandImpactComparison()
    {
        var monthly = Insights(Point(new(2025, 11, 1), 100m, 0m, 80m, 25m, 0m, 8m), Point(new(2025, 12, 1), 90m, 0m, 70m, 22m, 0m, 7m), Point(new(2026, 2, 1), 70m, 0m, 45m, 18m, 0m, 4.5m), Point(new(2026, 3, 1), 60m, 0m, 40m, 15m, 0m, 4m));
        var marker = new HomeEvent(1, new(2026, 1, 15), "Loft insulation installed", "Property");
        var analysis = EnergyComparisonEngine.AnalyzeMarker(monthly, marker, [marker], false);
        Assert.Equal(75m, Assert.Single(analysis.Metrics, value => value.Metric == "Average monthly gas usage").Before);
        Assert.Equal(42.5m, Assert.Single(analysis.Metrics, value => value.Metric == "Average monthly gas usage").After);
    }

    [Fact]
    public async Task ProjectFoundation_StoresFutureCostStartAndAccumulatedBenefit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"joakim-project-foundation-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteDashboardRepository(path); await repository.InitializeAsync();
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var insert = connection.CreateCommand(); insert.CommandText = "INSERT INTO energy_project_foundations(name,category,project_cost,start_date,accumulated_benefit,created_at,updated_at) VALUES('Loft insulation','Insulation',2500,'2026-01-15',125,'2026-01-01','2026-06-01')"; await insert.ExecuteNonQueryAsync();
                await using var read = connection.CreateCommand(); read.CommandText = "SELECT project_cost,start_date,accumulated_benefit FROM energy_project_foundations";
                await using var reader = await read.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
                Assert.Equal(2500m, reader.GetDecimal(0)); Assert.Equal("2026-01-15", reader.GetString(1)); Assert.Equal(125m, reader.GetDecimal(2));
            }
            SqliteConnection.ClearAllPools();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static IReadOnlyList<MonthlyEnergyInsight> Insights(params EnergyPeriodPoint[] points) => EnergyInsightBuilder.BuildMonthly(points);

    private static EnergyPeriodPoint Point(DateOnly month, decimal import, decimal export, decimal gas, decimal importCost, decimal exportIncome, decimal gasCost)
        => new(month, import, export, gas, importCost, exportIncome, gasCost, true, true, true, import, 0, 0, importCost, 0, true, false);
}
