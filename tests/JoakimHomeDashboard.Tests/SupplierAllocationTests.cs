using System.Text.Json;
using JoakimHomeDashboard.Application;
using JoakimHomeDashboard.Domain;
using JoakimHomeDashboard.Infrastructure;
using Microsoft.Data.Sqlite;

namespace JoakimHomeDashboard.Tests;

public sealed class SupplierAllocationTests
{
    private static readonly DateTimeOffset Boundary=DateTimeOffset.Parse("2026-08-26T00:00:00+01:00");
    private static readonly OctopusTariffPeriod Tariff=new("A",1,"electricity",false,"IMP","FOUR","FOUR",Boundary,null);
    private static SupplierAllocation Band(DateTimeOffset start,string band,decimal quantity,decimal rate)
        => new(start,start.AddMinutes(30),band,quantity,rate,quantity*rate,quantity*rate,quantity*rate*1.05m,0.05m,"exclusive_verified_rest");

    [Theory]
    [InlineData(20,20,21,"exclusive_verified_rest",20,21)]
    [InlineData(21,20,21,"inclusive_verified_rest",20,21)]
    [InlineData(20,20,24,"exclusive_verified_rest",20,24)]
    public void TaxBasisIsVerified_NotAssumedOrAppliedTwice(decimal applied,decimal netRate,decimal grossRate,string basis,decimal expectedNet,decimal expectedGross)
    {
        var parsed=SupplierAllocationImporter.Parse(Response(applied),[new("ECO7_DAY",Boundary,Boundary.AddDays(1),netRate,grossRate)]);
        var row=Assert.Single(parsed); Assert.Equal(basis,row.TaxBasis); Assert.Equal(expectedNet,row.NetPence); Assert.Equal(expectedGross,row.GrossPence);
        Assert.Equal(grossRate/netRate-1,row.VatRate);
    }

    [Fact]
    public void TaxEvidenceMustCoverIntervalAndMatchAppliedRate()
    {
        Assert.Throws<InvalidOperationException>(()=>SupplierAllocationImporter.Parse(Response(22),[new("ECO7_DAY",Boundary,Boundary.AddDays(1),20,21)]));
        Assert.Throws<InvalidOperationException>(()=>SupplierAllocationImporter.Parse(Response(20),[new("ECO7_DAY",Boundary.AddMinutes(15),Boundary.AddDays(1),20,21)]));
        Assert.Throws<InvalidOperationException>(()=>SupplierAllocationImporter.Parse(Response(20),[]));
    }

    [Fact]
    public void RejectsPartialGraphqlErrors_UnknownCurrency_AndWrongPeriodTotals()
    {
        Assert.Throws<InvalidOperationException>(()=>SupplierAllocationImporter.Parse("{\"errors\":[{\"message\":\"partial\"}],\"data\":{}}",[]));
        var rates=new[]{new SupplierTaxRate("ECO7_DAY",Boundary,Boundary.AddDays(1),20,21)};
        Assert.Throws<InvalidOperationException>(()=>SupplierAllocationImporter.Parse(Response(20).Replace("GBP_PENCE","GBP"),rates));
        Assert.Throws<InvalidOperationException>(()=>SupplierAllocationImporter.Parse(Response(20).Replace("\"totalConsumption\":1","\"totalConsumption\":2"),rates));
    }

    [Fact]
    public void ReconciliationUsesUtcInstantsAndRejectsExtraMissingOrDuplicateAllocations()
    {
        var rows=new[]{new AllocationReading(1,Boundary,Boundary.AddMinutes(30),1,"2026-08-26")};
        var a=Band(Boundary.ToUniversalTime(),"ECO7_NIGHT",1,7);
        Assert.Equal("reconciled",Assert.Single(SupplierAllocationStore.Reconcile(rows,[a])).Status);
        Assert.Equal("missing_allocation",Assert.Single(SupplierAllocationStore.Reconcile(rows,[])).Status);
        Assert.Equal("quantity_mismatch",Assert.Single(SupplierAllocationStore.Reconcile(rows,[a with {Quantity=2}])).Status);
        Assert.Equal("duplicate_band",Assert.Single(SupplierAllocationStore.Reconcile(rows,[a,a])).Status);
        Assert.Equal("allocation_without_meter",Assert.Single(SupplierAllocationStore.Reconcile(rows,[a,Band(Boundary.AddMinutes(30),"ECO7_NIGHT",1,7)])).Status);
    }

