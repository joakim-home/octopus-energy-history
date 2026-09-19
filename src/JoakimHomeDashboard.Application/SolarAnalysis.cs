using JoakimHomeDashboard.Domain;

namespace JoakimHomeDashboard.Application;

public static class SolarStartDetector
{
    private const decimal PresenceThresholdKwh = 0.01m;

    public static SolarDetectionResult Detect(IEnumerable<DailyExportObservation> observations)
    {
        var totals = observations.GroupBy(value => value.Date).ToDictionary(group => group.Key, group => group.Sum(value => value.ExportKwh));
        var present = totals.Where(pair => pair.Value >= PresenceThresholdKwh).Select(pair => pair.Key).ToHashSet();
        if (present.Count == 0) return new(null, SolarDetectionConfidence.None, "No sustained export detected", 0, 0);
        var first = totals.Keys.Min();
        var last = totals.Keys.Max();
        Candidate? sevenDay = null;
        Candidate? thirtyDay = null;
        for (var start = first; start <= last; start = start.AddDays(1))
        {
            if (sevenDay is null) sevenDay = CandidateFor(start, 7, 5, present);
            if (thirtyDay is null) thirtyDay = CandidateFor(start, 30, 20, present);
            if (sevenDay is not null && thirtyDay is not null) break;
        }
        if (sevenDay is null && thirtyDay is null) return new(null, SolarDetectionConfidence.None, "No sustained export detected", present.Count, 0);
        if (sevenDay is not null && thirtyDay is not null)
        {
            var selected = sevenDay.StartDate <= thirtyDay.StartDate ? sevenDay : thirtyDay;
            return new(selected.StartDate, SolarDetectionConfidence.High, "5 of 7 days and 20 of 30 days sustained export", Math.Max(sevenDay.SupportingDays, thirtyDay.SupportingDays), 30);
        }
        var candidate = sevenDay ?? thirtyDay!;
        return sevenDay is not null
            ? new(candidate.StartDate, SolarDetectionConfidence.Medium, "Export present on at least 5 of 7 consecutive days", candidate.SupportingDays, 7)
            : new(candidate.StartDate, SolarDetectionConfidence.High, "Export present on at least 20 days in a rolling 30-day window", candidate.SupportingDays, 30);
    }

    private static Candidate? CandidateFor(DateOnly windowStart, int windowDays, int requiredDays, IReadOnlySet<DateOnly> present)
    {
        var dates = Enumerable.Range(0, windowDays).Select(windowStart.AddDays).Where(present.Contains).ToArray();
        return dates.Length >= requiredDays ? new(dates.Min(), dates.Length) : null;
    }

    private sealed record Candidate(DateOnly StartDate, int SupportingDays);
}

public static class EnergyComparisonEngine
{
    private static readonly string[] AwayCategories = ["Holiday", "Away", "Travel"];

    public static SolarBaselineAnalysis AnalyzeSolar(IReadOnlyCollection<MonthlyEnergyInsight> monthly, SolarAnalysisConfiguration configuration, IReadOnlyCollection<HomeEvent> markers, bool excludeAwayMonths)
    {
        var start = configuration.EffectiveStartDate;
        if (start is null) return new(configuration, new(null, null), new(null, null), [], [], []);
        var ordered = monthly.OrderBy(value => value.Month).ToArray();
        var away = excludeAwayMonths ? markers.Where(IsAway).Select(value => new DateOnly(value.Date.Year, value.Date.Month, 1)).Distinct().OrderBy(value => value).ToArray() : [];
        var baselineEndExclusive = new DateOnly(start.Value.Year, start.Value.Month, 1);
        var solarStart = start.Value.Day == 1 ? baselineEndExclusive : baselineEndExclusive.AddMonths(1);
        var before = ordered.Where(value => value.Month < baselineEndExclusive && !away.Contains(value.Month)).ToArray();
        var after = ordered.Where(value => value.Month >= solarStart).ToArray();
        var baselinePeriod = before.Length == 0 ? new EnergyAnalysisPeriod(null, null) : new(before[0].Month, before[^1].Month.AddMonths(1).AddDays(-1));
        var solarPeriod = after.Length == 0 ? new EnergyAnalysisPeriod(null, null) : new(after[0].Month, after[^1].Month.AddMonths(1).AddDays(-1));
        return new(configuration, baselinePeriod, solarPeriod, Metrics(before, after), SameMonth(before, after), away);
    }

