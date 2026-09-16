namespace JoakimHomeDashboard.Domain;

public enum AccountType { Cash, Savings, Mortgage, Loan, Investment, Pension }
public enum EnergyFlowType { ElectricityImport, ElectricityExport, Gas, SolarGeneration, BatteryCharge, BatteryDischarge, SiteConsumption }
public enum EnergyViewMode { Energy, Cost, Both }
public enum ImportRateBand { Unknown, Peak, OffPeak }
public enum DataQualitySeverity { Information, Warning, Critical }
public enum SolarDetectionConfidence { None, Medium, High }
public enum AnalysisChartType { Line, Columns, Table }
public enum CostColumnMode { StackedBreakdown, NetCost }
public enum YearAnalysisMetric { ImportCost, NetElectricityCost, ImportUsage, ExportUsage, NetGridUsage, GasUsage, GasCost, CombinedUtilityCost }

public sealed record Account(long Id, string Name, AccountType Type, string Provider, string Currency, decimal Balance, bool IsActive = true);
public sealed record EnergyRecord(long Id, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, EnergyFlowType FlowType, decimal QuantityKwh, decimal CostGbp, string Source);
public sealed record InvestmentPosition(long Id, long AccountId, string Symbol, string Name, decimal Quantity, decimal UnitPrice, decimal CostBasis, DateOnly AsOfDate);
public sealed record PensionValue(long Id, long AccountId, DateOnly AsOfDate, decimal Value, decimal EmployeeContribution, decimal EmployerContribution);
public sealed record RsuGrant(long Id, string Company, DateOnly GrantDate, int TotalShares, decimal TaxRate, string Currency);
public sealed record RsuVesting(long Id, long GrantId, DateOnly VestDate, int Shares, decimal SharePrice);
public sealed record PropertyValue(long Id, string Name, DateOnly AsOfDate, decimal Value, decimal MortgageBalance);
public sealed record NetWorthSnapshot(long Id, DateOnly AsOfDate, decimal Assets, decimal Liabilities);

public sealed record DashboardSnapshot(
    decimal TotalCash, decimal Investments, decimal Pensions, decimal PropertyEquity, decimal Debt,
    decimal NetWorth, decimal MonthSpending, decimal MonthSavings,
    decimal TodayImportKwh, decimal MonthImportKwh, decimal MonthExportKwh, decimal MonthSolarKwh, decimal MonthSiteConsumptionKwh, decimal MonthGasKwh, decimal MonthExportIncome,
    decimal EstimatedBill, decimal EstimatedBillWithoutSolar, decimal SolarSavings, decimal AnnualSolarSavings,
    decimal SolarPaybackPercent, decimal TodayGasKwh, decimal ElectricityImportCost, decimal GasCost, decimal TotalEnergyCost, decimal NetEnergyCost,
    IReadOnlyList<ChartPoint> EnergyHistory, IReadOnlyList<RsuEvent> UpcomingVesting);

public sealed record ChartPoint(DateOnly Date, decimal ImportKwh, decimal SolarKwh, decimal ExportKwh, decimal GasKwh, decimal SiteConsumptionKwh);
public sealed record RsuEvent(DateOnly Date, int Shares, decimal GrossValue, decimal NetValue);
public sealed record DailyEnergyReading(DateOnly Date, EnergyFlowType FlowType, decimal QuantityKwh, decimal CostGbp, string Source, string ExternalId, decimal StandingChargeGbp = 0, string TariffCode = "", bool CostAvailable = false);
public sealed record ProviderSyncStatus(string Source, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, bool? Succeeded, int RecordsImported, string Message, DateTimeOffset? LastSuccessfulAt, DateTimeOffset? RangeFrom, DateTimeOffset? RangeTo);
public sealed record OctopusRawReading(DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, EnergyFlowType FlowType, decimal QuantityKwh, decimal? CostGbp, string MeterPoint, string MeterSerial, string TariffCode, string ExternalId, ImportRateBand RateBand = ImportRateBand.Unknown, decimal? UnitRatePence = null, int RateBandVersion = 1);
public sealed record OctopusStandingCharge(DateOnly Date, string MeterPoint, string TariffCode, decimal CostGbp);
public sealed record EnergyPeriodPoint(DateOnly Period, decimal ImportKwh, decimal ExportKwh, decimal GasKwh, decimal ImportCost = 0, decimal ExportIncome = 0, decimal GasCost = 0, bool ImportCostExact = false, bool ExportIncomeExact = false, bool GasCostExact = false,
    decimal PeakImportKwh = 0, decimal OffPeakImportKwh = 0, decimal UnknownImportKwh = 0, decimal PeakImportCost = 0, decimal OffPeakImportCost = 0, bool PeakCostExact = false, bool OffPeakCostExact = false);
