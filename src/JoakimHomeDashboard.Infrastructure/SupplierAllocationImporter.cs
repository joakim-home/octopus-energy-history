using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JoakimHomeDashboard.Application;

namespace JoakimHomeDashboard.Infrastructure;

public sealed record SupplierTaxRate(string Band, DateTimeOffset From, DateTimeOffset To, decimal ExVat, decimal IncVat);
public sealed record AllocationImportResult(int Intervals, int Reconciled, IReadOnlyList<string> Warnings);

public sealed class SupplierAllocationImporter(IDashboardRepository repository, IOctopusConfigurationStore configuration, SupplierAllocationStore store, HttpClient http)
{
    private const string Base = "https://api.octopus.energy/v1/";
    private static readonly Dictionary<string,string> Relations = new()
    {
        ["ECO7_DAY"]="day_unit_rates", ["ECO7_NIGHT"]="night_unit_rates", ["EV_DEVICE_PEAK"]="ev_device_peak_unit_rates", ["EV_DEVICE_OFF_PEAK"]="ev_device_off_peak_unit_rates"
    };

    public Task<AllocationImportResult> BackfillAsync(CancellationToken ct = default) => ImportAsync(false, ct);
    public Task<AllocationImportResult> SyncIncrementalAsync(CancellationToken ct = default) => ImportAsync(true, ct);

    private async Task<AllocationImportResult> ImportAsync(bool incremental, CancellationToken ct)
    {
        var key = await repository.GetSettingAsync(SettingKeys.OctopusApiKey, ct) ?? throw new InvalidOperationException("Octopus credential missing.");
        var tariffs = (await configuration.GetOctopusTariffPeriodsAsync(ct)).Where(t=>t.FuelType=="electricity"&&!t.IsExport).ToArray();
        var total=0; var reconciled=0; var warnings=new List<string>(); string? token=null;
        var accountsValidated=new HashSet<string>();
        foreach(var tariff in tariffs)
        {
            if (tariff.ValidFrom is null || string.IsNullOrWhiteSpace(tariff.ProductCode)) continue;
            var metadataJson = await GetAsync(new Uri(Base+$"products/{Uri.EscapeDataString(tariff.ProductCode)}/?tariffs_active_at={Uri.EscapeDataString(tariff.ValidFrom.Value.ToString("O"))}"),key,ct);
            var metadata = OctopusTariffMetadata.Parse(metadataJson,tariff.TariffCode);
            if(metadata.Semantics!=OctopusProductSemantics.FourRateEv) continue;
            // Register coverage before fetching allocations, so an unavailable response cannot restore legacy prices.
            await store.RegisterAsync(tariff,metadataJson,ct);
            var readings = await store.ReadingsAsync(tariff,ct);
            if (incremental)
            {
                var configured = Environment.GetEnvironmentVariable("OCTOPUS_ALLOCATION_OVERLAP_DAYS");
                var overlap = configured is null ? 3 : int.TryParse(configured, out var days) && days is >= 1 and <= 30 ? days : throw new InvalidOperationException("OCTOPUS_ALLOCATION_OVERLAP_DAYS must be 1–30.");
                readings = await store.IncrementalReadingsAsync(readings, ct, overlap);
            }
            if(readings.Count==0) continue;
            if(accountsValidated.Add(tariff.AccountNumber))
            {
                var accountJson=await GetAsync(new Uri(Base+$"accounts/{Uri.EscapeDataString(tariff.AccountNumber)}/"),key,ct);
                var live=OctopusResponseParser.ParseAccountDiscovery(accountJson,tariff.AccountNumber);
                var imports=live.MeterPoints.Where(m=>m.FuelType=="electricity"&&!m.IsExport).Select(m=>m.MeterPoint).Distinct().ToArray();
                // The account-scoped endpoint is safe only when its import scope is unambiguous.
                if(imports.Length!=1||imports[0]!=tariff.MeterPoint) throw new InvalidOperationException("Allocation account has ambiguous import supply points; explicit supplier scope is required.");
                await store.EvidenceAsync(tariff.AccountNumber,"account_scope",accountJson,ct);
            }
            var taxRates=new List<SupplierTaxRate>();
            foreach(var (band,rel) in Relations)
            {
                if(!metadata.Endpoints.TryGetValue(rel,out var endpoint)) throw new InvalidOperationException($"Missing {rel} metadata.");
                var next=endpoint; var seen=new HashSet<Uri>();
                while(next is not null)
                {
                    OctopusTariffMetadata.ValidateEndpoint(next);
                    if(next.AbsolutePath!=endpoint.AbsolutePath||!seen.Add(next)) throw new InvalidOperationException("Invalid supplier rate pagination.");
                    var json=await GetAsync(next,key,ct);
                    await store.EvidenceAsync(tariff.AccountNumber,$"tax_rates:{tariff.TariffCode}:{band}",json,ct);
                    using var page=JsonDocument.Parse(json);
                    foreach(var r in page.RootElement.GetProperty("results").EnumerateArray()) taxRates.Add(new(band,r.GetProperty("valid_from").GetDateTimeOffset(),r.GetProperty("valid_to").ValueKind==JsonValueKind.Null?DateTimeOffset.MaxValue:r.GetProperty("valid_to").GetDateTimeOffset(),Decimal(r.GetProperty("value_exc_vat")),Decimal(r.GetProperty("value_inc_vat"))));
                    next=page.RootElement.TryGetProperty("next",out var n)&&n.ValueKind==JsonValueKind.String?new Uri(n.GetString()!):null;
                }
            }
            token ??= await TokenAsync(key,ct);
            foreach(var day in readings.GroupBy(r=>r.LocalDate))
            {
                var rows=day.ToArray(); total+=rows.Length;
                string json="{}";
                try
                {
                    if(rows.Any(r=>r.Start<tariff.ValidFrom||tariff.ValidTo is not null&&r.End>tariff.ValidTo)) throw new InvalidOperationException("reading_straddles_agreement_boundary");
                    json=await GraphqlAsync(AllocationQuery,new {account=tariff.AccountNumber,periods=new[]{new {start=rows.Min(r=>r.Start).ToUniversalTime().ToString("O"),end=rows.Max(r=>r.End).ToUniversalTime().ToString("O")}}},token,ct);
                    var allocations=Parse(json,taxRates);
                    var matched=await store.SaveAsync(tariff,rows,allocations,json,ct:ct); reconciled+=matched;
                    if(matched!=rows.Length) warnings.Add($"{day.Key}: {rows.Length-matched} allocation intervals unresolved.");
                }
                catch(Exception ex) when(ex is HttpRequestException or InvalidOperationException or JsonException or FormatException or KeyNotFoundException)
                {
                    await store.SaveAsync(tariff,rows,[],json,"supplier_response_unusable",ct);
                    warnings.Add($"{day.Key}: allocation unavailable ({ex.Message}).");
                }
            }
            try
            {
                await store.EvidenceAsync(tariff.AccountNumber,"charging_sessions",await GraphqlAsync(SessionQuery,new {account=tariff.AccountNumber,from=readings.Min(r=>r.Start).ToUniversalTime().ToString("O"),to=readings.Max(r=>r.End).ToUniversalTime().ToString("O")},token,ct),ct);
                await store.EvidenceAsync(tariff.AccountNumber,"issued_bills",await GraphqlAsync(BillingQuery,new {account=tariff.AccountNumber},token,ct),ct);
                // Supporting tax-inclusive sample is kept as supplier-estimated evidence, never used as raw consumption.
                await store.EvidenceAsync(tariff.AccountNumber,"measurement_tax_sample",await GraphqlAsync(MeasurementQuery,new {account=tariff.AccountNumber,from=readings[0].Start.ToUniversalTime().ToString("O"),to=readings[0].End.ToUniversalTime().ToString("O")},token,ct),ct);
            }
            catch(Exception ex) when(ex is HttpRequestException or InvalidOperationException) { warnings.Add($"Supporting evidence incomplete: {ex.Message}"); }
        }
        return new(total,reconciled,warnings);
    }

