using System.Net;
using System.Text;
using JoakimHomeDashboard.Application;
using JoakimHomeDashboard.Domain;
using JoakimHomeDashboard.Infrastructure;

namespace JoakimHomeDashboard.Tests;

public sealed class OctopusDiscoveryTests
{
    [Fact]
    public async Task ApiKeyOnlyDiscovery_ResolvesAccountAndPersistsAllServices()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), $"joakim-discovery-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteDashboardRepository(path, new TestSecretProtector()); await repository.InitializeAsync(); await repository.SetSettingAsync("octopus.apiKey", "fixture-key", true);
            using var client = new HttpClient(new DiscoveryHandler()); var connector = new OctopusEnergyDataSource(repository, repository, repository, client);
            var result = await connector.DiscoverAccountAsync(); var stored = await repository.GetOctopusMeterPointsAsync();
            Assert.Equal("ACCOUNT-DEMO", result.AccountNumber); Assert.Equal(4, result.MeterPoints.Count); Assert.Equal(4, stored.Count);
            Assert.Contains(stored, x => x.FuelType == "electricity" && !x.IsExport && x.MeterPoint == "MPAN-DEMO-IMPORT");
            Assert.Contains(stored, x => x.FuelType == "electricity" && x.IsExport); Assert.Equal(2, stored.Count(x => x.FuelType == "gas"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task UpgradedDatabase_RediscoversTariffHistoryAndIgnoresStaleSameMeterOverride()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), $"joakim-upgrade-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteDashboardRepository(path, new TestSecretProtector()); await repository.InitializeAsync();
            await repository.SetSettingAsync("octopus.apiKey","fixture-key",true); await repository.SetSettingAsync("octopus.accountCode","ACCOUNT-UPGRADE-DEMO");
            await repository.SetSettingAsync("octopus.discoveredAccount","ACCOUNT-UPGRADE-DEMO");
            await repository.SetSettingAsync("octopus.mpan","MPAN-DEMO-IMPORT"); await repository.SetSettingAsync("octopus.meterSerial","IMP1");
            await repository.SetSettingAsync("octopus.productCode","STALE-PRODUCT"); await repository.SetSettingAsync("octopus.tariffCode","E-1R-STALE-PRODUCT-A");
            var legacyMeter = new OctopusMeterPoint("ACCOUNT-UPGRADE-DEMO",1,"electricity",false,"MPAN-DEMO-IMPORT","IMP1","E-1R-STALE-PRODUCT-A","STALE-PRODUCT",null,null);
            await repository.ReplaceOctopusConfigurationAsync(new("ACCOUNT-UPGRADE-DEMO",1,[legacyMeter],[]));
            var handler=new UpgradeHandler(); using var client=new HttpClient(handler); var connector=new OctopusEnergyDataSource(repository,repository,repository,client);

            var result=await connector.SyncAsync(CancellationToken.None); var dashboard=await repository.GetEnergyDashboardAsync();
            Assert.True(result.Succeeded,result.Message); Assert.Single(await repository.GetOctopusTariffPeriodsAsync());
            Assert.Contains(handler.Paths,path=>path.Contains("INTELLI-VAR-25-01",StringComparison.Ordinal)); Assert.True(Assert.Single(dashboard.Daily).ImportCostExact);
        }
        finally { if(File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task DualRegisterHistoricalTariff_DoesNotSendInvalidStandardRateRequestOrFailSync()
    {
        if(!OperatingSystem.IsWindows()) return; var path=Path.Combine(Path.GetTempPath(),$"joakim-dual-rate-{Guid.NewGuid():N}.db");
        try
        {
            var repository=new SqliteDashboardRepository(path, new TestSecretProtector()); await repository.InitializeAsync(); await repository.SetSettingAsync("octopus.apiKey","fixture-key",true);
            var meter=new OctopusMeterPoint("A",1,"electricity",false,"IMP","I1","E-1R-INTELLI-VAR-A","INTELLI-VAR",new(2025,1,1,0,0,0,TimeSpan.Zero),null);
            var periods=new[] { new OctopusTariffPeriod("A",1,"electricity",false,"IMP","E-2R-VAR-A","VAR",new(2024,1,1,0,0,0,TimeSpan.Zero),new(2024,3,1,0,0,0,TimeSpan.Zero)),new OctopusTariffPeriod("A",1,"electricity",false,"IMP",meter.TariffCode,meter.ProductCode,meter.ValidFrom,null) };
            await repository.ReplaceOctopusConfigurationAsync(new("A",1,[meter],[],periods)); var handler=new DualRateHandler(); using var client=new HttpClient(handler); var result=await new OctopusEnergyDataSource(repository,repository,repository,client).SyncAsync(CancellationToken.None);
            Assert.True(result.Succeeded,result.Message);
            Assert.DoesNotContain(handler.Paths,path=>path.Contains("E-2R",StringComparison.OrdinalIgnoreCase)&&path.Contains("standard-unit-rates",StringComparison.OrdinalIgnoreCase));
            Assert.Contains(handler.Paths,path=>path.Contains("E-2R",StringComparison.OrdinalIgnoreCase)&&path.Contains("standing-charges",StringComparison.OrdinalIgnoreCase));
            Assert.Contains(handler.Paths,path=>path.Contains("INTELLI",StringComparison.OrdinalIgnoreCase));
            Assert.True(Assert.Single((await repository.GetEnergyDashboardAsync()).Daily).ImportCostExact);
        }
        finally { if(File.Exists(path)) File.Delete(path); }
    }

    private sealed class DiscoveryHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/graphql/", StringComparison.Ordinal) == true)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var json = body.Contains("obtainKrakenToken", StringComparison.Ordinal)
                    ? """{"data":{"obtainKrakenToken":{"token":"fixture-jwt"}}}"""
                    : """{"data":{"viewer":{"accounts":[{"number":"ACCOUNT-DEMO"}]}}}""";
                return Ok(json);
            }
            if (request.RequestUri?.AbsolutePath == "/v1/accounts/ACCOUNT-DEMO/")
                return Ok("""{"number":"ACCOUNT-DEMO","properties":[{"id":10,"moved_out_at":null,"electricity_meter_points":[{"mpan":"MPAN-DEMO-IMPORT","is_export":false,"meters":[{"serial_number":"IMP1"}],"agreements":[{"tariff_code":"E-1R-INTELLI-26-01-A","valid_from":"2025-01-01T00:00:00Z","valid_to":null}]},{"mpan":"MPAN-DEMO-EXPORT","is_export":true,"meters":[{"serial_number":"EXP1"}],"agreements":[{"tariff_code":"E-1R-OUTGOING-26-01-A","valid_from":"2025-01-01T00:00:00Z","valid_to":null}]}],"gas_meter_points":[{"mprn":"MPRN-DEMO-SECONDARY","meters":[{"serial_number":"GAS1"},{"serial_number":"GAS2"}],"agreements":[{"tariff_code":"G-1R-FLEX-26-01-A","valid_from":"2025-01-01T00:00:00Z","valid_to":null}]}]}]}""");
            return new(HttpStatusCode.NotFound);
        }
        private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class UpgradeHandler : HttpMessageHandler
    {
        public List<string> Paths { get; }=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            var path=request.RequestUri?.AbsolutePath??""; Paths.Add(path);
            if(path=="/v1/products/INTELLI-VAR-25-01/") return Task.FromResult(OctopusMetadataFixture.Response("INTELLI-VAR-25-01", "E-1R-INTELLI-VAR-25-01-A"));
            if(path=="/v1/accounts/ACCOUNT-UPGRADE-DEMO/") return Task.FromResult(Ok("""{"number":"ACCOUNT-UPGRADE-DEMO","properties":[{"id":1,"moved_out_at":null,"electricity_meter_points":[{"mpan":"MPAN-DEMO-IMPORT","is_export":false,"meters":[{"serial_number":"IMP1"}],"agreements":[{"tariff_code":"E-1R-INTELLI-VAR-25-01-A","valid_from":"2025-01-01T00:00:00Z","valid_to":null}]}],"gas_meter_points":[]}]}"""));
            if(path.Contains("standard-unit-rates",StringComparison.Ordinal)) return Task.FromResult(Ok("""{"next":null,"results":[{"value_inc_vat":25,"valid_from":"2025-01-01T00:00:00Z","valid_to":null}]}"""));
            if(path.Contains("consumption",StringComparison.Ordinal)) return Task.FromResult(Ok("""{"next":null,"results":[{"consumption":1,"interval_start":"2026-06-18T00:00:00Z","interval_end":"2026-06-18T00:30:00Z"}]}"""));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        private static HttpResponseMessage Ok(string json)=>new(HttpStatusCode.OK){Content=new StringContent(json,Encoding.UTF8,"application/json")};
    }

    private sealed class DualRateHandler:HttpMessageHandler
    {
        public List<string> Paths { get; }=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            var path=request.RequestUri?.AbsolutePath??""; Paths.Add(path); if(path.Contains("E-2R",StringComparison.OrdinalIgnoreCase)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
            if(path=="/v1/products/VAR/") return Task.FromResult(OctopusMetadataFixture.Response("VAR", "E-2R-VAR-A", "dual_register_electricity_tariffs"));
            if(path=="/v1/products/INTELLI-VAR/") return Task.FromResult(OctopusMetadataFixture.Response("INTELLI-VAR", "E-1R-INTELLI-VAR-A"));
            if(path.Contains("standard-unit-rates",StringComparison.Ordinal)) return Task.FromResult(Ok("""{"next":null,"results":[{"value_inc_vat":7,"valid_from":"2026-06-18T00:00:00Z","valid_to":"2026-06-18T00:30:00Z"},{"value_inc_vat":25,"valid_from":"2026-06-18T00:30:00Z","valid_to":"2026-06-18T01:00:00Z"}]}"""));
            if(path.Contains("consumption",StringComparison.Ordinal)) return Task.FromResult(Ok("""{"next":null,"results":[{"consumption":1,"interval_start":"2026-06-18T00:00:00Z","interval_end":"2026-06-18T00:30:00Z"}]}""")); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        private static HttpResponseMessage Ok(string json)=>new(HttpStatusCode.OK){Content=new StringContent(json,Encoding.UTF8,"application/json")};
    }
}