    public static SolarSavingsEstimate EstimateSolarSavings(IReadOnlyCollection<EnergyPeriodPoint> daily, SolarAnalysisConfiguration configuration, DateOnly today)
    {
        var start = configuration.EffectiveStartDate;
        if (start is null) return new(null, 0, null, null);

        static bool ActualExact(EnergyPeriodPoint point)
            => point.ImportCostExact
               && (point.ExportKwh == 0 || point.ExportIncomeExact)
               && (point.GasKwh == 0 || point.GasCostExact);

        static decimal ActualVariableCost(EnergyPeriodPoint point)
            => point.ImportCost - point.ExportIncome + point.GasCost;

        var ordered = daily.OrderBy(point => point.Period).ToArray();
        var baseline = ordered.Where(point => point.Period < start.Value).ToArray();
        var candidates = ordered.Where(point => point.Period >= start.Value && point.Period < today && ActualExact(point)).ToArray();

        var rates = candidates
            .GroupBy(point => new DateOnly(point.Period.Year, point.Period.Month, 1))
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var peakKwh = group.Sum(point => point.PeakImportKwh);
                    var offPeakKwh = group.Sum(point => point.OffPeakImportKwh);
                    var gasKwh = group.Sum(point => point.GasKwh);
                    var peakExact = group.All(point => point.PeakImportKwh == 0 || point.PeakCostExact);
                    var offPeakExact = group.All(point => point.OffPeakImportKwh == 0 || point.OffPeakCostExact);
                    var gasExact = group.All(point => point.GasKwh == 0 || point.GasCostExact);
                    return (
                        PeakRate: peakKwh > 0 && peakExact ? group.Sum(point => point.PeakImportCost) / peakKwh : (decimal?)null,
                        OffPeakRate: offPeakKwh > 0 && offPeakExact ? group.Sum(point => point.OffPeakImportCost) / offPeakKwh : (decimal?)null,
                        GasRate: gasKwh > 0 && gasExact ? group.Sum(point => point.GasCost) / gasKwh : (decimal?)null);
                });

        decimal savings = 0;
        var used = new List<DateOnly>();
        foreach (var current in candidates)
        {
            var sameMonth = baseline.Where(point => point.Period.Month == current.Period.Month).ToArray();
            if (sameMonth.Length == 0) continue;

            var baselinePeak = sameMonth.Average(point => point.PeakImportKwh);
            var baselineOffPeak = sameMonth.Average(point => point.OffPeakImportKwh);
            var baselineUnknown = sameMonth.Average(point => point.UnknownImportKwh);
            var baselineGas = sameMonth.Average(point => point.GasKwh);
            if (baselineUnknown > 0.001m) continue;

            var rateMonth = new DateOnly(current.Period.Year, current.Period.Month, 1);
            if (!rates.TryGetValue(rateMonth, out var rate)) continue;
            if (baselinePeak > 0 && rate.PeakRate is null) continue;
            if (baselineOffPeak > 0 && rate.OffPeakRate is null) continue;
            if (baselineGas > 0 && rate.GasRate is null) continue;

            var expected = baselinePeak * (rate.PeakRate ?? 0)
                         + baselineOffPeak * (rate.OffPeakRate ?? 0)
                         + baselineGas * (rate.GasRate ?? 0);

            savings += expected - ActualVariableCost(current);
            used.Add(current.Period);
        }

        return used.Count == 0
            ? new(null, 0, null, null)
            : new(decimal.Round(savings, 2), used.Count, used[0], used[^1]);
    }

    public static MarkerImpactAnalysis AnalyzeMarker(IReadOnlyCollection<MonthlyEnergyInsight> monthly, HomeEvent marker, IReadOnlyCollection<HomeEvent> markers, bool excludeAwayMonths)
    {
        var boundaryMonth = new DateOnly(marker.Date.Year, marker.Date.Month, 1);
        var away = excludeAwayMonths ? markers.Where(IsAway).Select(value => new DateOnly(value.Date.Year, value.Date.Month, 1)).ToHashSet() : [];
        var before = monthly.Where(value => value.Month < boundaryMonth && !away.Contains(value.Month)).OrderBy(value => value.Month).ToArray();
        var after = monthly.Where(value => value.Month > boundaryMonth).OrderBy(value => value.Month).ToArray();
        var beforePeriod = before.Length == 0 ? new EnergyAnalysisPeriod(null, null) : new(before[0].Month, before[^1].Month.AddMonths(1).AddDays(-1));
        var afterPeriod = after.Length == 0 ? new EnergyAnalysisPeriod(null, null) : new(after[0].Month, after[^1].Month.AddMonths(1).AddDays(-1));
        var metrics = new[]
        {
            Compare("Average monthly gas usage", "kWh", before.Select(value => (decimal?)value.GasKwh), after.Select(value => (decimal?)value.GasKwh)),
            Compare("Average monthly electricity import", "kWh", before.Select(value => (decimal?)value.ImportKwh), after.Select(value => (decimal?)value.ImportKwh)),
            Compare("Average monthly combined utility cost", "£", before.Select(value => value.CombinedUtilityCostExact ? (decimal?)value.CombinedUtilityCostGbp : null), after.Select(value => value.CombinedUtilityCostExact ? (decimal?)value.CombinedUtilityCostGbp : null))
        };
        return new(marker, beforePeriod, afterPeriod, metrics);
    }

    private static IReadOnlyList<AnalysisMetricComparison> Metrics(IReadOnlyCollection<MonthlyEnergyInsight> before, IReadOnlyCollection<MonthlyEnergyInsight> after) =>
    [
        Compare("Average monthly import", "kWh", before.Select(value => (decimal?)value.ImportKwh), after.Select(value => (decimal?)value.ImportKwh)),
        Compare("Average monthly export", "kWh", before.Select(value => (decimal?)value.ExportKwh), after.Select(value => (decimal?)value.ExportKwh)),
        Compare("Average monthly gas", "kWh", before.Select(value => (decimal?)value.GasKwh), after.Select(value => (decimal?)value.GasKwh)),
        Compare("Average monthly electricity cost", "£", before.Select(value => value.ImportCostExact ? (decimal?)value.ImportCostGbp : null), after.Select(value => value.ImportCostExact ? (decimal?)value.ImportCostGbp : null)),
        Compare("Average monthly export income", "£", before.Select(value => value.ExportIncomeExact ? (decimal?)value.ExportIncomeGbp : null), after.Select(value => value.ExportIncomeExact ? (decimal?)value.ExportIncomeGbp : null)),
        Compare("Average monthly net electricity cost", "£", before.Select(value => value.NetElectricityCostExact ? (decimal?)value.NetElectricityCostGbp : null), after.Select(value => value.NetElectricityCostExact ? (decimal?)value.NetElectricityCostGbp : null)),
        Compare("Average monthly combined utility cost", "£", before.Select(value => value.CombinedUtilityCostExact ? (decimal?)value.CombinedUtilityCostGbp : null), after.Select(value => value.CombinedUtilityCostExact ? (decimal?)value.CombinedUtilityCostGbp : null))
    ];

    private static IReadOnlyList<SameMonthEnergyComparison> SameMonth(IReadOnlyCollection<MonthlyEnergyInsight> before, IReadOnlyCollection<MonthlyEnergyInsight> after)
    {
        var comparisons = new List<SameMonthEnergyComparison>();
        foreach (var current in after.OrderByDescending(value => value.Month))
        {
            var baseline = before.Where(value => value.Month.Month == current.Month.Month).OrderByDescending(value => value.Month).FirstOrDefault();
            if (baseline is null) continue;
            comparisons.Add(new(baseline.Month, current.Month, current.ImportKwh - baseline.ImportKwh, current.ExportKwh - baseline.ExportKwh, current.NetGridKwh - baseline.NetGridKwh,
                current.NetElectricityCostExact && baseline.NetElectricityCostExact ? current.NetElectricityCostGbp - baseline.NetElectricityCostGbp : null,
                current.CombinedUtilityCostExact && baseline.CombinedUtilityCostExact ? current.CombinedUtilityCostGbp - baseline.CombinedUtilityCostGbp : null));
        }
        return comparisons;
    }

    private static AnalysisMetricComparison Compare(string metric, string unit, IEnumerable<decimal?> before, IEnumerable<decimal?> after)
        => new(metric, unit, Average(before), Average(after));

    private static decimal? Average(IEnumerable<decimal?> values)
    {
        var available = values.Where(value => value is not null).Select(value => value!.Value).ToArray();
        return available.Length == 0 ? null : available.Average();
    }

    private static bool IsAway(HomeEvent marker) => AwayCategories.Contains(marker.Category, StringComparer.OrdinalIgnoreCase);
}
