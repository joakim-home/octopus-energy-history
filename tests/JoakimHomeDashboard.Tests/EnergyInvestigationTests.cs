using JoakimHomeDashboard.Application;
using JoakimHomeDashboard.Domain;
using JoakimHomeDashboard.Infrastructure;

namespace JoakimHomeDashboard.Tests;

public sealed class EnergyInvestigationTests
{
    [Fact]
    public void SpikeDetection_UsesPrecedingThirtyDayAverageForImportAndExport()
    {
        var start = new DateOnly(2026, 5, 1);
        var normal = Enumerable.Range(0, 30).Select(day => new EnergyPeriodPoint(start.AddDays(day), 10m, 2m, 0m)).ToList();
        normal.Add(new(start.AddDays(30), 31m, 6.1m, 0m));

        var spikes = EnergySpikeDetector.Detect(normal);

        Assert.Contains(spikes, spike => spike.Date == start.AddDays(30) && spike.FlowType == EnergyFlowType.ElectricityImport && spike.RollingAverageKwh == 10m);
        Assert.Contains(spikes, spike => spike.Date == start.AddDays(30) && spike.FlowType == EnergyFlowType.ElectricityExport && spike.RollingAverageKwh == 2m);
    }

    [Fact]
    public void SpikeDetection_DoesNotUseCurrentDayInItsOwnBaseline()
    {
        var start = new DateOnly(2026, 5, 1);
        var points = Enumerable.Range(0, 8).Select(day => new EnergyPeriodPoint(start.AddDays(day), day == 7 ? 40m : 10m, 0m, 0m)).ToArray();
        var spike = Assert.Single(EnergySpikeDetector.Detect(points));
        Assert.Equal(4m, spike.Multiple);
    }

    [Fact]
    public void DataQualityWarnings_AreGroupedWithoutDuplicatingBanners()
    {
        var groups = DataQualityGrouping.Group([
            "Some cost periods are unavailable because tariff coverage is incomplete.",
            "10 export readings were excluded.",
            "ElectricityImport data incomplete: 22/30 recent days present.",
            "ElectricityExport data incomplete: 29/30 recent days present.",
            "1 export interval reading exceeds the credible half-hour export limit of 3.89 kWh for a 5.76 kWp solar array.",
            "Latest sync failed: HTTP 400."]);

        Assert.Contains(groups, group => group.Title == "Missing tariff coverage");
        Assert.Contains(groups, group => group.Title == "Untrusted export readings" && group.Severity == DataQualitySeverity.Warning && group.Count == 1);
        Assert.Contains(groups, group => group.Title == "Missing days" && group.Count == 2);
        Assert.Contains(groups, group => group.Title == "Export capability checks" && group.Severity == DataQualitySeverity.Warning && group.Count == 1);
        Assert.Contains(groups, group => group.Title == "Sync failures" && group.Severity == DataQualitySeverity.Critical);
        Assert.Equal(6, groups.Sum(group => group.Warnings.Count));
        var partial = Assert.Single(DataQualityGrouping.Group(["Negative usage was found and should be reviewed."]));
        Assert.Equal("Partial data", partial.Title);
        Assert.Equal(DataQualitySeverity.Information, partial.Severity);
        var tariff = Assert.Single(groups, group => group.Title == "Missing tariff coverage");
        Assert.Contains(tariff.Warnings, warning => warning.Contains("tariff coverage is incomplete", StringComparison.Ordinal) && warning.Contains("Usage history remains available", StringComparison.Ordinal));
        var missingDays = Assert.Single(groups, group => group.Title == "Missing days");
        Assert.DoesNotContain(missingDays.Warnings, warning => warning.Contains("excluded from export and net calculations", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HoverInspection_IncludesDateSeriesUnitDirectionAndRateBand()
    {
        var text = ChartPresentation.FormatHover([
            new(new(2026, 6, 18), "Grid import", 4.125m, "kWh", ImportRateBand.Peak),
            new(new(2026, 6, 18), "Export", 1.375m, "kWh")]);

        Assert.Contains("18 Jun 2026", text, StringComparison.Ordinal);
        Assert.Contains("Grid import (peak): 4.125 kWh", text, StringComparison.Ordinal);
        Assert.Contains("Export: 1.375 kWh", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IntervalInspection_ReturnsTrustedHalfHourlyValuesAndClassification()
    {
        var path = Path.Combine(Path.GetTempPath(), $"joakim-inspection-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteDashboardRepository(path);
            await repository.InitializeAsync();
            var start = DateTimeOffset.Parse("2026-06-18T00:00:00Z");
            var meter = new OctopusMeterPoint("A", 1, "electricity", false, "IMP", "I1", "E-1R-INTELLI-GO-A", "INTELLI-GO", null, null);
            await repository.ReplaceOctopusConfigurationAsync(new("A", 1, [meter], []));
            await repository.UpsertOctopusRawReadingsAsync([
                new(start, start.AddMinutes(30), EnergyFlowType.ElectricityImport, 1.25m, 0.0875m, "IMP", "I1", meter.TariffCode, "trusted", ImportRateBand.OffPeak, 7m),
                new(start, start.AddMinutes(30), EnergyFlowType.ElectricityImport, 99m, null, "OTHER", "BAD", meter.TariffCode, "untrusted")]);

            var interval = Assert.Single(await repository.GetOctopusIntervalsAsync(new(2026, 6, 18)));
            Assert.Equal(1.25m, interval.QuantityKwh);
            Assert.Equal(ImportRateBand.OffPeak, interval.RateBand);
            Assert.Equal(7m, interval.UnitRatePence);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
