using JoakimHomeDashboard.Domain;

namespace JoakimHomeDashboard.Application;

public static class EnergyDataQuality
{
    public static IReadOnlyList<string> Evaluate(IReadOnlyCollection<EnergyPeriodPoint> daily, IReadOnlyCollection<OctopusMeterPoint> meters, DateOnly today, DateOnly? solarStartDate = null, decimal preSolarExportKwh = 0, int invalidExportMeterReadings = 0, bool solarStartIsManual = false, SolarExportValidationSummary? exportValidation = null)
    {
        var warnings = new List<string>();
        var imports = meters.Where(x => x.FuelType == "electricity" && !x.IsExport).Select(x => x.MeterPoint).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var exports = meters.Where(x => x.FuelType == "electricity" && x.IsExport).Select(x => x.MeterPoint).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (imports.Overlaps(exports)) warnings.Add("Import and export share the same MPAN. Check Octopus discovery or Advanced Overrides; direction may be inconsistent.");
        if (daily.Any(x => x.ImportKwh < 0 || x.ExportKwh < 0 || x.GasKwh < 0)) warnings.Add("Negative usage was found. Raw readings should be reviewed before relying on totals.");
        var recent = daily.Where(x => x.Period >= today.AddDays(-30) && x.Period < today).ToArray(); var import = recent.Sum(x => x.ImportKwh); var export = recent.Sum(x => x.ExportKwh);
        if (export > 10m && import == 0) warnings.Add("Recent export readings exist but no recent import readings were found. This can be valid for a net-exporting solar home; check import meter sync completeness before comparing import totals.");
        if (exportValidation is { HalfHourlyBreachCount: > 0 })
        {
            var date = exportValidation.LargestHalfHourlyDate is null ? "an unknown date" : exportValidation.LargestHalfHourlyDate.Value.ToString("dd MMM yyyy");
            warnings.Add($"{exportValidation.HalfHourlyBreachCount:N0} export interval reading(s) are malformed or exceed the credible half-hour export limit of {exportValidation.HalfHourlyLimitKwh:N2} kWh for a {exportValidation.CapacityKwp:N2} kWp solar array. Largest interval was {exportValidation.LargestHalfHourlyKwh:N2} kWh on {date}; affected date(s): {Dates(exportValidation.HalfHourlyBreachDates)}. These readings are excluded from trusted export totals while raw supplier history is preserved.");
        }
        if (exportValidation is { DailyBreachCount: > 0 })
        {
            var date = exportValidation.LargestDailyDate is null ? "an unknown date" : exportValidation.LargestDailyDate.Value.ToString("dd MMM yyyy");
            warnings.Add($"{exportValidation.DailyBreachCount:N0} export day(s) exceed the credible daily export limit of {exportValidation.DailyLimitKwh:N1} kWh for a {exportValidation.CapacityKwp:N2} kWp solar array. Largest day was {exportValidation.LargestDailyKwh:N1} kWh on {date}; affected date(s): {Dates(exportValidation.DailyBreachDates)}. These days are excluded from trusted export totals while raw supplier history is preserved.");
        }
        if (solarStartDate is not null && preSolarExportKwh > 0.1m) warnings.Add($"Ignored {preSolarExportKwh:N1} kWh of export recorded before the {(solarStartIsManual ? "manual" : "automatically detected")} solar start ({solarStartDate:dd MMM yyyy}). Raw supplier readings are preserved for review.");
        if (invalidExportMeterReadings > 0) warnings.Add($"{invalidExportMeterReadings:N0} export reading(s) do not match a currently discovered export meter and were excluded from trusted totals.");
        return warnings;

        static string Dates(IReadOnlyList<DateOnly> dates)
            => dates.Count == 0 ? "unknown" : string.Join(", ", dates.Order().Take(8).Select(value => value.ToString("dd MMM yyyy"))) + (dates.Count > 8 ? $", +{dates.Count - 8:N0} more" : "");
    }
}

public readonly record struct EnergyCompletenessWindow(DateOnly From, DateOnly ToExclusive)
{
    public int ExpectedDays => Math.Max(0, ToExclusive.DayNumber - From.DayNumber);
    public string DisplayRange => ExpectedDays == 0 ? "no expected days" : $"{From:dd MMM yyyy} to {ToExclusive.AddDays(-1):dd MMM yyyy}";
}

public static class OctopusReportingCompleteness
{
    public const int RecentWindowDays = 30;
    public const int ReportingGraceCalendarDays = 2;

    public static EnergyCompletenessWindow RecentExpectedWindow(DateOnly today)
    {
        var toExclusive = today.AddDays(1 - ReportingGraceCalendarDays);
        return new(toExclusive.AddDays(-RecentWindowDays), toExclusive);
    }

