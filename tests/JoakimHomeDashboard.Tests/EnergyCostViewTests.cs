using JoakimHomeDashboard.Application;
using JoakimHomeDashboard.Domain;
using JoakimHomeDashboard.Infrastructure;

namespace JoakimHomeDashboard.Tests;

public sealed class EnergyCostViewTests
{
    [Fact]
    public void IntelligentGoRates_ClassifyPeakOffPeakAndUnknownFromTariffData()
    {
        var offPeak=new OctopusRate(DateTimeOffset.Parse("2026-06-18T00:00:00Z"),DateTimeOffset.Parse("2026-06-18T00:30:00Z"),7m,"E-1R-INTELLI-GO-A",OctopusProductSemantics.IntelligentGoIntervalRates);
        var peak=new OctopusRate(DateTimeOffset.Parse("2026-06-18T00:30:00Z"),DateTimeOffset.Parse("2026-06-18T01:00:00Z"),30m,"E-1R-INTELLI-GO-A",OctopusProductSemantics.IntelligentGoIntervalRates);
        var intervals=new[] { new OctopusInterval(offPeak.Start,offPeak.End,1m),new OctopusInterval(peak.Start,peak.End,1m) };
        var raw=OctopusResponseParser.ToRaw(intervals,EnergyFlowType.ElectricityImport,[offPeak,peak],1m,"IMP","I1",offPeak.TariffCode);
        Assert.Equal(ImportRateBand.OffPeak,raw[0].RateBand); Assert.Equal(ImportRateBand.Peak,raw[1].RateBand);
        Assert.Equal(0.07m,raw[0].CostGbp); Assert.Equal(0.30m,raw[1].CostGbp);
        Assert.Equal(ImportRateBand.Unknown,OctopusResponseParser.ClassifyImportRate(peak,[peak],"E-1R-STANDARD-A"));
        var secondOffPeak=offPeak with { PencePerKwh=7.5m }; var secondPeak=peak with { PencePerKwh=22.8m };
        Assert.Equal(ImportRateBand.OffPeak,OctopusResponseParser.ClassifyImportRate(secondOffPeak,[offPeak,secondOffPeak,secondPeak,peak],offPeak.TariffCode));
    }