public sealed record ImportRateBreakdown(decimal PeakKwh, decimal OffPeakKwh, decimal UnknownKwh, decimal PeakCost, decimal OffPeakCost, bool PeakCostExact, bool OffPeakCostExact);
public sealed record EnergyCostPeriod(decimal ImportCost, decimal ExportIncome, decimal GasCost, bool ImportExact, bool ExportExact, bool GasExact)
{
    public decimal NetCost => ImportCost + GasCost - ExportIncome;
    public bool NetExact => ImportExact && ExportExact && GasExact;
}
public sealed record EnergyDashboardSnapshot(
    decimal TodayImportKwh, decimal TodayExportKwh, decimal TodayGasKwh,
    decimal MonthImportKwh, decimal MonthExportKwh, decimal MonthGasKwh,
    decimal YearImportKwh, decimal YearExportKwh, decimal YearGasKwh,
    DateTimeOffset? DataFrom, DateTimeOffset? DataTo, ProviderSyncStatus? SyncStatus,
    IReadOnlyList<EnergyPeriodPoint> Daily, IReadOnlyList<EnergyPeriodPoint> Monthly, IReadOnlyList<string> Warnings,
    EnergyCostPeriod TodayCost, EnergyCostPeriod MonthCost, EnergyCostPeriod YearCost,
    ImportRateBreakdown MonthImportBreakdown, ImportRateBreakdown YearImportBreakdown)
{
    public IReadOnlyList<SupplierMonthSummary> SupplierAllocationMonths { get; init; } = [];
}
public sealed record SupplierBandTotal(string Band, decimal Kwh, decimal GrossGbp);
public sealed record SupplierMonthSummary(string Month, int Intervals, int Reconciled, decimal MeterKwh, decimal AllocatedKwh,
    decimal GrossGbp, bool Complete, IReadOnlyList<SupplierBandTotal> Bands)
{
    public decimal? WeightedRatePence => Complete && MeterKwh > 0 ? GrossGbp * 100m / MeterKwh : null;
}
public sealed record HomeEvent(long Id, DateOnly Date, string Label, string Category);
public sealed record EnergyAnalysisPeriod(DateOnly? From, DateOnly? To)
{
    public bool HasData => From is not null && To is not null && From <= To;
}
public sealed record HomeEventAnalysisBoundary(HomeEvent Marker, EnergyAnalysisPeriod Baseline, EnergyAnalysisPeriod Analysis);
public sealed record ChartHoverPoint(DateOnly Date, string SeriesName, decimal Value, string Unit, ImportRateBand RateBand = ImportRateBand.Unknown);
public sealed record EnergyIntervalDetail(
    DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, EnergyFlowType FlowType,
    decimal QuantityKwh, decimal? CostGbp, ImportRateBand RateBand,
    decimal? UnitRatePence, string TariffCode);
