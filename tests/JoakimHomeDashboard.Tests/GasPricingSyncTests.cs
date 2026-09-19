using System.Net;
using System.Text.Json;
using JoakimHomeDashboard.Application;
using JoakimHomeDashboard.Domain;
using JoakimHomeDashboard.Infrastructure;
using Microsoft.Data.Sqlite;

namespace JoakimHomeDashboard.Tests;

public sealed class GasPricingSyncTests
{
    [Fact]
    public async Task DatedRateAndAgreementBoundarySelectsDdWithoutChangingRawOrExistingPrices()
    {
        using var f = await Fixture.Create();
        var before = await f.Raw();
        var result = await f.Importer.SyncAsync();
        Assert.True(result.Succeeded);
        Assert.Equal(2, result.RecordsImported);
        Assert.Equal(.28m, await f.Standing());
        Assert.Equal(before, await f.Raw());
        Assert.Equal(new decimal?[] { .14m, .18m, .77m }, (await f.Repository.GetOctopusIntervalsAsync(new(2026, 9, 14))).Select(r => r.CostGbp));
        var dashboard = await f.Repository.GetEnergyDashboardAsync();
        Assert.True(Assert.Single(dashboard.Monthly).GasCostExact);
        Assert.DoesNotContain(dashboard.Warnings, w => w.Contains("exact Octopus tariff coverage is incomplete", StringComparison.OrdinalIgnoreCase));
        Assert.All(f.Handler.Dates, d => Assert.Equal("2026-09-14", d));
        Assert.Equal(0, (await f.Importer.SyncAsync()).RecordsImported);
        Assert.Equal(before, await f.Raw());
    }

    [Fact]
    public async Task NoApplicableRateRemainsUnavailableRatherThanChoosingNonDd()
    {
        using var f = await Fixture.Create();
        f.Handler.Dd = false;
        var before = await f.Raw();
        Assert.False((await f.Importer.SyncAsync()).Succeeded);
        Assert.Equal(before, await f.Raw());
        Assert.Equal(new decimal?[] { null, null, .77m }, (await f.Repository.GetOctopusIntervalsAsync(new(2026, 9, 14))).Select(r => r.CostGbp));
    }

    [Fact]
    public async Task ReusesTariffRatePagesAcrossUnpricedDaysWhileKeepingPaymentEvidenceDated()
    {
        using var f = await Fixture.Create();
        var next = DateTimeOffset.Parse("2026-09-15T00:00:00+01:00");
        var outsideAgreement = DateTimeOffset.Parse("2026-09-10T00:00:00+01:00");
        await f.Repository.UpsertOctopusRawReadingsAsync([
            new(outsideAgreement,outsideAgreement.AddMinutes(30),EnergyFlowType.Gas,1,null,"GAS-DEMO","SERIAL-DEMO","UNKNOWN","synthetic-outside-agreement"),
            new(next,next.AddMinutes(30),EnergyFlowType.Gas,1,null,"GAS-DEMO","SERIAL-DEMO","NEW","synthetic-next-0"),
            new(next.AddMinutes(30),next.AddHours(1),EnergyFlowType.Gas,1,null,"GAS-DEMO","SERIAL-DEMO","NEW","synthetic-next-1")
        ]);

        var result = await f.Importer.SyncAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(4, result.RecordsImported);
        Assert.Equal(["2026-09-14","2026-09-15"], f.Handler.Dates.Distinct().Order().ToArray());
        Assert.DoesNotContain("2026-09-10", f.Handler.Dates);
        Assert.Equal(2, f.Handler.ProductRequests);
        Assert.Equal(4, f.Handler.RatePageRequests);
    }

    [Fact]
    public async Task ExistingVersion11ViewIsReplacedAndProjectionSurvivesReinitialization()
    {
        using var f = await Fixture.Create();
        await f.Importer.SyncAsync();
        var before = await f.Raw();
        using (var connection = new SqliteConnection($"Data Source={f.Path}"))
        {
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DROP VIEW octopus_effective_readings; CREATE VIEW octopus_effective_readings AS SELECT * FROM octopus_raw_readings; DELETE FROM schema_versions WHERE version=12;";
            await cmd.ExecuteNonQueryAsync();
        }
        await f.Repository.InitializeAsync();
        Assert.Equal(before, await f.Raw());
        Assert.Equal(new decimal?[] { .14m, .18m, .77m }, (await f.Repository.GetOctopusIntervalsAsync(new(2026, 9, 14))).Select(r => r.CostGbp));
    }

