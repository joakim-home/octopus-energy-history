using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OctopusEnergyDashboard.Application;

namespace OctopusEnergyDashboard.Infrastructure;

public sealed record SupplierGasMeasurement(DateTimeOffset ReadAt, string Source, decimal Kwh, decimal? CostGbp, string Evidence);
public sealed record SupplierGasMeasurementPage(IReadOnlyList<SupplierGasMeasurement> Measurements, string? NextCursor);

public sealed class GasMeasurementImporter(IDashboardRepository repository, IOctopusConfigurationStore configuration, SupplierAllocationStore store, HttpClient http) : IProviderConnector
{
    private const string Base = "https://api.octopus.energy/v1/graphql/";
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    public string Name => "Octopus gas measurements";

    public Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new ConnectionTestResult(false, "Supplier gas measurement evidence is verified during sync."));

    public async Task<SyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var key = await repository.GetSettingAsync(SettingKeys.OctopusApiKey, cancellationToken) ?? "";
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Octopus credential missing.");
            var meters = (await configuration.GetOctopusMeterPointsAsync(cancellationToken)).Where(x => x.FuelType == "gas" && !x.IsExport)
                .DistinctBy(x => (x.AccountNumber, x.MeterPoint)).ToArray();
            if (meters.Length == 0) return new(Name, true, 0, "No gas meter is configured.");

            var token = await TokenAsync(key, cancellationToken);
            var appliedDays = 0; var correctedIntervals = 0; var warnings = new List<string>();
            foreach (var meter in meters)
            {
                var meterDays = 0;
                var range = await store.GasMeasurementRangeAsync(meter.MeterPoint, cancellationToken);
                if (range is null) continue;
                var from = AtLondonMidnight(range.Value.From);
                var to = AtLondonMidnight(range.Value.To.AddDays(1));
                string? cursor = null; var seen = new HashSet<string?>();
                do
                {
                    if (!seen.Add(cursor)) throw new InvalidOperationException("Gas measurement pagination repeated a cursor.");
                    var json = await GraphqlAsync(MeasurementQuery, new
                    {
                        account = meter.AccountNumber,
                        mprn = meter.MeterPoint,
                        from = from.ToString("O"),
                        to = to.ToString("O"),
                        after = cursor
                    }, token, cancellationToken);
                    var page = Parse(json);
                    foreach (var measurement in page.Measurements)
                    {
                        var local = TimeZoneInfo.ConvertTime(measurement.ReadAt, London);
                        var date = DateOnly.FromDateTime(local.DateTime);
                        if (date < range.Value.From || date > range.Value.To) continue;
                        var changed = await store.SaveGasDailyMeasurementAsync(meter.MeterPoint, date, measurement.Kwh, measurement.CostGbp, measurement.Source, measurement.Evidence, cancellationToken);
                        if (changed > 0) { appliedDays++; meterDays++; correctedIntervals += changed; }
                    }
                    cursor = page.NextCursor;
                } while (cursor is not null);
                if (meterDays == 0)
                    warnings.Add($"{meter.MeterPoint}: supplier returned no usable daily kWh measurements for the requested range.");
                else
                {
                    var remaining = await store.FirstMissingGasMeasurementDayAsync(meter.MeterPoint, cancellationToken);
                    if (remaining is not null && remaining.Value <= range.Value.To)
                        warnings.Add($"{meter.MeterPoint}: supplier daily kWh coverage is still incomplete from {remaining.Value:yyyy-MM-dd}. Uncorrected raw gas remains preserved rather than guessed.");
                }
            }
            if (repository is IOctopusReadingStore readings) await readings.RebuildOctopusRollupsAsync(cancellationToken);
            return new(Name, warnings.Count == 0, correctedIntervals,
                warnings.Count == 0
                    ? $"Supplier gas measurements applied: {appliedDays} day(s), {correctedIntervals} raw interval(s) corrected without changing raw meter evidence."
                    : $"Supplier gas measurements incomplete. {string.Join(" ", warnings)}");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or FormatException or KeyNotFoundException)
        {
            return new(Name, false, 0, $"Supplier gas measurements unavailable: {ex.Message} Raw gas evidence was not changed.");
        }
    }

    public static SupplierGasMeasurementPage Parse(string json)
    {
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        if (root.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0) throw new InvalidOperationException("Octopus GraphQL returned errors for gas measurements.");
        var active = new List<JsonElement>();
        foreach (var property in root.GetProperty("data").GetProperty("account").GetProperty("properties").EnumerateArray())
        {
            var connection = property.GetProperty("measurements");
            var edges = connection.GetProperty("edges");
            var hasNext = connection.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean();
            if (edges.GetArrayLength() > 0 || hasNext) active.Add(connection);
        }
        if (active.Count > 1) throw new InvalidOperationException("Gas measurement scope is ambiguous across properties.");
        if (active.Count == 0) return new([], null);
        var selected = active[0]; var output = new List<SupplierGasMeasurement>();
        foreach (var edge in selected.GetProperty("edges").EnumerateArray())
        {
            var node = edge.GetProperty("node"); var unit = node.GetProperty("unit").GetString() ?? "";
            var normalized = new string(unit.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            if (normalized is not ("kwh" or "kilowatthour" or "kilowatthours")) continue;
            var kwh = Decimal(node.GetProperty("value")); if (kwh < 0) throw new InvalidOperationException("Supplier gas measurement has a negative quantity.");
            decimal? cost = null;
            if (node.TryGetProperty("metaData", out var metadata) && metadata.ValueKind != JsonValueKind.Null && metadata.TryGetProperty("statistics", out var statistics))
            {
                var costs = new List<decimal>();
                foreach (var statistic in statistics.EnumerateArray())
                {
                    if (statistic.GetProperty("type").GetString() != "CONSUMPTION_COST") continue;
                    if (!statistic.TryGetProperty("costInclTax", out var money) || money.ValueKind == JsonValueKind.Null) continue;
                    if (money.GetProperty("costCurrency").GetString() != "GBP") throw new InvalidOperationException("Supplier gas measurement cost is not GBP.");
                    costs.Add(Decimal(money.GetProperty("estimatedAmount")) / 100m);
                }
                var distinct = costs.Distinct().ToArray();
                if (distinct.Length > 1) throw new InvalidOperationException("Supplier gas measurement has ambiguous inclusive cost evidence.");
                if (distinct.Length == 1) cost = distinct[0];
            }
            output.Add(new(node.GetProperty("readAt").GetDateTimeOffset(), node.GetProperty("source").GetString() ?? "supplier_measurements", kwh, cost, node.GetRawText()));
        }
        var pageInfo = selected.GetProperty("pageInfo");
        var next = pageInfo.GetProperty("hasNextPage").GetBoolean() && pageInfo.TryGetProperty("endCursor", out var endCursor) && endCursor.ValueKind == JsonValueKind.String ? endCursor.GetString() : null;
        if (pageInfo.GetProperty("hasNextPage").GetBoolean() && string.IsNullOrWhiteSpace(next)) throw new InvalidOperationException("Gas measurement pagination has no end cursor.");
        return new(output, next);
    }

    private static decimal Decimal(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? decimal.Parse(value.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture) : value.GetDecimal();
    private static DateTimeOffset AtLondonMidnight(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new(local, London.GetUtcOffset(local));
    }
    private async Task<string> TokenAsync(string key, CancellationToken ct)
    {
        var json = await GraphqlAsync("mutation($key:String!){obtainKrakenToken(input:{APIKey:$key}){token}}", new { key }, null, ct);
        using var doc = JsonDocument.Parse(json); return doc.RootElement.GetProperty("data").GetProperty("obtainKrakenToken").GetProperty("token").GetString() ?? throw new InvalidOperationException("Octopus did not return a token.");
    }
    private async Task<string> GraphqlAsync(string query, object variables, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Base) { Content = new StringContent(JsonSerializer.Serialize(new { query, variables }), Encoding.UTF8, "application/json") };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("JWT", token);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("OctopusEnergyDashboard", "1.0"));
        using var response = await http.SendAsync(request, ct); response.EnsureSuccessStatusCode(); var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json); if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0) throw new InvalidOperationException("Octopus GraphQL returned errors for gas measurements."); return json;
    }

    public const string MeasurementQuery = """
        query($account:String!,$mprn:String!,$from:DateTime!,$to:DateTime!,$after:String){
          account(accountNumber:$account){properties{
            measurements(startAt:$from,endAt:$to,timezone:"Europe/London",first:100,after:$after,
              utilityFilters:[{gasFilters:{marketSupplyPointId:$mprn,readingFrequencyType:DAY_INTERVAL}}]){
              pageInfo{hasNextPage endCursor}
              edges{node{readAt source value unit metaData{statistics{type value costInclTax{estimatedAmount costCurrency}}}}}
            }
          }}
        }
        """;
}
