using JoakimHomeDashboard.Application;
using JoakimHomeDashboard.Domain;
using JoakimHomeDashboard.Infrastructure;
using Microsoft.Data.Sqlite;

namespace JoakimHomeDashboard.Tests;

public sealed class EnergyDataQualityTests
{
    [Fact]
    public void NetExportingSolarPeriod_DoesNotProduceDirectionWarning()
    {
        var today = new DateOnly(2026, 6, 20); var daily = Enumerable.Range(1, 20).Select(day => new EnergyPeriodPoint(today.AddDays(-day), 2m, 20m, 0m)).ToArray();
        var meters = new[] { new OctopusMeterPoint("A", 1, "electricity", false, "IMP", "I", "", "", null, null), new OctopusMeterPoint("A", 1, "electricity", true, "EXP", "E", "", "", null, null) };
        var warnings = EnergyDataQuality.Evaluate(daily, meters, today);
        Assert.DoesNotContain(warnings, x => x.Contains("three times import", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(warnings, x => x.Contains("direction", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SolarCapacityBreaches_AreFlaggedWithoutUsingImportRatio()
    {
        var validation = new SolarExportValidationSummary(5.76m, 3.89m, 1, 6m, new(2026, 8, 10), 46.1m, 1, 52m, new(2026, 8, 10));
        var warnings = EnergyDataQuality.Evaluate([], [], new(2026, 8, 27), exportValidation: validation);

        Assert.Contains(warnings, warning => warning.Contains("credible half-hour export limit", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warnings, warning => warning.Contains("credible daily export limit", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(warnings, warning => warning.Contains("three times import", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OctopusCompletenessWindow_IgnoresCurrentAndPreviousCalendarDay()
    {
        var window = OctopusReportingCompleteness.RecentExpectedWindow(new(2026, 8, 27));

        Assert.Equal(new DateOnly(2026, 7, 27), window.From);
        Assert.Equal(new DateOnly(2026, 8, 26), window.ToExclusive);
        Assert.Equal(30, window.ExpectedDays);
        Assert.Contains("24-48 hour Octopus reporting grace", OctopusReportingCompleteness.IncompleteWarning(EnergyFlowType.ElectricityExport, 29, window), StringComparison.Ordinal);
    }

    [Fact]
    public void SameMpanForImportAndExport_IsFlagged()
    {
        var meters = new[] { new OctopusMeterPoint("A", 1, "electricity", false, "SAME", "I", "", "", null, null), new OctopusMeterPoint("A", 1, "electricity", true, "SAME", "E", "", "", null, null) };
        Assert.Contains(EnergyDataQuality.Evaluate([], meters, new(2026, 6, 20)), x => x.Contains("same MPAN", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExportBeforeDetectedSolarStart_IsFlagged()
    {
        var warnings=EnergyDataQuality.Evaluate([],[],new(2026,6,20),new(2025,8,1),42.5m,0);
        Assert.Contains(warnings,warning=>warning.Contains("before the automatically detected solar start",StringComparison.OrdinalIgnoreCase)&&warning.Contains("42.5",StringComparison.Ordinal));
    }

    [Fact]
    public async Task SustainedExport_DetectsSolarStartAndMarkerDoesNotControlIt()
    {
        var path=Path.Combine(Path.GetTempPath(),$"joakim-solar-validation-{Guid.NewGuid():N}.db");
        try
        {
            var repository=new JoakimHomeDashboard.Infrastructure.SqliteDashboardRepository(path, new TestSecretProtector()); await repository.InitializeAsync();
            var meter=new OctopusMeterPoint("A",1,"electricity",true,"EXP","E1","E-1R-OUTGOING-A","OUTGOING",null,null); await repository.ReplaceOctopusConfigurationAsync(new("A",1,[meter],[]));
            var before=DateTimeOffset.Parse("2026-01-01T00:00:00Z"); var after=DateTimeOffset.Parse("2026-02-01T00:00:00Z");
            var readings=new List<OctopusRawReading> { new(before,before.AddMinutes(30),EnergyFlowType.ElectricityExport,10m,-1m,"EXP","E1",meter.TariffCode,"isolated-before") };
            readings.AddRange(Enumerable.Range(0,5).Select(day=>new OctopusRawReading(after.AddDays(day),after.AddDays(day).AddMinutes(30),EnergyFlowType.ElectricityExport,2m,-0.2m,"EXP","E1",meter.TariffCode,$"sustained-{day}")));
            await repository.UpsertOctopusRawReadingsAsync(readings);
            await repository.SaveHomeEventAsync(new(0,new(2025,1,15),"Solar note only","Solar")); await repository.RebuildOctopusRollupsAsync(); var dashboard=await repository.GetEnergyDashboardAsync(); var configuration=await repository.GetSolarAnalysisConfigurationAsync();
            Assert.Equal(new DateOnly(2026,2,1),configuration.DetectedStartDate); Assert.Equal(10m,dashboard.YearExportKwh); Assert.Contains(dashboard.Warnings,warning=>warning.Contains("Ignored 10.0 kWh",StringComparison.Ordinal));
        }
        finally { if(File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task PlausibleHighExportLowImportPeriod_RemainsInTrustedRollups()
    {
        var path = Path.Combine(Path.GetTempPath(), $"joakim-net-export-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteDashboardRepository(path, new TestSecretProtector()); await repository.InitializeAsync();
            var importMeter = new OctopusMeterPoint("A", 1, "electricity", false, "IMP", "I1", "E-1R-INTELLI-GO-A", "INTELLI-GO", null, null);
            var exportMeter = new OctopusMeterPoint("A", 1, "electricity", true, "EXP", "E1", "E-1R-OUTGOING-A", "OUTGOING", null, null);
            await repository.ReplaceOctopusConfigurationAsync(new("A", 1, [importMeter, exportMeter], []));
            await repository.SetSettingAsync("energy.solarCapacityKwp", "5.76");

            var start = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
            var readings = new List<OctopusRawReading>();
            var exportPerInterval = 560.3m / (30m * 48m);
            for (var day = 0; day < 30; day++)
            {
                var date = start.AddDays(day);
                readings.Add(new(date, date.AddMinutes(30), EnergyFlowType.ElectricityImport, 0.61m, 0.1525m, "IMP", "I1", importMeter.TariffCode, $"import-{day}"));
                for (var interval = 0; interval < 48; interval++)
                {
                    var intervalStart = date.AddMinutes(interval * 30);
                    readings.Add(new(intervalStart, intervalStart.AddMinutes(30), EnergyFlowType.ElectricityExport, exportPerInterval, -exportPerInterval * 0.15m, "EXP", "E1", exportMeter.TariffCode, $"export-{day}-{interval}"));
                }
            }

            await repository.UpsertOctopusRawReadingsAsync(readings);
            await repository.RebuildOctopusRollupsAsync();
            var dashboard = await repository.GetEnergyDashboardAsync();

            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = """
                SELECT
                  COALESCE(SUM(CASE WHEN flow_type=0 THEN quantity_kwh ELSE 0 END),0),
                  COALESCE(SUM(CASE WHEN flow_type=1 THEN quantity_kwh ELSE 0 END),0)
                FROM energy_records
                WHERE source='Octopus' AND period_start>='2026-08-01' AND period_start<'2026-08-31'
                """;
            await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
            var import = reader.GetDecimal(0); var export = reader.GetDecimal(1);
            Assert.Equal(18.30m, import);
            Assert.InRange(export, 560.29m, 560.31m);
            Assert.True(export > import * 3m);
            var august = Assert.Single(dashboard.Monthly, point => point.Period == new DateOnly(2026, 8, 1));
            Assert.Equal(18.30m, august.ImportKwh);
            Assert.InRange(august.ExportKwh, 560.29m, 560.31m);
            Assert.InRange(august.ImportKwh - august.ExportKwh, -542.01m, -541.99m);
            Assert.InRange(august.ExportIncome, 84.04m, 84.05m);
            Assert.InRange(dashboard.Daily.Sum(point => point.ExportKwh), 560.29m, 560.31m);
            Assert.InRange(EnergyInsightBuilder.BuildMonthly(dashboard.Monthly).Single().ExportIncomeGbp, 84.04m, 84.05m);
            Assert.Equal(-542.00m, Math.Round(YearAnalysisBuilder.BuildSeries(EnergyInsightBuilder.BuildMonthly(dashboard.Monthly), YearAnalysisMetric.NetGridUsage, [2026]).Single().Months[7] ?? 0, 2));
            Assert.DoesNotContain(dashboard.Warnings, warning => warning.Contains("three times import", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(dashboard.Warnings, warning => warning.Contains("untrusted", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(dashboard.Warnings, warning => warning.Contains("credible daily export limit", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(dashboard.Warnings, warning => warning.Contains("credible half-hour export limit", StringComparison.OrdinalIgnoreCase));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task ImpossibleIndividualExportReading_IsFlaggedAndExcludedFromTrustedRollups()
    {
        var path = Path.Combine(Path.GetTempPath(), $"joakim-impossible-export-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteDashboardRepository(path, new TestSecretProtector()); await repository.InitializeAsync();
            var exportMeter = new OctopusMeterPoint("A", 1, "electricity", true, "EXP", "E1", "EXPORT", "P", null, null);
            await repository.ReplaceOctopusConfigurationAsync(new("A", 1, [exportMeter], []));
            await repository.SetSettingAsync("energy.solarCapacityKwp", "5.76");
            await repository.SetSolarManualOverrideAsync(new(2026, 8, 1));
            var start = DateTimeOffset.Parse("2026-08-10T12:00:00Z");
            await repository.UpsertOctopusRawReadingsAsync([
                new(start, start.AddMinutes(30), EnergyFlowType.ElectricityExport, 1m, -0.15m, "EXP", "E1", exportMeter.TariffCode, "valid-export"),
                new(start.AddMinutes(30), start.AddHours(1), EnergyFlowType.ElectricityExport, 10m, -1.50m, "EXP", "E1", exportMeter.TariffCode, "impossible-export")]);
            await repository.RebuildOctopusRollupsAsync();

            var dashboard = await repository.GetEnergyDashboardAsync();
            var day = Assert.Single(dashboard.Daily, point => point.Period == new DateOnly(2026, 8, 10));
            Assert.Equal(1m, day.ExportKwh);
            Assert.Equal(0.15m, day.ExportIncome);
            Assert.Contains(dashboard.Warnings, warning => warning.Contains("credible half-hour export limit", StringComparison.OrdinalIgnoreCase) && warning.Contains("10 Aug 2026", StringComparison.Ordinal) && warning.Contains("excluded from trusted export totals", StringComparison.OrdinalIgnoreCase));
            Assert.Single(await repository.GetOctopusIntervalsAsync(new(2026, 8, 10)));

            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM octopus_raw_readings WHERE external_id IN ('valid-export','impossible-export')";
            Assert.Equal(2, Convert.ToInt32(await command.ExecuteScalarAsync()));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task RecentCompletenessWarning_AppliesOctopusReportingGrace()
    {
        var path = Path.Combine(Path.GetTempPath(), $"joakim-reporting-grace-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteDashboardRepository(path, new TestSecretProtector()); await repository.InitializeAsync();
            var today = DateOnly.FromDateTime(DateTime.Now);
            var window = OctopusReportingCompleteness.RecentExpectedWindow(today);
            var importMeter = new OctopusMeterPoint("A", 1, "electricity", false, "IMP", "I1", "IMPORT", "P", null, null);
            var exportMeter = new OctopusMeterPoint("A", 1, "electricity", true, "EXP", "E1", "EXPORT", "P", null, null);
            await repository.ReplaceOctopusConfigurationAsync(new("A", 1, [importMeter, exportMeter], []));
            var readings = new List<OctopusRawReading>();
            for (var date = window.From; date < window.ToExclusive; date = date.AddDays(1))
            {
                var start = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                readings.Add(new(start, start.AddMinutes(30), EnergyFlowType.ElectricityImport, 0.25m, null, "IMP", "I1", importMeter.TariffCode, $"import-{date:yyyyMMdd}"));
                readings.Add(new(start, start.AddMinutes(30), EnergyFlowType.ElectricityExport, 0.5m, null, "EXP", "E1", exportMeter.TariffCode, $"export-{date:yyyyMMdd}"));
            }

            await repository.UpsertOctopusRawReadingsAsync(readings);
            await repository.RebuildOctopusRollupsAsync();

            var dashboard = await repository.GetEnergyDashboardAsync();

            Assert.DoesNotContain(dashboard.Warnings, warning => warning.Contains("data incomplete", StringComparison.OrdinalIgnoreCase));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task OlderMissingDay_StillRaisesCompletenessWarning()
    {
        var path = Path.Combine(Path.GetTempPath(), $"joakim-older-gap-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteDashboardRepository(path, new TestSecretProtector()); await repository.InitializeAsync();
            var today = DateOnly.FromDateTime(DateTime.Now);
            var window = OctopusReportingCompleteness.RecentExpectedWindow(today);
            var missingDate = window.From.AddDays(10);
            var importMeter = new OctopusMeterPoint("A", 1, "electricity", false, "IMP", "I1", "IMPORT", "P", null, null);
            await repository.ReplaceOctopusConfigurationAsync(new("A", 1, [importMeter], []));
            var readings = new List<OctopusRawReading>();
            for (var date = window.From; date < window.ToExclusive; date = date.AddDays(1))
            {
                if (date == missingDate) continue;
                var start = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                readings.Add(new(start, start.AddMinutes(30), EnergyFlowType.ElectricityImport, 0.25m, null, "IMP", "I1", importMeter.TariffCode, $"import-{date:yyyyMMdd}"));
            }

            await repository.UpsertOctopusRawReadingsAsync(readings);
            await repository.RebuildOctopusRollupsAsync();

            var dashboard = await repository.GetEnergyDashboardAsync();

            Assert.Contains(dashboard.Warnings, warning => warning.Contains("ElectricityImport data incomplete: 29/30 expected days present", StringComparison.Ordinal) && warning.Contains("24-48 hour Octopus reporting grace", StringComparison.Ordinal));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