    public static List<SupplierAllocation> Parse(string json, IReadOnlyList<SupplierTaxRate> rates)
    {
        using var doc=JsonDocument.Parse(json); RejectErrors(doc.RootElement);
        var output=new List<SupplierAllocation>();
        foreach(var p in doc.RootElement.GetProperty("data").GetProperty("gbrCostOfUsage").GetProperty("periods").EnumerateArray())
        {
            if(p.GetProperty("consumptionUnit").GetString()=="day") continue; // Standing charge evidence stays in the revision payload.
            if(p.GetProperty("consumptionUnit").GetString()!="kilowatt_hour"||p.GetProperty("currency").GetString()!="GBP_PENCE") throw new InvalidOperationException("Unrecognised allocation units.");
            decimal periodQuantity=0,periodCost=0;
            foreach(var a in p.GetProperty("intervals").EnumerateArray())
            {
                var band=a.GetProperty("bandSubcategory").GetString()!;
                if(!SupplierAllocationStore.Bands.Contains(band)||a.GetProperty("consumptionUnit").GetString()!="kilowatt_hour"||a.GetProperty("currency").GetString()!="GBP_PENCE") throw new InvalidOperationException("Unrecognised allocation band or units.");
                var start=a.GetProperty("period").GetProperty("start").GetDateTimeOffset(); var end=a.GetProperty("period").GetProperty("end").GetDateTimeOffset();
                var quantity=Decimal(a.GetProperty("consumption")); var rate=Decimal(a.GetProperty("rateApplied")); var cost=Decimal(a.GetProperty("cost"));
                if(end<=start||quantity<0||Math.Abs(quantity*rate-cost)>0.00001m) throw new InvalidOperationException("Supplier interval quantity/rate/cost inconsistency.");
                var candidates=rates.Where(r=>r.Band==band&&start>=r.From&&end<=r.To).Distinct().ToArray();
                if(candidates.Length!=1) throw new InvalidOperationException("Tax evidence missing or ambiguous.");
                var tax=candidates[0];
                if(tax.ExVat<=0||tax.IncVat<tax.ExVat) throw new InvalidOperationException("Unsupported tax rate evidence.");
                var factor=tax.IncVat/tax.ExVat;
                string basis; decimal net,gross;
                if(Math.Abs(rate-tax.ExVat)<0.000001m) { basis="exclusive_verified_rest"; net=cost; gross=cost*factor; }
                else if(Math.Abs(rate-tax.IncVat)<0.000001m) { basis="inclusive_verified_rest"; gross=cost; net=cost/factor; }
                else throw new InvalidOperationException("Supplier applied rate does not match VAT evidence.");
                output.Add(new(start,end,band,quantity,rate,cost,net,gross,factor-1,basis)); periodQuantity+=quantity; periodCost+=cost;
            }
            if(Math.Abs(periodQuantity-Decimal(p.GetProperty("totalConsumption")))>0.000001m||Math.Abs(periodCost-Decimal(p.GetProperty("totalCost")))>0.00001m)
                throw new InvalidOperationException("Supplier period totals do not reconcile with interval bands.");
        }
        if(output.GroupBy(a=>(a.Start,a.End,a.Band)).Any(g=>g.Count()!=1)) throw new InvalidOperationException("Duplicate supplier allocation bands.");
        return output;
    }