    [Fact]
    public async Task BoundaryDashboardUsesAllocations_LegacyUnchanged_AndRawRowsAreIdentical()
    {
        using var f=await Fixture.Create();
        await f.Repository.UpsertOctopusRawReadingsAsync([
            new(Boundary.AddMinutes(-30),Boundary,EnergyFlowType.ElectricityImport,2,0.5m,"IMP","M1","OLD","old",ImportRateBand.Peak,25),
            new(Boundary,Boundary.AddMinutes(30),EnergyFlowType.ElectricityImport,4,1.2m,"IMP","M1","PRESERVED","four",ImportRateBand.Peak,30),
            new(Boundary,Boundary.AddMinutes(30),EnergyFlowType.Gas,2,0.1m,"GAS","G1","G","gas")]);
        var before=await f.Raw(); var rows=await f.Store.ReadingsAsync(Tariff);
        Assert.Single(rows);
        await f.Store.SaveAsync(Tariff,rows,[Band(Boundary,"ECO7_DAY",1,20),Band(Boundary,"EV_DEVICE_OFF_PEAK",3,5)],"supplier response");
        await f.Repository.RebuildOctopusRollupsAsync();
        var dashboard=await f.Repository.GetEnergyDashboardAsync();
        var month=Assert.Single(dashboard.Monthly);
        Assert.Equal(6,month.ImportKwh); Assert.Equal(0.8675m,month.ImportCost,12); Assert.Equal(3,month.PeakImportKwh); Assert.Equal(3,month.OffPeakImportKwh);
        var supplier=Assert.Single(dashboard.SupplierAllocationMonths); Assert.True(supplier.Complete); Assert.Equal(9.1875m,supplier.WeightedRatePence);
        Assert.Equal(before,await f.Raw());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrDiscrepantFourRateAllocationNeverFallsBackToPreservedPrice(bool discrepancy)
    {
        using var f=await Fixture.Create();
        await f.Repository.UpsertOctopusRawReadingsAsync([new(Boundary,Boundary.AddMinutes(30),EnergyFlowType.ElectricityImport,4,1.2m,"IMP","M1","OLD","four",ImportRateBand.Peak,30)]);
        var rows=await f.Store.ReadingsAsync(Tariff);
        if(discrepancy) await f.Store.SaveAsync(Tariff,rows,[Band(Boundary,"ECO7_DAY",3,20)],"mismatch");
        await f.Repository.RebuildOctopusRollupsAsync();
        var dashboard=await f.Repository.GetEnergyDashboardAsync(); var month=Assert.Single(dashboard.Monthly);
        Assert.False(month.ImportCostExact); Assert.Equal(4,month.UnknownImportKwh); Assert.False(Assert.Single(dashboard.SupplierAllocationMonths).Complete);
        Assert.Null(Assert.Single(await f.Repository.GetOctopusIntervalsAsync(new(2026,8,26))).CostGbp);
    }

    [Fact]
    public async Task RevisionsAreRetained_Idempotent_RejectionSupersedesOldGoodPricing()
    {
        using var f=await Fixture.Create();
        await f.Repository.UpsertOctopusRawReadingsAsync([new(Boundary,Boundary.AddMinutes(30),EnergyFlowType.ElectricityImport,1,null,"IMP","M1","FOUR","four")]);
        var rows=await f.Store.ReadingsAsync(Tariff); var a=new[]{Band(Boundary,"ECO7_DAY",1,20)};
        await f.Store.SaveAsync(Tariff,rows,a,"good"); await f.Store.SaveAsync(Tariff,rows,a,"good");
        Assert.Equal(1,await f.Count("octopus_allocation_revisions"));
        await f.Store.SaveAsync(Tariff,rows,[],"empty");
        Assert.Equal(2,await f.Count("octopus_allocation_revisions")); Assert.False(Assert.Single(await f.Store.AuditAsync(true)).Complete);
        await f.Store.SaveAsync(Tariff,rows,a,"good");
        Assert.Equal(3,await f.Count("octopus_allocation_revisions")); Assert.True(Assert.Single(await f.Store.AuditAsync(true)).Complete);
    }

    [Fact]
    public async Task DailyAndMonthlyReconciliationCrossesMonthBoundaryWithoutMovingReadings()
    {
        using var f=await Fixture.Create(); var start=DateTimeOffset.Parse("2026-09-01T00:00:00+01:00");
        await f.Repository.UpsertOctopusRawReadingsAsync([new(start.AddMinutes(-30),start,EnergyFlowType.ElectricityImport,1,null,"IMP","M1","FOUR","a"),new(start,start.AddMinutes(30),EnergyFlowType.ElectricityImport,2,null,"IMP","M1","FOUR","b")]);
        var rows=await f.Store.ReadingsAsync(Tariff); await f.Store.SaveAsync(Tariff,rows,[Band(start.AddMinutes(-30),"ECO7_NIGHT",1,7),Band(start,"EV_DEVICE_OFF_PEAK",2,7)],"two months");
        var days=await f.Store.AuditAsync(false); var months=await f.Store.AuditAsync(true);
        Assert.Equal(2,days.Count); Assert.Equal(2,months.Count); Assert.All(days,d=>Assert.True(d.Complete)); Assert.All(months,m=>Assert.True(m.Complete));
        Assert.Equal(1,months[0].MeterKwh); Assert.Equal(2,months[1].MeterKwh);
    }

    [Fact]
    public async Task IncrementalSyncRetriesOldGapsAndThreeDayOverlapWithoutRewritingRawRows()
    {
        using var f = await Fixture.Create();
        for (var day = 0; day < 7; day++)
        {
            var start = Boundary.AddDays(day);
            await f.Repository.UpsertOctopusRawReadingsAsync([new(start, start.AddMinutes(30), EnergyFlowType.ElectricityImport, 1, null, "IMP", "M1", "FOUR", $"day-{day}")]);
        }
        var all = await f.Store.ReadingsAsync(Tariff);
        foreach (var row in all.Where(r => r.Start != Boundary.AddDays(1)))
            await f.Store.SaveAsync(Tariff, [row], [Band(row.Start, "ECO7_DAY", 1, 20)], row.LocalDate);
        var before = await f.Raw();
        var selected = await f.Store.IncrementalReadingsAsync(all);
        Assert.Equal(new[] { 1, 4, 5, 6 }, selected.Select(r => (r.Start - Boundary).Days));
        Assert.Equal(before, await f.Raw());
        Assert.Empty(await f.Store.IncrementalReadingsAsync([]));
    }

    [Fact]
    public void SmallIntervalDifferencesCannotAccumulateIntoAnAcceptedDailyMismatch()
    {
        var rows = Enumerable.Range(0, 3).Select(i => new AllocationReading(i + 1, Boundary.AddMinutes(i * 30), Boundary.AddMinutes((i + 1) * 30), 1, "2026-08-26")).ToArray();
        var allocations = rows.Select(r => Band(r.Start, "ECO7_DAY", 1.0000005m, 20)).ToArray();
        Assert.All(SupplierAllocationStore.Reconcile(rows, allocations), c => Assert.Equal("aggregate_mismatch", c.Status));
        Assert.False(new AllocationAudit("2026-08", 3, 3, 3, 3.0000015m, 1).Complete);
    }

    [Fact]
    public async Task MissingFinalDayLaterBackfills_ClearsWarningsAndRestoresCosts_WithoutChangingAugustOrRawRows()
    {
        using var f = await Fixture.Create();
        var september = Boundary.AddDays(6);
        var starts = new[] { Boundary, september, september.AddDays(1), september.AddDays(1).AddMinutes(30) };
        await f.Repository.UpsertOctopusRawReadingsAsync(starts.Select((start, i) => new OctopusRawReading(
            start, start.AddMinutes(30), EnergyFlowType.ElectricityImport, i + 1, 99m, "IMP", "M1", "FOUR", $"partial-{i}", ImportRateBand.Peak, 99)).ToArray());
        var rawBefore = await f.Raw();
        var all = await f.Store.ReadingsAsync(Tariff);
        await f.Store.SaveAsync(Tariff, [all[0]], [Band(all[0].Start, "ECO7_DAY", 1, 20)], "august");
        var septemberRows = all.Skip(1).ToArray();
        await f.Store.SaveAsync(Tariff, septemberRows, [Band(september, "ECO7_NIGHT", 2, 7)], "partial");
        await f.Repository.RebuildOctopusRollupsAsync();
        var partial = await f.Repository.GetEnergyDashboardAsync();
        var augustBefore = partial.Monthly.Single(m => m.Period.Month == 8);
        var septemberBefore = partial.Monthly.Single(m => m.Period.Month == 9);
        Assert.False(septemberBefore.ImportCostExact);
        Assert.Equal(7m, septemberBefore.UnknownImportKwh);
        Assert.False(partial.SupplierAllocationMonths.Single(m => m.Month == "2026-09").Complete);
        Assert.Contains(partial.Warnings, w => w.Contains("supplier allocation pending", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(partial.Warnings, w => w.Contains("exact Octopus tariff coverage is incomplete", StringComparison.OrdinalIgnoreCase));
        Assert.All((await f.Repository.GetOctopusIntervalsAsync(new(2026, 9, 2))), r => Assert.Null(r.CostGbp));

        var full = septemberRows.Select(r => Band(r.Start, "ECO7_NIGHT", r.Quantity, 7)).ToArray();
        await f.Store.SaveAsync(Tariff, septemberRows, full, "complete");
        await f.Store.SaveAsync(Tariff, septemberRows, full, "complete");
        await f.Repository.RebuildOctopusRollupsAsync();
        var repaired = await f.Repository.GetEnergyDashboardAsync();
        var septemberAfter = repaired.Monthly.Single(m => m.Period.Month == 9);
        Assert.True(septemberAfter.ImportCostExact);
        Assert.True(septemberAfter.OffPeakCostExact);
        Assert.True(repaired.SupplierAllocationMonths.Single(m => m.Month == "2026-09").Complete);
        Assert.Equal(0m, septemberAfter.UnknownImportKwh);
        Assert.Equal(9m, septemberAfter.OffPeakImportKwh);
        Assert.Equal(0.6615m, septemberAfter.ImportCost);
        Assert.DoesNotContain(repaired.Warnings, w => w.Contains("supplier allocation", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(repaired.Warnings, w => w.Contains("exact Octopus tariff coverage is incomplete", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(augustBefore, repaired.Monthly.Single(m => m.Period.Month == 8));
        Assert.All(await f.Store.AuditAsync(false), a => Assert.True(a.Complete));
        Assert.All(await f.Store.AuditAsync(true), a => Assert.Equal(a.MeterKwh, a.AllocatedKwh));
        Assert.Equal(rawBefore, await f.Raw());
        Assert.Equal(3, await f.Count("octopus_allocation_revisions"));
    }

    [Fact]
    public async Task AgreementSelectionUsesHalfOpenBoundsAndEquivalentUtcInstants()
    {
        using var f = await Fixture.Create();
        var end = Boundary.AddHours(1);
        foreach (var start in new[] { Boundary.AddMinutes(-30), Boundary, Boundary.AddMinutes(30), end })
            await f.Repository.UpsertOctopusRawReadingsAsync([new(start, start.AddMinutes(30), EnergyFlowType.ElectricityImport, 1, null, "IMP", "M1", "FOUR", start.ToString("O"))]);
        var bounded = Tariff with { ValidFrom = Boundary.ToUniversalTime(), ValidTo = end.ToUniversalTime() };
        var selected = await f.Store.ReadingsAsync(bounded);
        Assert.Equal(new[] { Boundary, Boundary.AddMinutes(30) }, selected.Select(r => r.Start));
        Assert.Equal("2026-08-25T23:00:00.0000000+00:00", selected.Min(r => r.Start).ToUniversalTime().ToString("O"));
        Assert.Equal(end.ToUniversalTime(), selected.Max(r => r.End).ToUniversalTime());
        Assert.All(selected, r => Assert.Equal("2026-08-26", r.LocalDate));
    }

    [Fact]
    public async Task PendingSupplierWarningDoesNotHideGenuineGasTariffGapOnSameDay()
    {
        using var f = await Fixture.Create();
        await f.Repository.UpsertOctopusRawReadingsAsync([
            new(Boundary, Boundary.AddMinutes(30), EnergyFlowType.ElectricityImport, 1, 99m, "IMP", "M1", "FOUR", "import"),
            new(Boundary, Boundary.AddMinutes(30), EnergyFlowType.Gas, 2, null, "GAS", "G1", "GAS", "gas")]);
        await f.Repository.RebuildOctopusRollupsAsync();
        var dashboard = await f.Repository.GetEnergyDashboardAsync();
        Assert.Contains(dashboard.Warnings, w => w.Contains("supplier allocation pending", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(dashboard.Warnings, w => w.Contains("exact Octopus tariff coverage is incomplete", StringComparison.OrdinalIgnoreCase));
        var rows = await f.Store.ReadingsAsync(Tariff);
        await f.Store.SaveAsync(Tariff, rows, [Band(Boundary, "ECO7_DAY", 1, 20)], "complete");
        await f.Repository.RebuildOctopusRollupsAsync();
        dashboard = await f.Repository.GetEnergyDashboardAsync();
        Assert.DoesNotContain(dashboard.Warnings, w => w.Contains("supplier allocation pending", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(dashboard.Warnings, w => w.Contains("exact Octopus tariff coverage is incomplete", StringComparison.OrdinalIgnoreCase));
        Assert.True(Assert.Single(dashboard.Monthly).ImportCostExact);
        Assert.False(Assert.Single(dashboard.Monthly).GasCostExact);
    }

    [Fact]
    public void EmptySuccessfulSupplierResponseProvidesNoPricing()
    {
        var allocations = SupplierAllocationImporter.Parse("{\"data\":{\"gbrCostOfUsage\":{\"periods\":[]}}}", []);
        var check = Assert.Single(SupplierAllocationStore.Reconcile([new(1, Boundary, Boundary.AddMinutes(30), 1, "2026-08-26")], allocations));
        Assert.Equal("missing_allocation", check.Status);
    }

    [Fact]
    public void StandingChargePeriodsAreNotEnergyAllocations()
    {
        var json="{\"data\":{\"gbrCostOfUsage\":{\"periods\":[{\"consumptionUnit\":\"day\"}]}}}";
        Assert.Empty(SupplierAllocationImporter.Parse(json,[]));
    }

    private static string Response(decimal rate)=>JsonSerializer.Serialize(new {data=new {gbrCostOfUsage=new {periods=new[]{new {totalConsumption=1,totalCost=rate,consumptionUnit="kilowatt_hour",currency="GBP_PENCE",intervals=new[]{new {period=new {start=Boundary,end=Boundary.AddMinutes(30)},consumption=1,consumptionUnit="kilowatt_hour",currency="GBP_PENCE",rateApplied=rate,cost=rate,bandSubcategory="ECO7_DAY"}}}}}}});
    [Fact]
    public async Task ConfigurableOverlapRetainsUnresolvedMiddleDayOutsideWindow()
    {
        using var f = await Fixture.Create();
        foreach (var day in new[] { 0, 10, 20, 28, 29, 30 })
        {
            var start = Boundary.AddDays(day);
            await f.Repository.UpsertOctopusRawReadingsAsync([new(start, start.AddMinutes(30), EnergyFlowType.ElectricityImport, 1, 99m, "IMP", "M1", "FOUR", start.ToString("O"))]);
        }
        var all = await f.Store.ReadingsAsync(Tariff);
        foreach (var row in all.Where(r => r.Start != Boundary.AddDays(10)))
            await f.Store.SaveAsync(Tariff, [row], [Band(row.Start, "ECO7_DAY", 1, 20)], row.Start.ToString("O"));
        var oneDay = await f.Store.IncrementalReadingsAsync(all, overlapDays: 1);
        Assert.Contains(oneDay, r => r.Start == Boundary.AddDays(10));
        Assert.DoesNotContain(oneDay, r => r.Start == Boundary.AddDays(20));
        var wide = await f.Store.IncrementalReadingsAsync(all, overlapDays: 30);
        Assert.Contains(wide, r => r.Start == Boundary.AddDays(20));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => f.Store.IncrementalReadingsAsync(all, overlapDays: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => f.Store.IncrementalReadingsAsync(all, overlapDays: 31));
    }

    [Fact]
    public async Task PersistedHealthRequiresAllHistoricalIntervalsAndPreservesLastGoodMarker()
    {
        using var f = await Fixture.Create();
        foreach (var start in new[] { Boundary, Boundary.AddDays(40) })
            await f.Repository.UpsertOctopusRawReadingsAsync([new(start, start.AddMinutes(30), EnergyFlowType.ElectricityImport, 1, 99m, "IMP", "M1", "FOUR", start.ToString("O"))]);
        var rows = await f.Store.ReadingsAsync(Tariff);
        await f.Store.SaveAsync(Tariff, [rows[1]], [Band(rows[1].Start, "ECO7_DAY", 1, 20)], "latest complete");
        await f.Store.CaptureHealthAsync("running");
        await f.Store.CaptureHealthAsync("pending");
        var pending = await f.Store.GetHealthAsync();
        Assert.Equal(2, pending.ExpectedIntervals);
        Assert.Equal(1, pending.ReconciledIntervals);
        Assert.Equal(1m, pending.ClassifiedKwh);
        Assert.Equal(1m, pending.UnclassifiedKwh);
        Assert.Single(pending.MissingTimestamps);
        Assert.Null(pending.LastFullyReconciledAt);
        await f.Store.SaveAsync(Tariff, [rows[0]], [Band(rows[0].Start, "ECO7_DAY", 1, 20)], "old complete");
        await f.Store.CaptureHealthAsync("complete");
        var healthy = await f.Store.GetHealthAsync();
        Assert.NotNull(healthy.LastFullyReconciledAt);
        Assert.Equal(0m, healthy.UnclassifiedKwh);
        Assert.Empty(healthy.MissingTimestamps);
        await f.Store.CaptureHealthAsync("running");
        await f.Store.CaptureHealthAsync("failed", CancellationToken.None);
        var failed = await f.Store.GetHealthAsync();
        Assert.Equal(healthy.LastFullyReconciledAt, failed.LastFullyReconciledAt);
        Assert.Equal("failed", failed.RetryState);
    }

    private sealed class Fixture:IDisposable
    {
        private readonly string path=Path.Combine(Path.GetTempPath(),$"supplier-allocation-{Guid.NewGuid():N}.db");
        public SqliteDashboardRepository Repository {get;}
        public SupplierAllocationStore Store {get;}
        private Fixture(){Repository=new(path);Store=new(path);}
        public static async Task<Fixture> Create(){var f=new Fixture();await f.Repository.InitializeAsync();await f.Store.RegisterAsync(Tariff,"{}");return f;}
        public async Task<string> Raw(){using var c=new SqliteConnection($"Data Source={path};Pooling=False");await c.OpenAsync();using var cmd=c.CreateCommand();cmd.CommandText="SELECT * FROM octopus_raw_readings ORDER BY id";using var r=await cmd.ExecuteReaderAsync();var rows=new List<object[]>();while(await r.ReadAsync()){var row=new object[r.FieldCount];r.GetValues(row);rows.Add(row);}return JsonSerializer.Serialize(rows);}
        public async Task<long> Count(string table){using var c=new SqliteConnection($"Data Source={path};Pooling=False");await c.OpenAsync();using var cmd=c.CreateCommand();cmd.CommandText=$"SELECT COUNT(*) FROM {table}";return (long)(await cmd.ExecuteScalarAsync())!;}
        public void Dispose()=>File.Delete(path);
    }
}
