using JoakimHomeDashboard.Domain;
using JoakimHomeDashboard.Infrastructure;

namespace JoakimHomeDashboard.Tests;

public sealed class EnergyRollupTests
{
    [Fact]
    public async Task MeterReconciliation_RemovesCrossMappedImportAndExportRows()
    {
        var path=Path.Combine(Path.GetTempPath(),$"joakim-meter-map-{Guid.NewGuid():N}.db");
        try
        {
            var repository=new SqliteDashboardRepository(path, new TestSecretProtector()); await repository.InitializeAsync(); var start=DateTimeOffset.Parse("2026-06-18T00:00:00Z");
            var import=new JoakimHomeDashboard.Application.OctopusMeterPoint("A",1,"electricity",false,"IMP","S1","IMPORT","P",null,null); var export=import with { IsExport=true,MeterPoint="EXP",TariffCode="EXPORT" };
            await repository.UpsertOctopusRawReadingsAsync([new(start,start.AddMinutes(30),EnergyFlowType.ElectricityImport,1m,0.2m,"IMP","S1","IMPORT","correct-import"),new(start,start.AddMinutes(30),EnergyFlowType.ElectricityImport,9m,null,"EXP","S1","EXPORT","wrong-import"),new(start,start.AddMinutes(30),EnergyFlowType.ElectricityExport,0.5m,-0.1m,"EXP","S1","EXPORT","correct-export"),new(start,start.AddMinutes(30),EnergyFlowType.ElectricityExport,8m,null,"IMP","S1","IMPORT","wrong-export")]);
            Assert.Equal(2,await repository.DeleteReadingsOutsideConfigurationAsync([import,export])); await repository.SetSolarManualOverrideAsync(new(2026,6,18)); await repository.RebuildOctopusRollupsAsync(); var dashboard=await repository.GetEnergyDashboardAsync(); var day=Assert.Single(dashboard.Daily);
            Assert.Equal(1m,day.ImportKwh); Assert.Equal(0.5m,day.ExportKwh);
        }
        finally { if(File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task RawReadings_RebuildAccurateDailyAndMonthlyRollups()
    {
        var path = Path.Combine(Path.GetTempPath(), $"joakim-rollup-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteDashboardRepository(path, new TestSecretProtector()); await repository.InitializeAsync(); var start = new DateTimeOffset(2026, 6, 18, 0, 0, 0, TimeSpan.Zero);
            var raw = new[]
            {
                new OctopusRawReading(start,start.AddMinutes(30),EnergyFlowType.ElectricityImport,1.25m,0.30m,"IMP","I1","IMPORT-T","raw-1"),
                new OctopusRawReading(start.AddMinutes(30),start.AddHours(1),EnergyFlowType.ElectricityImport,0.75m,null,"IMP","I1","IMPORT-T","raw-2"),
                new OctopusRawReading(start,start.AddMinutes(30),EnergyFlowType.ElectricityExport,0.5m,-0.08m,"EXP","E1","EXPORT-T","raw-3"),
                new OctopusRawReading(start,start.AddHours(1),EnergyFlowType.Gas,5m,0.35m,"GAS","G1","GAS-T","raw-4")
            };
            await repository.UpsertOctopusRawReadingsAsync(raw); await repository.UpsertOctopusStandingChargesAsync([
                new(new(2026,6,18),"IMP","IMPORT-T",0.10m),
                new(new(2026,6,18),"GAS","GAS-T",0.20m)]); await repository.SetSolarManualOverrideAsync(new(2026,6,18)); await repository.RebuildOctopusRollupsAsync();
            var dashboard = await repository.GetEnergyDashboardAsync(); var day = Assert.Single(dashboard.Daily, x => x.Period == new DateOnly(2026,6,18)); var month = Assert.Single(dashboard.Monthly, x => x.Period == new DateOnly(2026,6,1));
            Assert.Equal(2m, day.ImportKwh); Assert.Equal(0.5m, day.ExportKwh); Assert.Equal(5m, day.GasKwh); Assert.Equal(day.ImportKwh, month.ImportKwh); Assert.Equal(day.GasKwh, month.GasKwh);
            Assert.Equal(0.30m, day.StandingChargeGbp); Assert.Equal(0.30m, month.StandingChargeGbp); Assert.Equal(0.35m, day.GasCost); Assert.True(day.StandingChargeExact); Assert.True(month.StandingChargeExact);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
