using JoakimHomeDashboard.Domain;

namespace JoakimHomeDashboard.Application;

public static class EnergyInsightBuilder
{
    public static IReadOnlyList<MonthlyEnergyInsight> BuildMonthly(IEnumerable<EnergyPeriodPoint> periods)
        => periods.GroupBy(point => new { point.Period.Year, point.Period.Month })
            .OrderBy(group => group.Key.Year).ThenBy(group => group.Key.Month)
            .Select(group =>
            {
                var values = group.ToArray();
                return new MonthlyEnergyInsight(
                    new(group.Key.Year, group.Key.Month, 1),
                    values.Sum(point => point.ImportKwh), values.Sum(point => point.ExportKwh), values.Sum(point => point.GasKwh),
                    values.Sum(point => point.PeakImportKwh), values.Sum(point => point.OffPeakImportKwh),
                    values.Sum(point => point.ImportCost), values.Sum(point => point.ExportIncome), values.Sum(point => point.GasCost),
                    values.Sum(point => point.PeakImportCost), values.Sum(point => point.OffPeakImportCost),
                    IsExact(values, point => point.ImportKwh, point => point.ImportCostExact),
                    IsExact(values, point => point.ExportKwh, point => point.ExportIncomeExact),
                    IsExact(values, point => point.GasKwh, point => point.GasCostExact),
                    IsExact(values, point => point.PeakImportKwh, point => point.PeakCostExact),
                    IsExact(values, point => point.OffPeakImportKwh, point => point.OffPeakCostExact));
            }).ToArray();

    public static MonthlyEnergyInsight Total(IEnumerable<MonthlyEnergyInsight> source)
    {
        var values = source.ToArray();
        return new(
            DateOnly.MinValue,
            values.Sum(point => point.ImportKwh), values.Sum(point => point.ExportKwh), values.Sum(point => point.GasKwh),
            values.Sum(point => point.PeakImportKwh), values.Sum(point => point.OffPeakImportKwh),
            values.Sum(point => point.ImportCostGbp), values.Sum(point => point.ExportIncomeGbp), values.Sum(point => point.GasCostGbp),
            values.Sum(point => point.PeakImportCostGbp), values.Sum(point => point.OffPeakImportCostGbp),
            HasExact(values, point => point.ImportKwh, point => point.ImportCostExact),
            HasExact(values, point => point.ExportKwh, point => point.ExportIncomeExact),
            HasExact(values, point => point.GasKwh, point => point.GasCostExact),
            HasExact(values, point => point.PeakImportKwh, point => point.PeakCostExact),
            HasExact(values, point => point.OffPeakImportKwh, point => point.OffPeakCostExact),
            values.Sum(point => point.SolarGenerationKwh), values.Sum(point => point.SelfConsumptionKwh),
            values.Sum(point => point.BatteryChargeKwh), values.Sum(point => point.BatteryDischargeKwh));
    }

    private static bool IsExact(IEnumerable<EnergyPeriodPoint> values, Func<EnergyPeriodPoint, decimal> quantity, Func<EnergyPeriodPoint, bool> exact)
    {
        var withUsage = values.Where(point => quantity(point) != 0).ToArray();
        return withUsage.Length > 0 && withUsage.All(exact);
    }

    private static bool HasExact(IEnumerable<MonthlyEnergyInsight> values, Func<MonthlyEnergyInsight, decimal> quantity, Func<MonthlyEnergyInsight, bool> exact)
    {
        var withUsage = values.Where(point => quantity(point) != 0).ToArray();
        return withUsage.Length > 0 && withUsage.All(exact);
    }
}