    public static string IncompleteWarning(EnergyFlowType flow, int availableDays, EnergyCompletenessWindow window)
        => $"{flow} data incomplete: {availableDays:N0}/{window.ExpectedDays:N0} expected days present after applying the 24-48 hour Octopus reporting grace period ({window.DisplayRange}).";
}

public static class SolarExportCapability
{
    public const decimal IntervalTolerance = 1.35m;
    public const decimal CredibleDailyKwhPerKwp = 8m;

    public static decimal IntervalLimitKwh(decimal capacityKwp, decimal hours)
        => capacityKwp * Math.Max(hours, 0.5m) * IntervalTolerance;

    public static decimal HalfHourlyLimitKwh(decimal capacityKwp) => IntervalLimitKwh(capacityKwp, 0.5m);
    public static decimal DailyLimitKwh(decimal capacityKwp) => capacityKwp * CredibleDailyKwhPerKwp;
}

public static class EnergyCostQuality
{
    private const decimal MinimumGasKwhForUnitCostCheck = 5m;

    public static IReadOnlyList<string> Evaluate(IReadOnlyCollection<EnergyPeriodPoint> periods, IReadOnlyCollection<OctopusTariffPeriod> tariffs, string periodFormat = "MMM yyyy", IReadOnlySet<DateOnly>? supplierAllocationDays = null)
    {
        var warnings = new List<string>();
        var invalidSigns = periods.Where(x => x.ImportCost < 0 || x.GasCost < 0 || x.ExportIncome < 0).ToArray();
        if (invalidSigns.Length > 0) warnings.Add($"A calculated cost has an invalid sign in {Periods(invalidSigns, periodFormat)}. Check tariff direction and raw rates.");
        var highImportCosts = periods.Where(x => x.ImportCostExact && x.ImportKwh > 0 && x.ImportCost / x.ImportKwh > 2m).ToArray();
        if (highImportCosts.Length > 0) warnings.Add($"Electricity import unit cost exceeds £2/kWh in {Periods(highImportCosts, periodFormat)} and appears suspicious.");
        var highGasCosts = periods.Where(x => x.GasCostExact && x.GasKwh >= MinimumGasKwhForUnitCostCheck && x.GasCost / x.GasKwh > 1m).ToArray();
        if (highGasCosts.Length > 0) warnings.Add($"Gas unit cost exceeds £1/kWh in {Periods(highGasCosts, periodFormat)} and appears suspicious.");
        var missingCosts = periods.Where(x => x.ImportKwh > 0 && !x.ImportCostExact && supplierAllocationDays?.Contains(x.Period) != true || x.ExportKwh > 0 && !x.ExportIncomeExact || x.GasKwh > 0 && !x.GasCostExact).ToArray();
        if (missingCosts.Length > 0) warnings.Add($"Cost periods are unavailable or partial for {Periods(missingCosts, periodFormat)} because exact Octopus tariff coverage is incomplete.");
        foreach (var meter in tariffs.GroupBy(x => new { x.MeterPoint, x.FuelType, x.IsExport }))
        {
            var ordered = meter.OrderBy(x => x.ValidFrom).ToArray();
            for (var index = 1; index < ordered.Length; index++) if (ordered[index - 1].ValidTo is not null && ordered[index].ValidFrom is not null && ordered[index].ValidFrom < ordered[index - 1].ValidTo) { warnings.Add($"Overlapping tariff periods found for meter point {meter.Key.MeterPoint}."); break; }
        }
        return warnings;

        static string Periods(IReadOnlyCollection<EnergyPeriodPoint> values, string format)
            => string.Join(", ", values.OrderBy(value => value.Period).Take(8).Select(value => value.Period.ToString(format))) + (values.Count > 8 ? $", +{values.Count - 8:N0} more" : "");
    }
}

public static class EnergySpikeDetector
{
    public static IReadOnlyList<EnergySpike> Detect(IReadOnlyCollection<EnergyPeriodPoint> daily, int rollingDays = 30, decimal threshold = 3m, int minimumBaselineDays = 1)
    {
        if (rollingDays < 1) throw new ArgumentOutOfRangeException(nameof(rollingDays));
        if (threshold <= 0) throw new ArgumentOutOfRangeException(nameof(threshold));
        var ordered = daily.OrderBy(point => point.Period).ToArray();
        var spikes = new List<EnergySpike>();
        for (var index = 0; index < ordered.Length; index++)
        {
            var from = ordered[index].Period.AddDays(-rollingDays);
            var baseline = ordered.Take(index).Where(point => point.Period >= from).ToArray();
            if (baseline.Length < minimumBaselineDays) continue;
            AddIfSpike(EnergyFlowType.ElectricityImport, ordered[index].ImportKwh, baseline.Average(point => point.ImportKwh));
            AddIfSpike(EnergyFlowType.ElectricityExport, ordered[index].ExportKwh, baseline.Average(point => point.ExportKwh));

            void AddIfSpike(EnergyFlowType flow, decimal value, decimal average)
            {
                if (average <= 0 || value <= average * threshold) return;
                spikes.Add(new(ordered[index].Period, flow, value, average, value / average));
            }
        }
        return spikes;
    }
}

