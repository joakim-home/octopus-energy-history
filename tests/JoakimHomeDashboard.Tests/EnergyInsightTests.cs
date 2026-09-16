using JoakimHomeDashboard.Application;
using JoakimHomeDashboard.Domain;

namespace JoakimHomeDashboard.Tests;

public sealed class EnergyInsightTests
{
    [Fact]
    public void DailyValues_AggregateIntoAccurateMonthlyInsight()
    {
        var points = new[]
        {
            Point(new(2026, 1, 1), 10m, 4m, 20m, 2m, 0.6m, 1m, 6m, 4m, 1.4m, 0.6m),
            Point(new(2026, 1, 2), 12m, 3m, 18m, 3m, 0.45m, 0.9m, 7m, 5m, 1.8m, 1.2m),
            Point(new(2026, 2, 1), 8m, 2m, 15m, 1.8m, 0.3m, 0.8m, 3m, 5m, 0.7m, 1.1m)
        };

        var january = Assert.Single(EnergyInsightBuilder.BuildMonthly(points), value => value.Month == new DateOnly(2026, 1, 1));
        Assert.Equal(22m, january.ImportKwh);
        Assert.Equal(7m, january.ExportKwh);
        Assert.Equal(15m, january.NetGridKwh);
        Assert.Equal(5m, january.ImportCostGbp);
        Assert.Equal(1.05m, january.ExportIncomeGbp);
        Assert.Equal(3.95m, january.NetElectricityCostGbp);
    }

    [Fact]
    public void Export_IsNegativeForChartsAndNetCreditIsObvious()
    {
        var insight = Assert.Single(EnergyInsightBuilder.BuildMonthly([
            Point(new(2026, 6, 1), 20m, 30m, 0m, 4m, 6m, 0m, 12m, 8m, 2m, 2m)]));

        Assert.Equal(-30m, insight.ExportChartKwh);
        Assert.Equal(-10m, insight.NetGridKwh);
        Assert.Equal(-6m, insight.ExportIncomeChartGbp);
        Assert.Equal(-2m, insight.NetElectricityCostGbp);
    }

    [Fact]
    public void GraphValuesAndTableTotals_UseTheSameMonthlyModels()
    {
        var rows = EnergyInsightBuilder.BuildMonthly([
            Point(new(2026, 4, 1), 40m, 10m, 12m, 8m, 2m, 1m, 25m, 15m, 5m, 3m),
            Point(new(2026, 5, 1), 30m, 20m, 5m, 6m, 4m, 0.5m, 18m, 12m, 3m, 3m)]);
        var total = EnergyInsightBuilder.Total(rows);

        Assert.Equal(rows.Sum(row => row.NetGridKwh), total.NetGridKwh);
        Assert.Equal(rows.Sum(row => row.NetElectricityCostGbp), total.NetElectricityCostGbp);
        Assert.Equal(rows.Sum(row => row.ExportIncomeGbp), total.ExportIncomeGbp);
        Assert.Equal(rows.Sum(row => row.CombinedUtilityCostGbp), total.CombinedUtilityCostGbp);
        Assert.True(total.NetElectricityCostExact);
    }

    [Fact]
    public void MonthlyInsight_ReservesFutureSolarAndBatteryDimensionsWithoutInventingData()
    {
        var insight = Assert.Single(EnergyInsightBuilder.BuildMonthly([Point(new(2026, 1, 1), 1m, 0m, 0m, 0.2m, 0m, 0m, 1m, 0m, 0.2m, 0m)]));
        Assert.Equal(0m, insight.SolarGenerationKwh);
        Assert.Equal(0m, insight.SelfConsumptionKwh);
        Assert.Equal(0m, insight.BatteryChargeKwh);
        Assert.Equal(0m, insight.BatteryDischargeKwh);
    }

    private static EnergyPeriodPoint Point(DateOnly date, decimal import, decimal export, decimal gas, decimal importCost, decimal exportIncome, decimal gasCost, decimal peak, decimal offPeak, decimal peakCost, decimal offPeakCost)
        => new(date, import, export, gas, importCost, exportIncome, gasCost, true, true, true, peak, offPeak, 0, peakCost, offPeakCost, true, true);
}
