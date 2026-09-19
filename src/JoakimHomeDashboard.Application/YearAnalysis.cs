using JoakimHomeDashboard.Domain;

namespace JoakimHomeDashboard.Application;

public static class YearAnalysisBuilder
{
    public static IReadOnlyList<int> AvailableYears(IEnumerable<MonthlyEnergyInsight> monthly)
        => monthly.Select(value => value.Month.Year).Distinct().OrderBy(value => value).ToArray();

    public static IReadOnlyList<YearAnalysisSeries> BuildSeries(IEnumerable<MonthlyEnergyInsight> monthly, YearAnalysisMetric metric, IEnumerable<int> selectedYears)
    {
        var source = monthly.ToArray();
        var result = new List<YearAnalysisSeries>();
        foreach (var year in selectedYears.Distinct().OrderBy(value => value))
        {
            var values = source.Where(value => value.Month.Year == year).ToDictionary(value => value.Month.Month);
            var months = Enumerable.Range(1, 12).Select(month => values.TryGetValue(month, out var point) ? Value(point, metric) : null).ToArray();
            if (months.Any(value => value is not null)) result.Add(new("Octopus", metric, Label(metric), Unit(metric), year, months));
        }
        return result;
    }

    public static IReadOnlyList<YearAnalysisTableRow> BuildTable(IEnumerable<MonthlyEnergyInsight> monthly, YearAnalysisMetric metric, IEnumerable<int> selectedYears)
    {
        var series = BuildSeries(monthly, metric, selectedYears);
        return Enumerable.Range(1, 12).Select(month =>
        {
            var values = series.Select(value => new YearAnalysisValue(value.Year, value.Months[month - 1])).ToArray();
            var available = values.Where(value => value.Value is not null).OrderBy(value => value.Year).ToArray();
            var difference = available.Length >= 2 ? available[^1].Value - available[0].Value : null;
            var percentage = difference is not null && available[0].Value != 0 ? difference / Math.Abs(available[0].Value!.Value) * 100m : null;
            return new YearAnalysisTableRow(month, values, difference, percentage);
        }).ToArray();
    }

    public static IReadOnlyList<YearEnergySummary> BuildSummaries(IEnumerable<MonthlyEnergyInsight> monthly, IEnumerable<int> selectedYears)
        => selectedYears.Distinct().OrderBy(value => value).Select(year =>
        {
            var rows = monthly.Where(value => value.Month.Year == year).ToArray();
            return new YearEnergySummary(year,
                Energy(rows, value => value.ImportKwh), Energy(rows, value => value.ExportKwh), Energy(rows, value => value.NetGridKwh),
                Cost(rows, value => value.ImportCostGbp, value => value.ImportCostExact),
                Cost(rows, value => value.ExportIncomeGbp, value => value.ExportKwh == 0 || value.ExportIncomeExact),
                Cost(rows, value => value.NetElectricityCostGbp, value => value.NetElectricityCostExact),
                Cost(rows, value => value.GasCostGbp, value => value.GasKwh == 0 || value.GasCostExact),
                Cost(rows, value => value.CombinedUtilityCostGbp, value => value.CombinedUtilityCostExact));
        }).ToArray();

    public static string Label(YearAnalysisMetric metric) => metric switch
    {
        YearAnalysisMetric.ImportCost => "Import cost",
        YearAnalysisMetric.NetElectricityCost => "Net electricity cost",
        YearAnalysisMetric.ImportUsage => "Electricity import",
        YearAnalysisMetric.ExportUsage => "Electricity export",
        YearAnalysisMetric.NetGridUsage => "Net grid usage",
        YearAnalysisMetric.GasUsage => "Gas usage",
        YearAnalysisMetric.GasCost => "Gas cost",
        YearAnalysisMetric.CombinedUtilityCost => "Combined utility cost",
        _ => metric.ToString()
    };

    public static string Unit(YearAnalysisMetric metric) => metric is YearAnalysisMetric.ImportCost or YearAnalysisMetric.NetElectricityCost or YearAnalysisMetric.GasCost or YearAnalysisMetric.CombinedUtilityCost ? "£" : "kWh";

    private static decimal? Value(MonthlyEnergyInsight point, YearAnalysisMetric metric) => metric switch
    {
        YearAnalysisMetric.ImportCost => point.ImportCostExact ? point.ImportCostGbp : null,
        YearAnalysisMetric.NetElectricityCost => point.NetElectricityCostExact ? point.NetElectricityCostGbp : null,
        YearAnalysisMetric.ImportUsage => point.ImportKwh,
        YearAnalysisMetric.ExportUsage => point.ExportKwh,
        YearAnalysisMetric.NetGridUsage => point.NetGridKwh,
        YearAnalysisMetric.GasUsage => point.GasKwh,
        YearAnalysisMetric.GasCost => point.GasCostExact ? point.GasCostGbp : null,
        YearAnalysisMetric.CombinedUtilityCost => point.CombinedUtilityCostExact ? point.CombinedUtilityCostGbp : null,
        _ => null
    };


    private static AnnualAnalysisValue Energy(IReadOnlyCollection<MonthlyEnergyInsight> rows, Func<MonthlyEnergyInsight, decimal> value)
    {
        var values = rows.Select(value).ToArray();
        return new(values.Length == 0 ? null : values.Sum(), values.Length == 0 ? null : values.Average(), "kWh");
    }

    private static AnnualAnalysisValue Cost(IReadOnlyCollection<MonthlyEnergyInsight> rows, Func<MonthlyEnergyInsight, decimal> value, Func<MonthlyEnergyInsight, bool> exact)
    {
        if (rows.Count == 0 || rows.Any(row => !exact(row))) return new(null, null, "£");
        var values = rows.Select(value).ToArray();
        return new(values.Sum(), values.Average(), "£");
    }
}