    [Fact]
    public async Task Rollups_CalculateImportCostExportIncomeGasCostAndNet()
    {
        var path = Path.Combine(Path.GetTempPath(), $"joakim-cost-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteDashboardRepository(path, new TestSecretProtector()); await repository.InitializeAsync();
            var start = new DateTimeOffset(DateTime.SpecifyKind(DateTime.Today,DateTimeKind.Unspecified), TimeSpan.Zero);
            await repository.UpsertOctopusRawReadingsAsync([
                new(start,start.AddMinutes(30),EnergyFlowType.ElectricityImport,2m,0.60m,"IMP","I1","IMPORT-A","cost-import"),
                new(start,start.AddMinutes(30),EnergyFlowType.ElectricityExport,1m,-0.15m,"EXP","E1","EXPORT-A","cost-export"),
                new(start,start.AddHours(1),EnergyFlowType.Gas,5m,0.35m,"GAS","G1","GAS-A","cost-gas")]);
            await repository.UpsertOctopusStandingChargesAsync([
                new(DateOnly.FromDateTime(DateTime.Today),"IMP","IMPORT-A",0.10m),
                new(DateOnly.FromDateTime(DateTime.Today),"GAS","GAS-A",0.20m)]);
            await repository.SetSolarManualOverrideAsync(DateOnly.FromDateTime(DateTime.Today));
            await repository.RebuildOctopusRollupsAsync();

            var dashboard = await repository.GetEnergyDashboardAsync();
            Assert.Equal(0.60m, dashboard.TodayCost.ImportCost); Assert.Equal(0.15m, dashboard.TodayCost.ExportIncome);
            Assert.Equal(0.35m, dashboard.TodayCost.GasCost); Assert.Equal(0.30m, dashboard.TodayCost.StandingCharge); Assert.Equal(1.10m, dashboard.TodayCost.NetCost);
            var day = Assert.Single(dashboard.Daily, x => x.Period == DateOnly.FromDateTime(DateTime.Today));
            Assert.Equal(0.30m, day.StandingChargeGbp); Assert.Equal(0.35m, day.GasCost);
            Assert.True(dashboard.TodayCost.ImportExact); Assert.True(dashboard.TodayCost.ExportExact); Assert.True(dashboard.TodayCost.GasExact); Assert.True(dashboard.TodayCost.StandingExact);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void HistoricalTariffChanges_ApplyTheMatchingRateAndTariffCode()
    {
        var intervals = new[]
        {
            new OctopusInterval(DateTimeOffset.Parse("2025-12-31T23:30:00Z"),DateTimeOffset.Parse("2026-01-01T00:00:00Z"),1m),
            new OctopusInterval(DateTimeOffset.Parse("2026-01-01T00:00:00Z"),DateTimeOffset.Parse("2026-01-01T00:30:00Z"),1m)
        };
        var rates = new[]
        {
            new OctopusRate(DateTimeOffset.Parse("2025-01-01T00:00:00Z"),DateTimeOffset.Parse("2026-01-01T00:00:00Z"),20m,"IMPORT-OLD"),
            new OctopusRate(DateTimeOffset.Parse("2026-01-01T00:00:00Z"),DateTimeOffset.MaxValue,30m,"IMPORT-NEW")
        };

        var raw = OctopusResponseParser.ToRaw(intervals,EnergyFlowType.ElectricityImport,rates,1m,"IMP","I1","");
        Assert.Equal(0.20m,raw[0].CostGbp); Assert.Equal("IMPORT-OLD",raw[0].TariffCode);
        Assert.Equal(0.30m,raw[1].CostGbp); Assert.Equal("IMPORT-NEW",raw[1].TariffCode);
    }

    [Fact]
    public async Task ExistingUncostedHistory_IsDetectedForTariffBackfill()
    {
        var path = Path.Combine(Path.GetTempPath(), $"joakim-backfill-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteDashboardRepository(path, new TestSecretProtector()); await repository.InitializeAsync();
            var start = DateTimeOffset.Parse("2024-03-01T00:00:00Z");
            await repository.UpsertOctopusRawReadingsAsync([new(start,start.AddMinutes(30),EnergyFlowType.ElectricityImport,1m,null,"IMP","I1","IMPORT-OLD","uncosted")]);
            Assert.Equal(start,await repository.GetEarliestUncostedOctopusReadingAsync("IMP","I1",EnergyFlowType.ElectricityImport));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task HistoricalCostBackfill_UpdatesExistingIntervalAndPeakBreakdown()
    {
        var path=Path.Combine(Path.GetTempPath(),$"joakim-backfill-cost-{Guid.NewGuid():N}.db");
        try
        {
            var repository=new SqliteDashboardRepository(path, new TestSecretProtector()); await repository.InitializeAsync(); var start=new DateTimeOffset(DateTime.Today.Year,DateTime.Today.Month,1,0,0,0,TimeSpan.Zero);
            var id="historical-import"; await repository.UpsertOctopusRawReadingsAsync([new(start,start.AddMinutes(30),EnergyFlowType.ElectricityImport,2m,null,"IMP","I1","E-1R-INTELLI-GO-A",id)]);
            await repository.UpsertOctopusRawReadingsAsync([new(start,start.AddMinutes(30),EnergyFlowType.ElectricityImport,2m,0.60m,"IMP","I1","E-1R-INTELLI-GO-A",id,ImportRateBand.Peak,30m)]); await repository.RebuildOctopusRollupsAsync();
            var dashboard=await repository.GetEnergyDashboardAsync(); Assert.Equal(2m,dashboard.MonthImportBreakdown.PeakKwh); Assert.Equal(0.60m,dashboard.MonthImportBreakdown.PeakCost); Assert.True(dashboard.MonthImportBreakdown.PeakCostExact);
        }
        finally { if(File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task MissingCurrentTariffRate_DoesNotErasePreviouslyCostedInterval()
    {
        var path=Path.Combine(Path.GetTempPath(),$"joakim-preserve-cost-{Guid.NewGuid():N}.db");
        try
        {
            var repository=new SqliteDashboardRepository(path, new TestSecretProtector()); await repository.InitializeAsync();
            var start=new DateTimeOffset(DateTime.Today.Year,DateTime.Today.Month,1,0,0,0,TimeSpan.Zero); var id="preserved-import";
            await repository.UpsertOctopusRawReadingsAsync([new(start,start.AddMinutes(30),EnergyFlowType.ElectricityImport,1m,0.30m,"IMP","I1","E-1R-INTELLI-GO-A",id,ImportRateBand.Peak,30m)]);
            await repository.UpsertOctopusRawReadingsAsync([new(start,start.AddMinutes(30),EnergyFlowType.ElectricityImport,1m,null,"IMP","I1","E-1R-INTELLI-GO-A",id)]);
            await repository.RebuildOctopusRollupsAsync();

            var dashboard=await repository.GetEnergyDashboardAsync();
            Assert.Equal(0.30m,Assert.Single(dashboard.Daily).ImportCost);
            Assert.True(Assert.Single(dashboard.Daily).ImportCostExact);
            Assert.Equal(1m,dashboard.MonthImportBreakdown.PeakKwh);
        }
        finally { if(File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void MissingTariffCoverage_IsExplicitlyFlaggedAsPartial()
    {
        var periods = new[] { new EnergyPeriodPoint(new(2026,1,1),100m,20m,0m,25m,3m,0m,true,false,false) };
        var warnings = EnergyCostQuality.Evaluate(periods,[]);
        Assert.Contains(warnings, warning => warning.Contains("unavailable or partial",StringComparison.OrdinalIgnoreCase) && warning.Contains("Jan 2026", StringComparison.Ordinal));
    }

    [Fact]
    public void CostAnomalyWarnings_IncludeAffectedMonth()
    {
        var periods = new[] { new EnergyPeriodPoint(new(2026,8,1),0m,0m,6m,0m,0m,7m,false,false,true) };
        var warnings = EnergyCostQuality.Evaluate(periods, []);

        Assert.Contains(warnings, warning => warning.Contains("Gas unit cost exceeds £1/kWh", StringComparison.Ordinal) && warning.Contains("Aug 2026", StringComparison.Ordinal));
    }

    [Fact]
    public void TinyGasUsageWithStandingCharge_DoesNotProduceUnitCostWarning()
    {
        var periods = new[] { new EnergyPeriodPoint(new(2026,8,1),0m,0m,0.25m,0m,0m,0.31m,false,false,true) };
        var warnings = EnergyCostQuality.Evaluate(periods, []);

        Assert.DoesNotContain(warnings, warning => warning.Contains("Gas unit cost exceeds", StringComparison.OrdinalIgnoreCase));
    }


}
