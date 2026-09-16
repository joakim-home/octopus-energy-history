using JoakimHomeDashboard.Domain;
using JoakimHomeDashboard.Infrastructure;

namespace JoakimHomeDashboard.Tests;

public sealed class ProviderParsingTests
{
    [Fact]
    public void OctopusConsumption_GroupsHalfHourlyValuesAndAppliesRate()
    {
        const string json = """{"next":null,"results":[{"consumption":0.5,"interval_start":"2026-06-18T00:00:00+01:00","interval_end":"2026-06-18T00:30:00+01:00"},{"consumption":0.25,"interval_start":"2026-06-18T00:30:00+01:00","interval_end":"2026-06-18T01:00:00+01:00"}]}""";
        var page = OctopusResponseParser.ParseConsumptionPage(json);
        var rate = new OctopusRate(DateTimeOffset.Parse("2026-06-17T23:00:00Z"), DateTimeOffset.Parse("2026-06-18T01:00:00Z"), 24m);
        var daily = OctopusResponseParser.ToDaily(page.Intervals, EnergyFlowType.ElectricityImport, [rate]);
        Assert.Single(daily); Assert.Equal(0.75m, daily[0].QuantityKwh); Assert.Equal(0.18m, daily[0].CostGbp);
    }

    [Fact]
    public void OctopusExportParsing_PreservesFlowAndExportIncomeSign()
    {
        var interval = new OctopusInterval(DateTimeOffset.Parse("2026-06-18T12:00:00Z"), DateTimeOffset.Parse("2026-06-18T12:30:00Z"), 0.75m);
        var rate = new OctopusRate(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), DateTimeOffset.MaxValue, -15m);
        var raw = OctopusResponseParser.ToRaw([interval], EnergyFlowType.ElectricityExport, [rate], 1m, "export-mpan", "export-serial", "E-1R-OUTGOING-26-A");
        Assert.Single(raw); Assert.Equal(EnergyFlowType.ElectricityExport, raw[0].FlowType); Assert.Equal(0.75m, raw[0].QuantityKwh); Assert.Equal(-0.1125m, raw[0].CostGbp);
    }

    [Fact]
    public void OctopusDiscovery_ReportsAllMissingMeterTypes()
    {
        const string json = """{"properties":[{"id":1,"moved_out_at":null,"electricity_meter_points":[],"gas_meter_points":[]}]}""";
        var result = OctopusResponseParser.ParseAccountDiscovery(json, "A-EMPTY");
        Assert.Empty(result.MeterPoints); Assert.Contains(result.Warnings, x => x.Contains("gas meter", StringComparison.OrdinalIgnoreCase)); Assert.Contains(result.Warnings, x => x.Contains("export meter", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OctopusGas_ConvertsCubicMetresAndAddsDailyStandingCharge()
    {
        var intervals = new[] { new OctopusInterval(DateTimeOffset.Parse("2026-06-18T00:00:00Z"), DateTimeOffset.Parse("2026-06-18T00:30:00Z"), 1m) };
        var usage = new[] { new OctopusRate(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), DateTimeOffset.MaxValue, 7m) };
        var standing = new[] { new OctopusRate(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), DateTimeOffset.MaxValue, 30m) };
        var daily = OctopusResponseParser.ToDaily(intervals, EnergyFlowType.Gas, usage, standing, 11.1868m, "G-1R-FLEX-26-A");
        Assert.Single(daily); Assert.Equal(11.187m, daily[0].QuantityKwh); Assert.Equal(0.30m, daily[0].StandingChargeGbp);
        Assert.Equal(1.0831m, daily[0].CostGbp); Assert.Equal("G-1R-FLEX-26-A", daily[0].TariffCode);
    }

    [Fact]
    public void OctopusAccount_DiscoversActiveGasMeterAndProduct()
    {
        const string json = """{"properties":[{"gas_meter_points":[{"mprn":"1234567890","meters":[{"serial_number":"GAS123"}],"agreements":[{"tariff_code":"G-1R-FLEX-26-01-A","valid_to":null}]}]}]}""";
        var gas = OctopusResponseParser.ParseGasDiscovery(json);
        Assert.Equal("1234567890", gas.Mprn); Assert.Equal("GAS123", gas.MeterSerial); Assert.Equal("FLEX-26-01", gas.ProductCode); Assert.Equal("G-1R-FLEX-26-01-A", gas.TariffCode);
    }

    [Fact]
    public void OctopusAccount_PreservesMultiplePropertiesAndGasMeters()
    {
        const string json = """{"properties":[{"id":10,"moved_out_at":null,"electricity_meter_points":[],"gas_meter_points":[{"mprn":"111","meters":[{"serial_number":"G1"},{"serial_number":"G2"}],"agreements":[{"tariff_code":"G-1R-FLEX-26-01-A","valid_from":"2025-01-01T00:00:00Z","valid_to":null}]}]},{"id":20,"moved_out_at":null,"electricity_meter_points":[{"mpan":"222","is_export":true,"meters":[{"serial_number":"E1"}],"agreements":[{"tariff_code":"E-1R-OUTGOING-26-01-A","valid_from":"2025-01-01T00:00:00Z","valid_to":null}]}],"gas_meter_points":[]}]}""";
        var result = OctopusResponseParser.ParseAccountDiscovery(json, "A-TEST");
        Assert.Equal(2, result.PropertyCount); Assert.Equal(3, result.MeterPoints.Count); Assert.Equal(2, result.MeterPoints.Count(x => x.FuelType == "gas"));
        Assert.Contains(result.Warnings, x => x.Contains("Multiple active properties", StringComparison.Ordinal));
    }

    [Fact]
    public void OctopusDiscovery_MapsIsExportFlagWithoutSwappingDirections()
    {
        const string json = """{"properties":[{"id":1,"moved_out_at":null,"electricity_meter_points":[{"mpan":"IMPORT","is_export":false,"meters":[{"serial_number":"I1"}],"agreements":[]},{"mpan":"EXPORT","is_export":true,"meters":[{"serial_number":"E1"}],"agreements":[]}],"gas_meter_points":[]}]}""";
        var meters = OctopusResponseParser.ParseAccountDiscovery(json, "A-MAP").MeterPoints;
        Assert.False(Assert.Single(meters, x => x.MeterPoint == "IMPORT").IsExport); Assert.True(Assert.Single(meters, x => x.MeterPoint == "EXPORT").IsExport);
    }

    [Fact]
    public void OctopusDiscovery_PreservesHistoricalTariffAgreementPeriods()
    {
        const string json = """{"properties":[{"id":1,"moved_out_at":null,"electricity_meter_points":[{"mpan":"IMPORT","is_export":false,"meters":[{"serial_number":"I1"}],"agreements":[{"tariff_code":"E-1R-FLEX-24-A","valid_from":"2024-01-01T00:00:00Z","valid_to":"2025-01-01T00:00:00Z"},{"tariff_code":"E-1R-FLEX-25-A","valid_from":"2025-01-01T00:00:00Z","valid_to":null}]}],"gas_meter_points":[]}]}""";
        var result = OctopusResponseParser.ParseAccountDiscovery(json,"A-HISTORY");
        Assert.NotNull(result.TariffPeriods); Assert.Equal(2,result.TariffPeriods!.Count);
        Assert.Equal("E-1R-FLEX-24-A",result.TariffPeriods[0].TariffCode); Assert.Equal(new DateTimeOffset(2025,1,1,0,0,0,TimeSpan.Zero),result.TariffPeriods[0].ValidTo);
    }

    [Fact]
    public void EnphaseSystems_ParsesDiscoveredSystemId()
    {
        const string json = """{"systems":[{"system_id":1234567,"name":"Home Solar","timezone":"Europe/London"}]}""";
        var systems = EnphaseResponseParser.ParseSystems(json);
        Assert.Single(systems); Assert.Equal("1234567", systems[0].SystemId); Assert.Equal("Europe/London", systems[0].Timezone);
    }

    [Fact]
    public void EnphaseTelemetry_NormalizesIntervalsToDailyKwh()
    {
        const string json = """{"system_id":1234567,"intervals":[{"end_at":1781737200,"wh_del":500},{"end_at":1781739000,"wh_del":750}]}""";
        var readings = EnphaseResponseParser.ParseTelemetry(json, EnergyFlowType.SolarGeneration, TimeZoneInfo.Utc);
        Assert.Single(readings); Assert.Equal(1.25m, readings[0].QuantityKwh); Assert.Equal("Enphase", readings[0].Source);
    }
}