    private static decimal Decimal(JsonElement value) => value.ValueKind==JsonValueKind.String?decimal.Parse(value.GetString()!,NumberStyles.Float,CultureInfo.InvariantCulture):value.GetDecimal();
    private async Task<string> GetAsync(Uri uri,string key,CancellationToken ct)
    {
        using var req=new HttpRequestMessage(HttpMethod.Get,uri); req.Headers.Authorization=new AuthenticationHeaderValue("Basic",Convert.ToBase64String(Encoding.UTF8.GetBytes(key+":")));
        using var res=await http.SendAsync(req,ct); res.EnsureSuccessStatusCode(); return await res.Content.ReadAsStringAsync(ct);
    }
    private async Task<string> TokenAsync(string key,CancellationToken ct)
    {
        var json=await GraphqlAsync("mutation($key:String!){obtainKrakenToken(input:{APIKey:$key}){token}}",new {key},null,ct);
        using var doc=JsonDocument.Parse(json); return doc.RootElement.GetProperty("data").GetProperty("obtainKrakenToken").GetProperty("token").GetString()!;
    }
    private async Task<string> GraphqlAsync(string query,object variables,string? token,CancellationToken ct)
    {
        using var req=new HttpRequestMessage(HttpMethod.Post,Base+"graphql/") {Content=new StringContent(JsonSerializer.Serialize(new {query,variables}),Encoding.UTF8,"application/json")};
        if(token is not null) req.Headers.Authorization=new("JWT",token);
        using var res=await http.SendAsync(req,ct); res.EnsureSuccessStatusCode(); var json=await res.Content.ReadAsStringAsync(ct);
        using var doc=JsonDocument.Parse(json); RejectErrors(doc.RootElement); return json;
    }
    private static void RejectErrors(JsonElement root)
    {
        if(root.TryGetProperty("errors",out var errors)&&errors.GetArrayLength()>0) throw new InvalidOperationException("Octopus GraphQL returned errors; partial data was rejected.");
    }
    public const string AllocationQuery="""
        query($account:String!,$periods:[DateTimeRangeInput]!){gbrCostOfUsage(accountNumber:$account,periods:$periods){periods{period{start end} totalConsumption consumptionUnit totalCost currency intervals{period{start end} consumption consumptionUnit cost currency rateApplied bandSubcategory}}}}
        """;
    public const string SessionQuery="""
        query($account:String!,$from:DateTime,$to:DateTime){devices(accountNumber:$account){id provider deviceType ... on ElectricDevice{chargingSessions(after:$from,before:$to,first:100){edges{node{start end energyAdded{value unit} cost{amount currency} ... on SmartFlexChargingSession{type dispatches{start end type energyAddedKwh}}}}}}}}
        """;
    public const string BillingQuery="""
        query($account:String!){account(accountNumber:$account){bills(first:10){pageInfo{hasNextPage endCursor} edges{node{issuedDate fromDate toDate ... on StatementType{id consumptionStartDate consumptionEndDate transactions(first:100){pageInfo{hasNextPage endCursor} edges{node{__typename id title isIssued isReversed amounts{net tax gross} ... on Charge{consumption{startDate endDate quantity unit usageCost supplyCharge}}}}}}}}}}}
        """;
    public const string MeasurementQuery="""
        query($account:String!,$from:DateTime,$to:DateTime){account(accountNumber:$account){properties{measurements(startAt:$from,endAt:$to,first:10,utilityFilters:[{electricityFilters:{readingFrequencyType:THIRTY_MIN_INTERVAL,readingDirection:CONSUMPTION,modelledSubMeterReadings:true}}]){edges{node{source value unit metaData{statistics{label value costExclTax{estimatedAmount costCurrency} costInclTax{estimatedAmount costCurrency}}}}}}}}}
        """;
}