public sealed record EnergySpike(DateOnly Date, EnergyFlowType FlowType, decimal ValueKwh, decimal RollingAverageKwh, decimal Multiple);
public sealed record DataQualityGroup(string Title, DataQualitySeverity Severity, IReadOnlyList<string> Warnings)
{
    public int Count => Warnings.Count;
}
public sealed record MonthlyEnergyInsight(
    DateOnly Month, decimal ImportKwh, decimal ExportKwh, decimal GasKwh,
    decimal PeakImportKwh, decimal OffPeakImportKwh,
    decimal ImportCostGbp, decimal ExportIncomeGbp, decimal GasCostGbp,
    decimal PeakImportCostGbp, decimal OffPeakImportCostGbp,
    bool ImportCostExact, bool ExportIncomeExact, bool GasCostExact,
    bool PeakCostExact, bool OffPeakCostExact,
    decimal SolarGenerationKwh = 0, decimal SelfConsumptionKwh = 0,
    decimal BatteryChargeKwh = 0, decimal BatteryDischargeKwh = 0)
{
    public decimal ExportChartKwh => -ExportKwh;
    public decimal NetGridKwh => ImportKwh - ExportKwh;
    public decimal ExportIncomeChartGbp => -ExportIncomeGbp;
    public decimal NetElectricityCostGbp => ImportCostGbp - ExportIncomeGbp;
    public bool NetElectricityCostExact => ImportCostExact && (ExportKwh == 0 || ExportIncomeExact);
    public decimal CombinedUtilityCostGbp => NetElectricityCostGbp + GasCostGbp;
    public bool CombinedUtilityCostExact => NetElectricityCostExact && (GasKwh == 0 || GasCostExact);
}
public sealed record DailyExportObservation(DateOnly Date, decimal ExportKwh);
public sealed record SolarDetectionResult(DateOnly? StartDate, SolarDetectionConfidence Confidence, string Method, int SupportingDays, int WindowDays);
public sealed record SolarExportValidationSummary(
    decimal CapacityKwp, decimal HalfHourlyLimitKwh, int HalfHourlyBreachCount, decimal? LargestHalfHourlyKwh, DateOnly? LargestHalfHourlyDate,
    decimal DailyLimitKwh, int DailyBreachCount, decimal? LargestDailyKwh, DateOnly? LargestDailyDate)
{
    public IReadOnlyList<DateOnly> HalfHourlyBreachDates { get; init; } = [];
    public IReadOnlyList<DateOnly> DailyBreachDates { get; init; } = [];
}
public sealed record SolarAnalysisConfiguration(DateOnly? DetectedStartDate, SolarDetectionConfidence Confidence, string DetectionMethod, DateOnly? ManualOverrideDate, DateTimeOffset? DetectedAt)
{
    public DateOnly? EffectiveStartDate => ManualOverrideDate ?? DetectedStartDate;
    public bool IsManualOverride => ManualOverrideDate is not null;
}
public sealed record AnalysisMetricComparison(string Metric, string Unit, decimal? Before, decimal? After)
{
    public decimal? Difference => Before is not null && After is not null ? After - Before : null;
    public decimal? PercentageChange => Before is not null && Before != 0 && After is not null ? (After - Before) / Math.Abs(Before.Value) * 100m : null;
}
public sealed record SameMonthEnergyComparison(
    DateOnly BeforeMonth, DateOnly AfterMonth,
    decimal ImportChangeKwh, decimal ExportChangeKwh, decimal NetGridChangeKwh,
    decimal? NetElectricityCostChangeGbp, decimal? CombinedUtilityCostChangeGbp);
public sealed record SolarBaselineAnalysis(
    SolarAnalysisConfiguration Configuration, EnergyAnalysisPeriod BaselinePeriod, EnergyAnalysisPeriod SolarPeriod,
    IReadOnlyList<AnalysisMetricComparison> Metrics, IReadOnlyList<SameMonthEnergyComparison> SameMonthComparisons,
    IReadOnlyList<DateOnly> ExcludedAwayMonths);
public sealed record MarkerImpactAnalysis(HomeEvent Marker, EnergyAnalysisPeriod BeforePeriod, EnergyAnalysisPeriod AfterPeriod, IReadOnlyList<AnalysisMetricComparison> Metrics);
public sealed record EnergyProjectFoundation(long Id, string Name, string Category, decimal? ProjectCost, DateOnly StartDate, decimal AccumulatedBenefit, long? SourceEventId);
public sealed record YearAnalysisSeries(string Source, YearAnalysisMetric Metric, string Label, string Unit, int Year, IReadOnlyList<decimal?> Months);
public sealed record MonthlyCostColumnPoint(
    DateOnly Month, decimal? PeakImportCostGbp, decimal? OffPeakImportCostGbp,
    decimal? ExportIncomeChartGbp, decimal? NetElectricityCostGbp);
public sealed record CostColumnSegment(DateOnly Month, string SeriesName, decimal Value, decimal ValueBase)
{
    public decimal ValueEnd => ValueBase + Value;
}
public sealed record YearAnalysisValue(int Year, decimal? Value);
public sealed record YearAnalysisTableRow(int Month, IReadOnlyList<YearAnalysisValue> Values, decimal? Difference, decimal? PercentageDifference);
public sealed record AnnualAnalysisValue(decimal? Total, decimal? MonthlyAverage, string Unit);
public sealed record YearEnergySummary(
    int Year, AnnualAnalysisValue ElectricityImport, AnnualAnalysisValue ElectricityExport, AnnualAnalysisValue NetGridUsage,
    AnnualAnalysisValue ElectricityCost, AnnualAnalysisValue ExportIncome, AnnualAnalysisValue NetElectricityCost,
    AnnualAnalysisValue GasCost, AnnualAnalysisValue CombinedUtilityCost);