public static class DataQualityGrouping
{
    public static IReadOnlyList<DataQualityGroup> Group(IEnumerable<string> warnings)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["Missing tariff coverage"] = [],
            ["Supplier allocation pending or unavailable"] = [],
            ["Cost anomalies"] = [],
            ["Missing days"] = [],
            ["Sync failures"] = [],
            ["Export capability checks"] = [],
            ["Untrusted export readings"] = [],
            ["Partial data"] = []
        };
        foreach (var warning in warnings.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
        {
            var title = Classify(warning);
            var explanation = Explain(title, warning);
            if (!groups[title].Contains(explanation, StringComparer.Ordinal)) groups[title].Add(explanation);
        }
        return groups.Where(pair => pair.Value.Count > 0).Select(pair => new DataQualityGroup(pair.Key, Severity(pair.Key), pair.Value)).ToArray();
    }

    private static string Classify(string warning)
    {
        if (warning.Contains("supplier allocation", StringComparison.OrdinalIgnoreCase)) return "Supplier allocation pending or unavailable";
        if (warning.Contains("unit cost", StringComparison.OrdinalIgnoreCase) || warning.Contains("invalid sign", StringComparison.OrdinalIgnoreCase)) return "Cost anomalies";
        if (warning.Contains("tariff", StringComparison.OrdinalIgnoreCase) || warning.Contains("cost", StringComparison.OrdinalIgnoreCase) || warning.Contains("rate", StringComparison.OrdinalIgnoreCase)) return "Missing tariff coverage";
        if (warning.Contains("credible", StringComparison.OrdinalIgnoreCase) || warning.Contains("solar array", StringComparison.OrdinalIgnoreCase) || warning.Contains("physically", StringComparison.OrdinalIgnoreCase)) return "Export capability checks";
        if (warning.Contains("sync", StringComparison.OrdinalIgnoreCase) || warning.Contains("failed", StringComparison.OrdinalIgnoreCase) || warning.Contains("provider", StringComparison.OrdinalIgnoreCase)) return "Sync failures";
        if (warning.Contains("incomplete", StringComparison.OrdinalIgnoreCase) || warning.Contains("missing", StringComparison.OrdinalIgnoreCase) || warning.Contains("no readings", StringComparison.OrdinalIgnoreCase) || warning.Contains("no recent import", StringComparison.OrdinalIgnoreCase)) return "Missing days";
        if (warning.Contains("export", StringComparison.OrdinalIgnoreCase) || warning.Contains("MPAN", StringComparison.OrdinalIgnoreCase) || warning.Contains("direction", StringComparison.OrdinalIgnoreCase)) return "Untrusted export readings";
        return "Partial data";
    }

    private static DataQualitySeverity Severity(string title) => title switch
    {
        "Sync failures" => DataQualitySeverity.Critical,
        "Export capability checks" or "Untrusted export readings" or "Missing tariff coverage" or "Supplier allocation pending or unavailable" or "Cost anomalies" or "Missing days" => DataQualitySeverity.Warning,
        _ => DataQualitySeverity.Information
    };

    private static string Explain(string title, string warning) => title switch
    {
        "Missing tariff coverage" => $"{warning} Usage history remains available; available readings are not estimated.",
        "Cost anomalies" => $"{warning} Usage history remains available; review the affected month before relying on cost comparisons.",
        "Missing days" => $"{warning} Totals and comparisons for the affected period may be incomplete; available readings remain visible.",
        "Sync failures" => $"{warning} Previously synced local history remains available; retry sync to retrieve newer readings.",
        "Export capability checks" => $"{warning} This check uses configured solar capacity, not the export/import ratio; normal net-exporting solar periods remain included.",
        "Untrusted export readings" => $"{warning} Only readings that fail meter identity or solar-start validation are excluded from trusted export and net calculations; normal net-exporting solar periods remain included.",
        _ => $"{warning} Review the affected period before comparing trends; unaffected readings remain available."
    };
}
