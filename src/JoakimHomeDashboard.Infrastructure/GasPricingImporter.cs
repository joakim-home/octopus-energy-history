using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JoakimHomeDashboard.Application;

namespace JoakimHomeDashboard.Infrastructure;

public sealed class GasPricingImporter(IDashboardRepository repository, IOctopusConfigurationStore configuration, SupplierAllocationStore store, HttpClient http) : IProviderConnector
{
    private const string Base = "https://api.octopus.energy/v1/";
    public string Name => "Octopus gas pricing";
    public Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken=default)
        => Task.FromResult(new ConnectionTestResult(false,"Payment evidence is verified during sync using the configured Octopus account."));

    public async Task<SyncResult> SyncAsync(CancellationToken cancellationToken=default)
    {
        var ct=cancellationToken; int expected=0, priced=0; string? token=null;
        var key=await repository.GetSettingAsync(SettingKeys.OctopusApiKey,ct) ?? "";
        var tariffs=(await configuration.GetOctopusTariffPeriodsAsync(ct)).Where(t=>t.FuelType=="gas").ToArray();
        var rateCache=new Dictionary<(string Product,string Tariff),(List<GasRate> Rates,string Metadata,string[] Pages)>();
        try
        {
            foreach(var meter in tariffs.GroupBy(t=>t.MeterPoint))
            foreach(var day in (await store.UnpricedGasAsync(meter.Key,ct)).GroupBy(r=>r.LocalDate))
            {
                var candidates=day.Select(row=>(Row:row,Agreements:meter.Where(t=>row.Start>=(t.ValidFrom??DateTimeOffset.MinValue)&&row.End<=(t.ValidTo??DateTimeOffset.MaxValue)).ToArray()))
                    .Where(x=>x.Agreements.Length>0).ToArray();
                if(candidates.Length==0) continue;
                expected+=candidates.Length;
                if(token is null)
                {
                    using var auth=JsonDocument.Parse(await GraphqlAsync("mutation($key:String!){obtainKrakenToken(input:{APIKey:$key}){token}}",new {key},null,ct));
                    token=auth.RootElement.GetProperty("data").GetProperty("obtainKrakenToken").GetProperty("token").GetString();
                }
                var account=meter.Select(t=>t.AccountNumber).Distinct().ToArray(); if(account.Length!=1) continue;
                var payment=await GraphqlAsync(PaymentQuery,new {account=account[0],date=day.Key},token,ct);
                var method=ResolvePaymentMethod(payment); if(method is null) continue;
                foreach(var candidate in candidates)
                {
                    var row=candidate.Row; var agreements=candidate.Agreements;
                    if(agreements.Length!=1) continue; var agreement=agreements[0];
                    var cacheKey=(agreement.ProductCode,agreement.TariffCode);
                    if(!rateCache.TryGetValue(cacheKey,out var cache))
                    {
                        var metadata=await GetAsync(new Uri(Base+$"products/{Uri.EscapeDataString(agreement.ProductCode)}/?tariffs_active_at={Uri.EscapeDataString(row.Start.ToString("O"))}"),key,ct);
                        var parsed=OctopusTariffMetadata.Parse(metadata,agreement.TariffCode);
                        if(!parsed.Endpoints.TryGetValue("standard_unit_rates",out var endpoint)) continue;
                        var rates=new List<GasRate>(); var pages=new List<string>();
                        foreach(var relation in new[]{"standard_unit_rates","standing_charges"})
                        {
                        if(!parsed.Endpoints.TryGetValue(relation,out endpoint)) continue;
                        var seen=new HashSet<Uri>(); Uri? next=endpoint;
                        while(next is not null)
                        {
                            OctopusTariffMetadata.ValidateEndpoint(next);
                            if(next.AbsolutePath!=endpoint.AbsolutePath||!seen.Add(next)) throw new InvalidOperationException("Invalid gas rate pagination.");
                            var json=await GetAsync(next,key,ct); pages.Add(json); using var doc=JsonDocument.Parse(json);
                            foreach(var r in doc.RootElement.GetProperty("results").EnumerateArray())
                                rates.Add(new(r.GetProperty("valid_from").GetDateTimeOffset(),r.GetProperty("valid_to").ValueKind==JsonValueKind.Null?DateTimeOffset.MaxValue:r.GetProperty("valid_to").GetDateTimeOffset(),r.GetProperty("value_inc_vat").GetDecimal(),r.TryGetProperty("payment_method",out var pm)?pm.GetString():null,relation=="standing_charges"));
                            next=doc.RootElement.TryGetProperty("next",out var n)&&n.ValueKind==JsonValueKind.String?new Uri(n.GetString()!):null;
                        }
                        }
                        cache=(rates,metadata,pages.ToArray()); rateCache[cacheKey]=cache;
                    }
                    var matches=cache.Rates.Where(r=>!r.Standing&&row.Start>=r.From&&row.End<=r.To&&(r.Method is null||r.Method==method)).Distinct().ToArray();
                    if(matches.Length!=1) continue;
                    var evidence=JsonSerializer.Serialize(new {payment,metadata=cache.Metadata,ratePages=cache.Pages,taxBasis="value_inc_vat",provenance="supplier_tariff_calculation"});
                    await store.SaveGasPriceAsync(row,matches[0].Rate,method,agreement.TariffCode,evidence,ct); priced++;
                    var date=DateOnly.Parse(day.Key); var midnight=date.ToDateTime(TimeOnly.MinValue);
                    var instant=new DateTimeOffset(midnight,TimeZoneInfo.FindSystemTimeZoneById("Europe/London").GetUtcOffset(midnight));
                    var standing=cache.Rates.Where(r=>r.Standing&&instant>=r.From&&instant<r.To&&(r.Method is null||r.Method==method)).Distinct().ToArray();
                    if(standing.Length==1 && instant>=(agreement.ValidFrom??DateTimeOffset.MinValue) && instant<(agreement.ValidTo??DateTimeOffset.MaxValue) && repository is IOctopusReadingStore readingStore)
                        await readingStore.InsertMissingOctopusStandingChargesAsync([new(date,meter.Key,agreement.TariffCode,standing[0].Rate/100m)],ct);
                }
            }
            return new(Name,expected==priced,priced,expected==priced?$"Gas pricing reconciled: {priced}/{expected} previously unpriced intervals.":$"Gas pricing pending: {expected-priced} intervals have no unambiguous main-ledger payment schedule, agreement or dated rate. No price was guessed.");
        }
        finally { if(repository is IOctopusReadingStore readings) await readings.RebuildOctopusRollupsAsync(ct); }
    }

    public static string? ResolvePaymentMethod(string json)
    {
        try { return ReadPaymentMethod(json); }
        catch(Exception ex) when(ex is JsonException or InvalidOperationException or KeyNotFoundException) { return null; }
    }

    private static string? ReadPaymentMethod(string json)
    {
        using var doc=JsonDocument.Parse(json); var root=doc.RootElement;
        if(root.TryGetProperty("errors",out var errors)&&errors.GetArrayLength()>0) return null;
        var account=root.GetProperty("data").GetProperty("account");
        var main=account.GetProperty("ledgers").EnumerateArray().Where(l=>l.GetProperty("ledgerType").GetString()=="MAIN").Select(l=>l.GetProperty("number").GetString()).ToArray();
        if(main.Length!=1) return null;
        var schedules=account.GetProperty("paymentSchedules"); if(schedules.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean()) return null;
        var applicable=schedules.GetProperty("edges").EnumerateArray().Select(e=>e.GetProperty("node"))
            .Where(s=>s.GetProperty("ledgerNumber").GetString()==main[0]&&s.GetProperty("reason").GetString()=="GENERAL_ACCOUNT_PAYMENT").ToArray();
        if(applicable.Length!=1) return null;
        return applicable[0].GetProperty("scheduleType").GetString() switch { "DIRECT_DEBIT"=>"DIRECT_DEBIT", "BACS_TRANSFER"=>"NON_DIRECT_DEBIT", _=>null };
    }

    private async Task<string> GetAsync(Uri uri,string key,CancellationToken ct)
    {
        using var req=new HttpRequestMessage(HttpMethod.Get,uri); req.Headers.Authorization=new AuthenticationHeaderValue("Basic",Convert.ToBase64String(Encoding.UTF8.GetBytes(key+":")));
        using var response=await http.SendAsync(req,ct); response.EnsureSuccessStatusCode(); return await response.Content.ReadAsStringAsync(ct);
    }
    private async Task<string> GraphqlAsync(string query,object variables,string? token,CancellationToken ct)
    {
        using var req=new HttpRequestMessage(HttpMethod.Post,Base+"graphql/"){Content=new StringContent(JsonSerializer.Serialize(new {query,variables}),Encoding.UTF8,"application/json")};
        if(token is not null) req.Headers.Authorization=new("JWT",token);
        using var response=await http.SendAsync(req,ct); response.EnsureSuccessStatusCode(); var json=await response.Content.ReadAsStringAsync(ct);
        using var doc=JsonDocument.Parse(json); if(doc.RootElement.TryGetProperty("errors",out var errors)&&errors.GetArrayLength()>0) throw new InvalidOperationException("Gas payment evidence query failed; no price selected.");
        return json;
    }
    private sealed record GasRate(DateTimeOffset From,DateTimeOffset To,decimal Rate,string? Method,bool Standing);
    public const string PaymentQuery="""
        query($account:String!,$date:Date!){account(accountNumber:$account){ledgers{number ledgerType} paymentSchedules(first:100,activeOnDate:$date,includeDormant:false){pageInfo{hasNextPage} edges{node{validFrom validTo scheduleType reason ledgerNumber}}}}}
        """;
}