    private sealed class Fixture : IDisposable
    {
        public string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"gas-test-{Guid.NewGuid():N}.db");
        public SqliteDashboardRepository Repository = null!;
        public Handler Handler = new();
        public GasPricingImporter Importer = null!;
        private HttpClient Http = null!;
        public static async Task<Fixture> Create()
        {
            var f = new Fixture(); f.Repository = new(f.Path, new TestSecretProtector()); await f.Repository.InitializeAsync();
            await f.Repository.SetSettingAsync("octopus.apiKey", "synthetic-test-key");
            var start = DateTimeOffset.Parse("2026-09-14T00:00:00+01:00");
            await f.Repository.ReplaceOctopusConfigurationAsync(new("DEMO", 1, [], [], [
                new("DEMO",1,"gas",false,"GAS-DEMO","OLD","OLD",start,start.AddMinutes(30)),
                new("DEMO",1,"gas",false,"GAS-DEMO","NEW","NEW",start.AddMinutes(30),null)]));
            await f.Repository.UpsertOctopusRawReadingsAsync(Enumerable.Range(0,3).Select(i=>new OctopusRawReading(start.AddMinutes(i*30),start.AddMinutes((i+1)*30),EnergyFlowType.Gas,2,i==2?.77m:null,"GAS-DEMO","SERIAL-DEMO",i==0?"OLD":"NEW",$"synthetic-{i}")).ToArray());
            f.Http = new(f.Handler); f.Importer = new(f.Repository,f.Repository,new(f.Path),f.Http); return f;
        }
        public async Task<decimal> Standing() { using var c=new SqliteConnection($"Data Source={Path}"); await c.OpenAsync(); using var cmd=c.CreateCommand(); cmd.CommandText="SELECT SUM(cost_gbp) FROM octopus_standing_charges"; return Convert.ToDecimal(await cmd.ExecuteScalarAsync()); }
        public async Task<string> Raw()
        {
            using var c=new SqliteConnection($"Data Source={Path}");await c.OpenAsync();using var cmd=c.CreateCommand();cmd.CommandText="SELECT * FROM octopus_raw_readings ORDER BY id";
            using var r=await cmd.ExecuteReaderAsync();var rows=new List<object[]>();while(await r.ReadAsync()){var row=new object[r.FieldCount];r.GetValues(row);rows.Add(row);}return JsonSerializer.Serialize(rows);
        }
        public void Dispose(){Http.Dispose();SqliteConnection.ClearAllPools();File.Delete(Path);}
    }
    private sealed class Handler : HttpMessageHandler
    {
        public bool Dd=true;
        public List<string> Dates=[];
        public int ProductRequests;
        public int RatePageRequests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            string json;
            if(request.Method==HttpMethod.Post)
            {
                using var doc=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                if(doc.RootElement.GetProperty("query").GetString()!.Contains("obtainKrakenToken")) json="{\"data\":{\"obtainKrakenToken\":{\"token\":\"synthetic-token\"}}}";
                else {Dates.Add(doc.RootElement.GetProperty("variables").GetProperty("date").GetString()!);json="""{"data":{"account":{"ledgers":[{"number":"demo","ledgerType":"MAIN"}],"paymentSchedules":{"pageInfo":{"hasNextPage":false},"edges":[{"node":{"ledgerNumber":"demo","reason":"GENERAL_ACCOUNT_PAYMENT","scheduleType":"DIRECT_DEBIT"}}]}}}}""";}
            }
            else
            {
                var code=request.RequestUri!.AbsolutePath.Contains("/OLD/")?"OLD":"NEW";
                if(request.RequestUri.AbsolutePath.EndsWith("rates/") || request.RequestUri.AbsolutePath.EndsWith("standing/"))
                {
                    RatePageRequests++;
                    var from=code=="OLD"?"2026-09-13T23:00:00Z":"2026-09-13T23:30:00Z";var to=code=="OLD"?"2026-09-13T23:30:00Z":"2026-09-15T00:00:00Z";
                    var isStanding=request.RequestUri.AbsolutePath.EndsWith("standing/");
                    var rows=new List<object>{new{valid_from=from,valid_to=to,value_inc_vat=isStanding?35:12,payment_method="NON_DIRECT_DEBIT"}};
                    if(Dd)rows.Add(new{valid_from=from,valid_to=to,value_inc_vat=isStanding?28:code=="OLD"?7:9,payment_method="DIRECT_DEBIT"});
                    json=JsonSerializer.Serialize(new{results=rows,next=(string?)null});
                }
                else
                {
                    ProductRequests++;
                    json=JsonSerializer.Serialize(new{gas_tariffs=new{region=new{direct_debit_monthly=new{code,links=new[]{new{method="GET",rel="standard_unit_rates",href=$"https://api.octopus.energy/v1/products/{code}/rates/"},new{method="GET",rel="standing_charges",href=$"https://api.octopus.energy/v1/products/{code}/standing/"}}}}}});
                }
            }
            return new(HttpStatusCode.OK){Content=new StringContent(json)};
        }
    }
}



