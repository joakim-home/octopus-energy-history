using JoakimHomeDashboard.Application;
using JoakimHomeDashboard.Domain;

namespace JoakimHomeDashboard.Tests;

public sealed class YearAnalysisTests
{
    [Fact]
    public void SelectedYears_DriveSharedTwelveMonthSeriesWithoutDuplicatingData()
    {
        var monthly = Insights(
            Point(new(2024, 1, 1), 100m, 10m, 60m, 25m, 1.5m, 6m),
            Point(new(2025, 1, 1), 80m, 20m, 50m, 20m, 3m, 5m),
            Point(new(2025, 2, 1), 70m, 25m, 40m, 18m, 3.75m, 4m),
            Point(new(2026, 1, 1), 60m, 30m, 35m, 15m, 4.5m, 3.5m));

        var series = YearAnalysisBuilder.BuildSeries(monthly, YearAnalysisMetric.NetElectricityCost, [2025, 2026]);
        Assert.Equal(2, series.Count);
        Assert.All(series, value => Assert.Equal(12, value.Months.Count));
        Assert.DoesNotContain(series, value => value.Year == 2024);
        Assert.Equal(17m, series.Single(value => value.Year == 2025).Months[0]);
        Assert.Null(series.Single(value => value.Year == 2026).Months[1]);
        Assert.All(series, value => Assert.Equal("Octopus", value.Source));
    }

    [Fact]
    public void ComparisonTable_CalculatesLatestMinusEarliestAndPercentage()
    {
        var monthly = Insights(Point(new(2024, 1, 1), 100m, 0m, 0m, 20m, 0m, 0m), Point(new(2025, 1, 1), 90m, 0m, 0m, 18m, 0m, 0m), Point(new(2026, 1, 1), 75m, 0m, 0m, 15m, 0m, 0m));
        var january = Assert.Single(YearAnalysisBuilder.BuildTable(monthly, YearAnalysisMetric.ImportCost, [2024, 2025, 2026]), value => value.Month == 1);
        Assert.Equal(-5m, january.Difference);
        Assert.Equal(-25m, january.PercentageDifference);
        Assert.Equal([2024, 2025, 2026], january.Values.Select(value => value.Year));
    }

    [Fact]
    public void YearSummary_ProvidesRequestedTotalsAndMonthlyAverages()
    {
        var monthly = Insights(
            Point(new(2026, 1, 1), 100m, 20m, 50m, 25m, 3m, 5m),
            Point(new(2026, 2, 1), 80m, 30m, 30m, 20m, 4.5m, 3m));
        var summary = Assert.Single(YearAnalysisBuilder.BuildSummaries(monthly, [2026]));
        Assert.Equal(180m, summary.ElectricityImport.Total); Assert.Equal(90m, summary.ElectricityImport.MonthlyAverage);
        Assert.Equal(50m, summary.ElectricityExport.Total); Assert.Equal(130m, summary.NetGridUsage.Total);
        Assert.Equal(45m, summary.ElectricityCost.Total); Assert.Equal(7.5m, summary.ExportIncome.Total);
        Assert.Equal(37.5m, summary.NetElectricityCost.Total); Assert.Equal(8m, summary.GasCost.Total);
        Assert.Equal(45.5m, summary.CombinedUtilityCost.Total); Assert.Equal(22.75m, summary.CombinedUtilityCost.MonthlyAverage);
    }

    [Fact]
    public void MissingExactCost_RemainsUnavailableWhileUsageStillWorks()
    {
        var point = Point(new(2026, 1, 1), 100m, 0m, 0m, 0m, 0m, 0m) with { ImportCostExact = false };
        var monthly = Insights(point);
        Assert.Empty(YearAnalysisBuilder.BuildSeries(monthly, YearAnalysisMetric.ImportCost, [2026]));
        Assert.Equal(100m, Assert.Single(YearAnalysisBuilder.BuildSeries(monthly, YearAnalysisMetric.ImportUsage, [2026])).Months[0]);
        Assert.Null(Assert.Single(YearAnalysisBuilder.BuildSummaries(monthly, [2026])).ElectricityCost.Total);
    }

    [Fact]
    public void AvailableYears_AreChronologicalAndEmptyYearsAreAbsent()
    {
        var monthly = Insights(Point(new(2026, 1, 1), 1m, 0m, 0m, 1m, 0m, 0m), Point(new(2024, 1, 1), 1m, 0m, 0m, 1m, 0m, 0m));
        Assert.Equal([2024, 2026], YearAnalysisBuilder.AvailableYears(monthly));
    }



    private static IReadOnlyList<MonthlyEnergyInsight> Insights(params EnergyPeriodPoint[] points) => EnergyInsightBuilder.BuildMonthly(points);
    private static EnergyPeriodPoint Point(DateOnly month, decimal import, decimal export, decimal gas, decimal importCost, decimal exportIncome, decimal gasCost)
        => new(month, import, export, gas, importCost, exportIncome, gasCost, true, true, true, import, 0, 0, importCost, 0, true, false);
}
