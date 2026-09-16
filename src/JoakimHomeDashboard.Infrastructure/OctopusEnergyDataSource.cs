using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JoakimHomeDashboard.Application;
using JoakimHomeDashboard.Domain;

namespace JoakimHomeDashboard.Infrastructure;

public sealed class OctopusEnergyDataSource(IDashboardRepository repository, IOctopusConfigurationStore configurationStore, IOctopusReadingStore readingStore, HttpClient httpClient) : IProviderConnector, IOctopusDiscoveryService
{
    private const string BaseUrl = "https://api.octopus.energy/v1/";
    public string Name => "Octopus Energy";

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var discovery = await DiscoverAccountAsync(cancellationToken);
            return new(true, $"Connected to account {discovery.AccountNumber}: {discovery.MeterPoints.Count} meter configuration(s) across {discovery.PropertyCount} propert{(discovery.PropertyCount == 1 ? "y" : "ies")}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException) { return new(false, SafeMessage(ex)); }
    }

    public async Task<SyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await LoadSettings(cancellationToken);
            if (string.IsNullOrWhiteSpace(settings.ApiKey)) throw new InvalidOperationException("Configure the Octopus API key first.");
            var meters = (await configurationStore.GetOctopusMeterPointsAsync(cancellationToken)).ToList();
            var tariffPeriods = await configurationStore.GetOctopusTariffPeriodsAsync(cancellationToken);
            var wasAutomaticallyDiscovered = !string.IsNullOrWhiteSpace(await repository.GetSettingAsync("octopus.discoveredAccount",cancellationToken));
            var to = DateTimeOffset.UtcNow;
            var pricingWarnings = new HashSet<string>(StringComparer.Ordinal);
            var needsRefresh = meters.Count > 0 && tariffPeriods.Count == 0 && (wasAutomaticallyDiscovered || meters.Any(x => x.ValidFrom is not null));
            // An old open-ended agreement can be superseded upstream without a local gap yet.
            var lastDiscovery = await repository.GetSettingAsync("octopus.agreementsCheckedAt", cancellationToken);
            if (wasAutomaticallyDiscovered && (!DateTimeOffset.TryParse(lastDiscovery, CultureInfo.InvariantCulture, DateTimeStyles.None, out var checkedAt)
                || to - checkedAt >= TimeSpan.FromDays(1))) needsRefresh = true;
            foreach (var meter in meters)
            {
                var periods = PeriodsFor(tariffPeriods, meter);
                if (periods.Length == 0 && !wasAutomaticallyDiscovered && meter.ValidFrom is null) continue;
                var latest = await readingStore.GetLatestOctopusReadingAsync(meter.MeterPoint, meter.MeterSerial, FlowFor(meter), cancellationToken);
                var requiredFrom = await readingStore.GetEarliestUncostedOctopusReadingAsync(meter.MeterPoint, meter.MeterSerial, FlowFor(meter), cancellationToken)
                    ?? latest?.AddDays(-2) ?? to;
                needsRefresh |= !OctopusAgreementCoverage.Spans(periods, requiredFrom, to);
            }
            if (needsRefresh)
            {
                try { await DiscoverAccountAsync(cancellationToken); }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
                {
                    pricingWarnings.Add($"Agreement refresh failed: {SafeMessage(ex)} Stored agreements are retained; uncovered usage will remain unpriced.");
                }
                meters = (await configurationStore.GetOctopusMeterPointsAsync(cancellationToken)).ToList();
                tariffPeriods = await configurationStore.GetOctopusTariffPeriodsAsync(cancellationToken);
            }
            ApplyAdvancedOverrides(meters, settings);
            if (meters.Count == 0)
            {
                meters.AddRange((await DiscoverAccountAsync(cancellationToken)).MeterPoints);
                tariffPeriods = await configurationStore.GetOctopusTariffPeriodsAsync(cancellationToken);
                ApplyAdvancedOverrides(meters, settings);
            }
            // Normal ingestion is insert-only. Historical correction/repricing needs a separate approved workflow.
            var imported = 0; var unavailableTariffs=new HashSet<string>(StringComparer.OrdinalIgnoreCase); DateTimeOffset? importedFrom = null; DateTimeOffset? importedTo = null; var standingApplied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var meter in meters.DistinctBy(x => new { x.FuelType, x.IsExport, x.MeterPoint, x.MeterSerial, x.TariffCode }))
            {
                var flow = FlowFor(meter);
                var latest = await readingStore.GetLatestOctopusReadingAsync(meter.MeterPoint, meter.MeterSerial, flow, cancellationToken);
                var periods = PeriodsFor(tariffPeriods, meter);
                if (periods.Length == 0 && !string.IsNullOrWhiteSpace(meter.TariffCode)) periods = [new(meter.AccountNumber,meter.PropertyId,meter.FuelType,meter.IsExport,meter.MeterPoint,meter.TariffCode,meter.ProductCode,meter.ValidFrom,meter.ValidTo)];
                var from = latest?.AddDays(-2) ?? new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero);
                if (!OctopusAgreementCoverage.Spans(periods, latest ?? to, to))
                    pricingWarnings.Add($"Agreement coverage remains incomplete for {meter.FuelType} {(meter.IsExport ? "export" : "import")}.");
                var rates = new List<OctopusRate>(); var standing = new List<OctopusRate>();
                foreach (var period in periods)
                {
                    var periodFrom = period.ValidFrom is not null && period.ValidFrom > from ? period.ValidFrom.Value : from; var periodTo = period.ValidTo is not null && period.ValidTo < to ? period.ValidTo.Value : to; if (periodFrom >= periodTo) continue;
                    if (string.IsNullOrWhiteSpace(period.ProductCode)) { unavailableTariffs.Add(period.TariffCode); continue; }
                    try
                    {
                        var metadata = await LoadMetadata(period, settings.ApiKey, periodFrom, cancellationToken);
                        if (!metadata.CanPriceMeterIntervals)
                        {
                            if (metadata.Semantics == OctopusProductSemantics.FourRateEv)
                            {
                                pricingWarnings.Add($"{period.TariffCode}: four-rate EV pricing is withheld pending authoritative home/EV allocation. Historical repricing is disabled.");
                                foreach (var relation in new[] { "day_unit_rates", "night_unit_rates", "ev_device_peak_unit_rates", "ev_device_off_peak_unit_rates" })
                                    await ReadEndpoint(relation); // Inspect availability only; never flatten register rates into a meter rate.
                            }
                            else unavailableTariffs.Add(period.TariffCode);
                            continue;
                        }
                        rates.AddRange((await ReadEndpoint("standard_unit_rates")).Select(rate => rate with
                        {
                            Start = rate.Start < periodFrom ? periodFrom : rate.Start,
                            End = rate.End > periodTo ? periodTo : rate.End,
                            TariffCode = period.TariffCode,
                            Semantics = metadata.Semantics
                        }).Where(rate => rate.Start < rate.End));
                        var standingKey = $"{meter.AccountNumber}:{meter.PropertyId}:{meter.MeterPoint}:{period.TariffCode}";
                        if (meter.FuelType == "gas" && standingApplied.Add(standingKey))
                            standing.AddRange((await ReadEndpoint("standing_charges")).Select(rate => rate with
                            {
                                Start = rate.Start < periodFrom ? periodFrom : rate.Start,
                                End = rate.End > periodTo ? periodTo : rate.End,
                                TariffCode = period.TariffCode
                            }).Where(rate => rate.Start < rate.End));

                        async Task<List<OctopusRate>> ReadEndpoint(string relation)
                        {
                            if (!metadata.Endpoints.TryGetValue(relation, out var endpoint))
                            {
                                pricingWarnings.Add($"{period.TariffCode}: product metadata has no {relation} endpoint.");
                                return [];
                            }
                            var result = await LoadRates(endpoint, settings.ApiKey, periodFrom, periodTo, cancellationToken);
                            if (result.Count == 0) pricingWarnings.Add($"{period.TariffCode}: {relation} returned HTTP success with an empty rate collection for the requested period.");
                            return result;
                        }
                    }
                    catch(HttpRequestException ex) when(IsUnavailableRateEndpoint(ex)) { unavailableTariffs.Add(period.TariffCode); }
                }
                var values = await LoadConsumption(settings.ApiKey, meter.FuelType, meter.MeterPoint, meter.MeterSerial, from, to, cancellationToken);
                if (flow == EnergyFlowType.ElectricityExport) rates = rates.Select(x => x with { PencePerKwh = -x.PencePerKwh }).ToList();
                var raw = OctopusResponseParser.ToRaw(values, flow, rates, flow == EnergyFlowType.Gas && settings.GasCubicMetres ? 11.1868m : 1m, meter.MeterPoint, meter.MeterSerial, meter.TariffCode, periods);
                var newlyImported = await readingStore.InsertMissingOctopusRawReadingsAsync(raw, cancellationToken); imported += newlyImported.Count;
                if (raw.Any(reading => reading.CostGbp is null)) pricingWarnings.Add($"{meter.FuelType} {(meter.IsExport ? "export" : "import")}: some requested consumption intervals have no usable exact rate; consumption sync success does not imply pricing coverage.");
                if (newlyImported.Count > 0) { importedFrom = importedFrom is null || newlyImported[0].PeriodStart < importedFrom ? newlyImported[0].PeriodStart : importedFrom; importedTo = importedTo is null || newlyImported[^1].PeriodEnd > importedTo ? newlyImported[^1].PeriodEnd : importedTo; }
                if (standing.Count > 0)
                {
                    var charges = Enumerable.Range(0, Math.Max(0, (to.Date - from.Date).Days + 1)).Select(offset => DateOnly.FromDateTime(from.Date.AddDays(offset))).Select(date =>
                    {
                        var instant = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                        var matches = standing.Where(x => instant >= x.Start && instant < x.End).DistinctBy(x => (x.PencePerKwh,x.TariffCode)).ToArray();
                        var rate = matches.Length == 1 ? matches[0] : null;
                        return rate is null ? null : new OctopusStandingCharge(date, meter.MeterPoint, string.IsNullOrWhiteSpace(rate.TariffCode) ? meter.TariffCode : rate.TariffCode, rate.PencePerKwh / 100m);
                    }).Where(x => x is not null).Cast<OctopusStandingCharge>().ToArray();
                    await readingStore.InsertMissingOctopusStandingChargesAsync(charges, cancellationToken);
                }
            }
            await readingStore.RebuildOctopusRollupsAsync(cancellationToken);
            var message = imported == 0 ? "No new Octopus readings were available." : $"Imported {imported:N0} raw readings from {meters.Count} meter configuration(s).";
            message += " Existing consumption and historical interval pricing were preserved; historical repricing is disabled.";
            if(unavailableTariffs.Count>0) message += $" Exact rate endpoints were unavailable for {unavailableTariffs.Count} historical tariff(s); their usage remains visible without cost.";
            if (pricingWarnings.Count > 0) message += " " + string.Join(" ", pricingWarnings);
            return new(Name, true, imported, message, importedFrom, importedTo);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException) { return new(Name, false, 0, SafeMessage(ex)); }
    }

    public async Task<OctopusDiscoveryResult> DiscoverAccountAsync(CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettings(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.ApiKey)) throw new InvalidOperationException("Enter and save the Octopus API key before discovery.");
        var account = string.IsNullOrWhiteSpace(settings.Account) ? await DiscoverAccountNumber(settings.ApiKey, cancellationToken) : settings.Account;
        using var response = await SendAsync(new Uri(new Uri(BaseUrl), $"accounts/{Esc(account)}/"), settings.ApiKey, cancellationToken); response.EnsureSuccessStatusCode();
        var discovered = OctopusResponseParser.ParseAccountDiscovery(await response.Content.ReadAsStringAsync(cancellationToken), account);
        await configurationStore.ReplaceOctopusConfigurationAsync(discovered, cancellationToken); await repository.SetSettingAsync("octopus.discoveredAccount", account, false, cancellationToken);
        await repository.SetSettingAsync("octopus.agreementsCheckedAt", DateTimeOffset.UtcNow.ToString("O"), false, cancellationToken);
        return discovered;
    }

    public Task<IReadOnlyList<OctopusMeterPoint>> GetDiscoveredConfigurationAsync(CancellationToken cancellationToken = default) => configurationStore.GetOctopusMeterPointsAsync(cancellationToken);

    private static EnergyFlowType FlowFor(OctopusMeterPoint meter) => meter.FuelType == "gas" ? EnergyFlowType.Gas : meter.IsExport ? EnergyFlowType.ElectricityExport : EnergyFlowType.ElectricityImport;
    private static OctopusTariffPeriod[] PeriodsFor(IEnumerable<OctopusTariffPeriod> periods, OctopusMeterPoint meter)
        => periods.Where(period => period.AccountNumber == meter.AccountNumber && period.PropertyId == meter.PropertyId
            && period.FuelType == meter.FuelType && period.IsExport == meter.IsExport && period.MeterPoint == meter.MeterPoint).ToArray();

    private async Task<string> DiscoverAccountNumber(string apiKey, CancellationToken ct)
    {
        const string tokenQuery = "mutation ObtainKrakenToken($apiKey: String!) { obtainKrakenToken(input: { APIKey: $apiKey }) { token } }";
        using var tokenResponse = await SendGraphql(tokenQuery, new { apiKey }, null, ct); tokenResponse.EnsureSuccessStatusCode();
        using var tokenDocument = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(ct)); ThrowGraphqlErrors(tokenDocument.RootElement, "The Octopus API key was rejected.");
        var token = tokenDocument.RootElement.GetProperty("data").GetProperty("obtainKrakenToken").GetProperty("token").GetString();
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Octopus did not return an authentication token. Check the API key.");
        const string accountsQuery = "query ViewerAccounts { viewer { accounts { number } } }";
        using var accountResponse = await SendGraphql(accountsQuery, new { }, token, ct); accountResponse.EnsureSuccessStatusCode();
        using var accountDocument = JsonDocument.Parse(await accountResponse.Content.ReadAsStringAsync(ct)); ThrowGraphqlErrors(accountDocument.RootElement, "Octopus account discovery failed.");
        var accounts = accountDocument.RootElement.GetProperty("data").GetProperty("viewer").GetProperty("accounts").EnumerateArray().Select(x => x.GetProperty("number").GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().Distinct().ToArray();
        return accounts.Length switch { 0 => throw new InvalidOperationException("No Octopus account is associated with this API key."), 1 => accounts[0], _ => throw new InvalidOperationException("Multiple Octopus accounts were found. Enter the account number in Advanced Overrides and discover again.") };
    }

    private async Task<HttpResponseMessage> SendGraphql(string query, object variables, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(BaseUrl), "graphql/"));
        request.Content = new StringContent(JsonSerializer.Serialize(new { query, variables }), Encoding.UTF8, "application/json");
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new("JWT", token);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("JoakimHomeDashboard", "1.0")); return await httpClient.SendAsync(request, ct);
    }

    private static void ThrowGraphqlErrors(JsonElement root, string fallback)
    {
        if (!root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() == 0) return;
        var message = errors[0].TryGetProperty("message", out var detail) ? detail.GetString() : null;
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? fallback : $"{fallback} {message}");
    }

    private static void ApplyAdvancedOverrides(List<OctopusMeterPoint> meters, OctopusConfig settings)
    {
        var account = string.IsNullOrWhiteSpace(settings.Account) ? meters.FirstOrDefault()?.AccountNumber ?? "ADVANCED" : settings.Account;
        if (!string.IsNullOrWhiteSpace(settings.Mpan) && !string.IsNullOrWhiteSpace(settings.Serial)) Replace("electricity", false, settings.Mpan, settings.Serial, settings.Product, settings.Tariff);
        if (!string.IsNullOrWhiteSpace(settings.ExportMpan) && !string.IsNullOrWhiteSpace(settings.ExportSerial)) Replace("electricity", true, settings.ExportMpan, settings.ExportSerial, settings.ExportProduct, settings.ExportTariff);
        if (!string.IsNullOrWhiteSpace(settings.GasMprn) && !string.IsNullOrWhiteSpace(settings.GasSerial)) Replace("gas", false, settings.GasMprn, settings.GasSerial, settings.GasProduct, settings.GasTariff);
        void Replace(string fuel, bool export, string point, string serial, string product, string tariff)
        {
            if (meters.Any(x => x.FuelType == fuel && x.IsExport == export && x.MeterPoint == point && x.MeterSerial == serial)) return;
            meters.RemoveAll(x => x.FuelType == fuel && x.IsExport == export); meters.Add(new(account, 0, fuel, export, point, serial, tariff, product, null, null));
        }
    }

    private static IReadOnlyList<DailyEnergyReading> AggregateMeters(IEnumerable<DailyEnergyReading> readings)
        => readings.GroupBy(x => new { x.Date, x.FlowType }).Select(group => new DailyEnergyReading(group.Key.Date, group.Key.FlowType,
            decimal.Round(group.Sum(x => x.QuantityKwh), 3), decimal.Round(group.Sum(x => x.CostGbp), 4), "Octopus", $"octopus:{group.Key.FlowType}:{group.Key.Date:yyyy-MM-dd}",
            decimal.Round(group.Sum(x => x.StandingChargeGbp), 4), string.Join(", ", group.Select(x => x.TariffCode).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct()))).OrderBy(x => x.Date).ThenBy(x => x.FlowType).ToArray();

    private async Task<List<OctopusInterval>> LoadConsumption(string apiKey, string fuel, string meterPoint, string serial, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var intervals = new List<OctopusInterval>(); Uri? next = new(new Uri(BaseUrl), ConsumptionPath(fuel, meterPoint, serial, from, to));
        while (next is not null)
        {
            if (!string.Equals(next.Host, "api.octopus.energy", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Octopus returned an unexpected pagination host.");
            using var response = await SendAsync(next, apiKey, ct); response.EnsureSuccessStatusCode(); var json = await response.Content.ReadAsStringAsync(ct);
            var page = OctopusResponseParser.ParseConsumptionPage(json); intervals.AddRange(page.Intervals); next = string.IsNullOrWhiteSpace(page.Next) ? null : new Uri(page.Next);
        }
        return intervals;
    }

    private async Task<OctopusTariffMetadata> LoadMetadata(OctopusTariffPeriod period, string apiKey, DateTimeOffset at, CancellationToken ct)
    {
        var path = $"products/{Esc(period.ProductCode)}/?tariffs_active_at={Uri.EscapeDataString(at.ToString("O"))}";
        using var response = await SendAsync(new Uri(new Uri(BaseUrl), path), apiKey, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        var metadata = OctopusTariffMetadata.Parse(json, period.TariffCode);
        if (metadata.Semantics == OctopusProductSemantics.FourRateEv) await configurationStore.RegisterFourRateTariffAsync(period, json, ct);
        return metadata;
    }

    private async Task<List<OctopusRate>> LoadRates(Uri endpoint, string apiKey, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        OctopusTariffMetadata.ValidateEndpoint(endpoint);
        var builder = new UriBuilder(endpoint) { Query = $"page_size=1500&period_from={Uri.EscapeDataString(from.ToString("O"))}&period_to={Uri.EscapeDataString(to.ToString("O"))}" };
        var rates = new List<OctopusRate>(); Uri? next = builder.Uri;
        var visited = new HashSet<Uri>();
        while (next is not null)
        {
            OctopusTariffMetadata.ValidateEndpoint(next);
            if (next.AbsolutePath != endpoint.AbsolutePath || !visited.Add(next)) throw new InvalidOperationException("Octopus returned invalid tariff pagination.");
            using var response = await SendAsync(next, apiKey, ct); response.EnsureSuccessStatusCode(); var page = OctopusResponseParser.ParseRatePage(await response.Content.ReadAsStringAsync(ct)); rates.AddRange(page.Rates); next = string.IsNullOrWhiteSpace(page.Next) ? null : new Uri(page.Next);
        }
        return rates;
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, string apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri); request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{apiKey}:")));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("JoakimHomeDashboard", "1.0")); return await httpClient.SendAsync(request, ct);
    }

    private async Task<OctopusConfig> LoadSettings(CancellationToken ct) => new(
        await Get(SettingKeys.OctopusApiKey), await Get(SettingKeys.OctopusMpan), await Get(SettingKeys.OctopusSerial),
        await Get(SettingKeys.OctopusExportMpan), await Get(SettingKeys.OctopusExportSerial), await Get(SettingKeys.OctopusExportProduct), await Get(SettingKeys.OctopusExportTariff), await Get(SettingKeys.OctopusAccount),
        await Get(SettingKeys.OctopusProduct), await Get(SettingKeys.OctopusTariff), await Get(SettingKeys.OctopusGasMprn), await Get(SettingKeys.OctopusGasSerial),
        await Get(SettingKeys.OctopusGasProduct), await Get(SettingKeys.OctopusGasTariff), bool.TryParse(await Get(SettingKeys.OctopusGasCubicMetres), out var cubic) && cubic);
    private async Task<string> Get(string key) => await repository.GetSettingAsync(key) ?? "";
    private static string ConsumptionPath(string fuel, string meterPoint, string serial, DateTimeOffset from, DateTimeOffset to) => $"{fuel}-meter-points/{Esc(meterPoint)}/meters/{Esc(serial)}/consumption/?page_size=25000&period_from={Uri.EscapeDataString(from.ToString("O"))}&period_to={Uri.EscapeDataString(to.ToString("O"))}&order_by=period";
    private static string Esc(string value) => Uri.EscapeDataString(value.Trim());
    private static string SafeMessage(Exception ex) => ex is HttpRequestException http && http.StatusCode is not null ? $"Octopus returned HTTP {(int)http.StatusCode} ({http.StatusCode}). Check credentials and meter identifiers." : ex.Message;
    private static bool IsUnavailableRateEndpoint(HttpRequestException ex) => ex.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.NotFound;
    private sealed record OctopusConfig(string ApiKey, string Mpan, string Serial, string ExportMpan, string ExportSerial, string ExportProduct, string ExportTariff, string Account, string Product, string Tariff, string GasMprn, string GasSerial, string GasProduct, string GasTariff, bool GasCubicMetres);
}

public sealed record OctopusInterval(DateTimeOffset Start, DateTimeOffset End, decimal ConsumptionKwh);
public sealed record OctopusRate(DateTimeOffset Start, DateTimeOffset End, decimal PencePerKwh, string TariffCode = "", OctopusProductSemantics Semantics = OctopusProductSemantics.StandardIntervalRates);
public sealed record OctopusPage(IReadOnlyList<OctopusInterval> Intervals, string? Next);
public sealed record OctopusRatePage(IReadOnlyList<OctopusRate> Rates, string? Next);

public static class OctopusResponseParser
{
    public static OctopusPage ParseConsumptionPage(string json)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement; var values = new List<OctopusInterval>();
        foreach (var item in root.GetProperty("results").EnumerateArray()) values.Add(new(
            item.GetProperty("interval_start").GetDateTimeOffset(), item.GetProperty("interval_end").GetDateTimeOffset(), item.GetProperty("consumption").GetDecimal()));
        return new(values, root.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null);
    }

    public static List<OctopusRate> ParseRates(string json)
        => ParseRatePage(json).Rates.ToList();

    public static OctopusRatePage ParseRatePage(string json)
    {
        using var document = JsonDocument.Parse(json); var rates = new List<OctopusRate>();
        foreach (var item in document.RootElement.GetProperty("results").EnumerateArray()) rates.Add(new(item.GetProperty("valid_from").GetDateTimeOffset(), item.TryGetProperty("valid_to", out var end) && end.ValueKind == JsonValueKind.String ? end.GetDateTimeOffset() : DateTimeOffset.MaxValue, item.GetProperty("value_inc_vat").GetDecimal()));
        return new(rates, document.RootElement.TryGetProperty("next",out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null);
    }

    public static IReadOnlyList<DailyEnergyReading> ToDaily(IEnumerable<OctopusInterval> intervals, EnergyFlowType flow, IReadOnlyCollection<OctopusRate> rates, IReadOnlyCollection<OctopusRate>? standingCharges = null, decimal quantityMultiplier = 1m, string tariffCode = "")
        => intervals.GroupBy(x => DateOnly.FromDateTime(x.Start.Date)).Select(day =>
        {
            var usageCost = day.Sum(interval => interval.ConsumptionKwh * quantityMultiplier * (rates.FirstOrDefault(rate => interval.Start >= rate.Start && interval.Start < rate.End)?.PencePerKwh ?? 0m) / 100m);
            var first = day.Min(x => x.Start); var standing = (standingCharges ?? []).FirstOrDefault(rate => first >= rate.Start && first < rate.End)?.PencePerKwh / 100m ?? 0m;
            return new DailyEnergyReading(day.Key, flow, decimal.Round(day.Sum(x => x.ConsumptionKwh) * quantityMultiplier, 3), decimal.Round(usageCost + standing, 4), "Octopus", $"octopus:{flow}:{day.Key:yyyy-MM-dd}", decimal.Round(standing, 4), tariffCode);
        }).OrderBy(x => x.Date).ToArray();

    public static IReadOnlyList<OctopusRawReading> ToRaw(IEnumerable<OctopusInterval> intervals, EnergyFlowType flow, IReadOnlyCollection<OctopusRate> rates, decimal quantityMultiplier, string meterPoint, string meterSerial, string tariffCode, IReadOnlyCollection<OctopusTariffPeriod>? agreements = null)
        => intervals.OrderBy(x => x.Start).Select(interval =>
        {
            var quantity = decimal.Round(interval.ConsumptionKwh * quantityMultiplier, 6);
            var agreementMatches = agreements?.Where(period => interval.Start >= (period.ValidFrom ?? DateTimeOffset.MinValue)
                && interval.End <= (period.ValidTo ?? DateTimeOffset.MaxValue)).ToArray();
            var agreement = agreementMatches?.Length == 1 ? agreementMatches[0] : null;
            var matches = rates.Where(item => item.Semantics is OctopusProductSemantics.StandardIntervalRates or OctopusProductSemantics.IntelligentGoIntervalRates)
                .Where(item => interval.Start >= item.Start && interval.End <= item.End
                    && (agreements is null || agreement is not null && item.TariffCode == agreement.TariffCode)).Distinct().ToArray();
            var rate = matches.Length == 1 ? matches[0] : null;
            var cost = rate is null ? (decimal?)null : decimal.Round(quantity * rate.PencePerKwh / 100m, 6);
            var matchedTariff = rate?.TariffCode ?? agreement?.TariffCode ?? (agreements is null ? tariffCode : "");
            if (string.IsNullOrWhiteSpace(matchedTariff) && agreements is null) matchedTariff = tariffCode;
            var band = flow == EnergyFlowType.ElectricityImport && rate is not null ? ClassifyImportRate(rate,rates,matchedTariff) : ImportRateBand.Unknown;
            return new OctopusRawReading(interval.Start, interval.End, flow, quantity, cost, meterPoint, meterSerial, matchedTariff, $"octopus:raw:{flow}:{meterPoint}:{meterSerial}:{interval.Start:O}",band,rate?.PencePerKwh);
        }).ToArray();

    public static ImportRateBand ClassifyImportRate(OctopusRate matchedRate, IReadOnlyCollection<OctopusRate> rates, string tariffCode)
    {
        if (matchedRate.Semantics != OctopusProductSemantics.IntelligentGoIntervalRates) return ImportRateBand.Unknown;
        var tariffRates=rates.Where(rate=>rate.Semantics == OctopusProductSemantics.IntelligentGoIntervalRates && (string.IsNullOrWhiteSpace(rate.TariffCode)||string.Equals(rate.TariffCode,matchedRate.TariffCode,StringComparison.OrdinalIgnoreCase))).Select(rate=>rate.PencePerKwh).Distinct().Order().ToArray();
        if(tariffRates.Length<2) return ImportRateBand.Unknown; var split=Enumerable.Range(0,tariffRates.Length-1).Select(index=>new { Index=index,Gap=tariffRates[index+1]-tariffRates[index] }).MaxBy(value=>value.Gap);
        if(split is null||split.Gap<3m) return ImportRateBand.Unknown; var threshold=(tariffRates[split.Index]+tariffRates[split.Index+1])/2m;
        return matchedRate.PencePerKwh<=threshold?ImportRateBand.OffPeak:ImportRateBand.Peak;
    }

    public static OctopusGasDiscovery ParseGasDiscovery(string json)
    {
        using var document = JsonDocument.Parse(json); var account = document.RootElement.TryGetProperty("number", out var number) ? number.GetString() ?? "" : "";
        var gas = ParseAccountDiscovery(json, account).MeterPoints.FirstOrDefault(x => x.FuelType == "gas") ?? throw new InvalidOperationException("No active gas meter was found on the Octopus account.");
        return new(gas.MeterPoint, gas.MeterSerial, gas.ProductCode, gas.TariffCode);
    }

    public static OctopusDiscoveryResult ParseAccountDiscovery(string json, string accountNumber)
    {
        using var document = JsonDocument.Parse(json); var now = DateTimeOffset.UtcNow; var meters = new List<OctopusMeterPoint>(); var tariffPeriods = new List<OctopusTariffPeriod>();
        var properties = document.RootElement.GetProperty("properties").EnumerateArray().Where(property => !property.TryGetProperty("moved_out_at", out var movedOut) || movedOut.ValueKind == JsonValueKind.Null || movedOut.GetDateTimeOffset() > now).ToArray();
        foreach (var property in properties)
        {
            var propertyId = property.TryGetProperty("id", out var id) && id.TryGetInt64(out var value) ? value : 0;
            if (property.TryGetProperty("electricity_meter_points", out var electricity)) foreach (var point in electricity.EnumerateArray()) AddPoint(point, "electricity", point.TryGetProperty("is_export", out var export) && export.GetBoolean(), "mpan", propertyId);
            if (property.TryGetProperty("gas_meter_points", out var gas)) foreach (var point in gas.EnumerateArray()) AddPoint(point, "gas", false, "mprn", propertyId);
        }
        var warnings = new List<string>(); if (properties.Length > 1) warnings.Add($"Multiple active properties found ({properties.Length}); all meter points will be synchronized.");
        if (!meters.Any(x => x.FuelType == "gas")) warnings.Add("No active gas meter found."); if (!meters.Any(x => x.FuelType == "electricity" && x.IsExport)) warnings.Add("No electricity export meter found.");
        return new(accountNumber, properties.Length, meters, warnings, tariffPeriods);

        void AddPoint(JsonElement point, string fuel, bool isExport, string pointKey, long propertyId)
        {
            var meterPoint = point.TryGetProperty(pointKey, out var pointValue) ? pointValue.GetString() ?? "" : ""; if (string.IsNullOrWhiteSpace(meterPoint)) return;
            var agreementValues = point.TryGetProperty("agreements", out var agreements) ? agreements.EnumerateArray().Select(item => new
            {
                Element = item,
                From = item.TryGetProperty("valid_from", out var from) && from.ValueKind == JsonValueKind.String ? from.GetDateTimeOffset() : (DateTimeOffset?)null,
                To = item.TryGetProperty("valid_to", out var to) && to.ValueKind == JsonValueKind.String ? to.GetDateTimeOffset() : (DateTimeOffset?)null
            }).ToArray() : [];
            foreach (var period in agreementValues)
            {
                var periodTariff = period.Element.TryGetProperty("tariff_code", out var code) ? code.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(periodTariff)) tariffPeriods.Add(new(accountNumber, propertyId, fuel, isExport, meterPoint, periodTariff, DeriveProductCode(periodTariff), period.From, period.To));
            }
            var agreement = agreementValues.Where(x => (x.From is null || x.From <= now) && (x.To is null || x.To > now)).OrderByDescending(x => x.From).FirstOrDefault();
            var tariff = agreement?.Element.TryGetProperty("tariff_code", out var tariffValue) == true ? tariffValue.GetString() ?? "" : ""; var product = DeriveProductCode(tariff);
            if (!point.TryGetProperty("meters", out var serials)) return;
            foreach (var serial in serials.EnumerateArray())
            {
                var serialNumber = serial.TryGetProperty("serial_number", out var serialValue) ? serialValue.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(serialNumber)) meters.Add(new(accountNumber, propertyId, fuel, isExport, meterPoint, serialNumber, tariff, product, agreement?.From, agreement?.To));
            }
        }
    }

    private static string DeriveProductCode(string tariffCode)
    {
        var parts = tariffCode.Split('-', StringSplitOptions.RemoveEmptyEntries); return parts.Length > 4 ? string.Join('-', parts.Skip(2).SkipLast(1)) : "";
    }
}
