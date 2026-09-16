using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JoakimHomeDashboard.Application;
using JoakimHomeDashboard.Domain;

namespace JoakimHomeDashboard.Infrastructure;

public sealed class EnphaseDataSource(IDashboardRepository repository, HttpClient httpClient) : IProviderConnector, IEnphaseAuthorizationService
{
    private const string Authority = "https://api.enphaseenergy.com/";
    public string Name => "Enphase";

    public async Task<ConnectionTestResult> AuthorizeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var config = await LoadConfig(cancellationToken); EnsureOAuthConfigured(config);
            var redirect = NormalizeRedirect(config.RedirectUri); var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            var authorize = new Uri($"{Authority}oauth/authorize?response_type=code&client_id={Esc(config.ClientId)}&redirect_uri={Esc(redirect)}&state={Esc(state)}");
            using var listener = new HttpListener(); listener.Prefixes.Add(redirect); listener.Start();
            Process.Start(new ProcessStartInfo(authorize.AbsoluteUri) { UseShellExecute = true });
            var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(3), cancellationToken);
            var query = context.Request.QueryString; var returnedState = query["state"]; var code = query["code"]; var error = query["error"];
            if (!string.Equals(state, returnedState, StringComparison.Ordinal)) { await WriteCallbackResponse(context.Response, "Authorization failed state validation. Return to the app and try again.", cancellationToken); return new(false, "Enphase OAuth state validation failed."); }
            if (!string.IsNullOrWhiteSpace(error)) { await WriteCallbackResponse(context.Response, "Authorization was cancelled or rejected. Return to the app to retry.", cancellationToken); return new(false, $"Enphase authorization was not completed: {error}."); }
            if (string.IsNullOrWhiteSpace(code)) { await WriteCallbackResponse(context.Response, "No authorization code was returned. Return to the app to retry.", cancellationToken); return new(false, "Enphase did not return an authorization code."); }
            try
            {
                await ExchangeToken(config, new() { ["grant_type"] = "authorization_code", ["redirect_uri"] = redirect, ["code"] = code }, cancellationToken);
                var system = await DiscoverSystem(cancellationToken); await WriteCallbackResponse(context.Response, "Joakim Home Dashboard is connected to Enphase. You can close this tab.", cancellationToken);
                return new(true, $"Connected to Enphase system {system.SystemId} ({system.Name}).");
            }
            catch { await WriteCallbackResponse(context.Response, "Enphase returned an error while completing authorization. Return to the app for details.", cancellationToken); throw; }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or HttpListenerException or TimeoutException) { return new(false, SafeMessage(ex)); }
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try { var system = await DiscoverSystem(cancellationToken); return new(true, $"Connected to Enphase system {system.SystemId} ({system.Name})."); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException) { return new(false, SafeMessage(ex)); }
    }

    public async Task<SyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        try
        {
            var config = await LoadConfig(cancellationToken); EnsureApiConfigured(config); var system = await DiscoverSystem(cancellationToken);
            var timezone = FindTimezone(system.Timezone); var from = DateTimeOffset.UtcNow.AddDays(-31); var to = DateTimeOffset.UtcNow;
            var readings = new List<DailyEnergyReading>();
            readings.AddRange(await LoadTelemetry(system.SystemId, "production_meter", EnergyFlowType.SolarGeneration, from, to, config.ApiKey, timezone, cancellationToken));
            readings.AddRange(await LoadTelemetry(system.SystemId, "consumption_meter", EnergyFlowType.SiteConsumption, from, to, config.ApiKey, timezone, cancellationToken));
            await repository.UpsertDailyEnergyAsync(readings, cancellationToken);
            return new(Name, true, readings.Count, $"Synced {readings.Count} normalized daily production/consumption readings for system {system.SystemId}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException) { return new(Name, false, 0, SafeMessage(ex)); }
    }

    private async Task<IReadOnlyList<DailyEnergyReading>> LoadTelemetry(string systemId, string meter, EnergyFlowType flow, DateTimeOffset from, DateTimeOffset to, string apiKey, TimeZoneInfo timezone, CancellationToken ct)
    {
        var readings = new List<DailyEnergyReading>(); var cursor = from;
        while (cursor < to)
        {
            var end = cursor.AddDays(7) < to ? cursor.AddDays(7) : to;
            var uri = new Uri($"{Authority}api/v4/systems/{Esc(systemId)}/telemetry/{meter}?key={Esc(apiKey)}&start_at={cursor.ToUnixTimeSeconds()}&end_at={end.ToUnixTimeSeconds()}");
            using var response = await SendAuthorized(uri, ct); response.EnsureSuccessStatusCode();
            readings.AddRange(EnphaseResponseParser.ParseTelemetry(await response.Content.ReadAsStringAsync(ct), flow, timezone)); cursor = end;
        }
        return readings.GroupBy(x => x.Date).Select(group => new DailyEnergyReading(group.Key, flow, group.Sum(x => x.QuantityKwh), 0, "Enphase", $"enphase:{flow}:{group.Key:yyyy-MM-dd}")).OrderBy(x => x.Date).ToArray();
    }

    private async Task<EnphaseSystem> DiscoverSystem(CancellationToken ct)
    {
        var config = await LoadConfig(ct); EnsureApiConfigured(config); var token = await EnsureAccessToken(config, ct);
        var uri = new Uri($"{Authority}api/v4/systems?key={Esc(config.ApiKey)}");
        using var response = await SendBearer(uri, token, ct); response.EnsureSuccessStatusCode();
        var systems = EnphaseResponseParser.ParseSystems(await response.Content.ReadAsStringAsync(ct));
        var selected = systems.FirstOrDefault(x => x.SystemId == config.SystemId) ?? systems.FirstOrDefault() ?? throw new InvalidOperationException("No Enphase systems are available for this account.");
        await repository.SetSettingAsync(SettingKeys.EnphaseSystemId, selected.SystemId, false, ct); return selected;
    }

    private async Task<string> EnsureAccessToken(EnphaseConfig config, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(config.AccessToken) && DateTimeOffset.TryParse(config.ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expires) && expires > DateTimeOffset.UtcNow.AddMinutes(5)) return config.AccessToken;
        if (string.IsNullOrWhiteSpace(config.RefreshToken)) throw new InvalidOperationException("Connect with Enphase OAuth before testing or syncing.");
        return await ExchangeToken(config, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = config.RefreshToken }, ct);
    }

    private async Task<string> ExchangeToken(EnphaseConfig config, Dictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{Authority}oauth/token")) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.ClientId}:{config.ClientSecret}")));
        using var response = await httpClient.SendAsync(request, ct); response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); var root = document.RootElement;
        var access = root.GetProperty("access_token").GetString() ?? throw new JsonException("Enphase token response omitted access_token.");
        var refresh = root.GetProperty("refresh_token").GetString() ?? throw new JsonException("Enphase token response omitted refresh_token.");
        var expiresIn = root.TryGetProperty("expires_in", out var expiry) ? expiry.GetInt32() : 86_400;
        await repository.SetSettingAsync(SettingKeys.EnphaseAccessToken, access, true, ct); await repository.SetSettingAsync(SettingKeys.EnphaseRefreshToken, refresh, true, ct);
        await repository.SetSettingAsync(SettingKeys.EnphaseExpiresAt, DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToString("O"), false, ct); return access;
    }

    private async Task<HttpResponseMessage> SendAuthorized(Uri uri, CancellationToken ct) { var config = await LoadConfig(ct); var token = await EnsureAccessToken(config, ct); return await SendBearer(uri, token, ct); }
    private async Task<HttpResponseMessage> SendBearer(Uri uri, string token, CancellationToken ct) { using var request = new HttpRequestMessage(HttpMethod.Get, uri); request.Headers.Authorization = new("Bearer", token); request.Headers.UserAgent.Add(new ProductInfoHeaderValue("JoakimHomeDashboard", "1.0")); return await httpClient.SendAsync(request, ct); }

    private async Task<EnphaseConfig> LoadConfig(CancellationToken ct) => new(
        await Get(SettingKeys.EnphaseClientId, ct), await Get(SettingKeys.EnphaseClientSecret, ct), await Get(SettingKeys.EnphaseApiKey, ct),
        await Get(SettingKeys.EnphaseRedirect, ct) is { Length: > 0 } uri ? uri : "http://127.0.0.1:53682/callback/", await Get(SettingKeys.EnphaseSystemId, ct),
        await Get(SettingKeys.EnphaseAccessToken, ct), await Get(SettingKeys.EnphaseRefreshToken, ct), await Get(SettingKeys.EnphaseExpiresAt, ct));
    private async Task<string> Get(string key, CancellationToken ct) => await repository.GetSettingAsync(key, ct) ?? "";
    private static void EnsureOAuthConfigured(EnphaseConfig c) { if (string.IsNullOrWhiteSpace(c.ClientId) || string.IsNullOrWhiteSpace(c.ClientSecret) || string.IsNullOrWhiteSpace(c.ApiKey)) throw new InvalidOperationException("Configure the Enphase client ID, client secret and API key first."); }
    private static void EnsureApiConfigured(EnphaseConfig c) { EnsureOAuthConfigured(c); if (string.IsNullOrWhiteSpace(c.RefreshToken)) throw new InvalidOperationException("Connect with Enphase OAuth before testing or syncing."); }
    private static string NormalizeRedirect(string value) => value.EndsWith('/') ? value : value + "/";
    private static string Esc(string value) => Uri.EscapeDataString(value.Trim());
    private static TimeZoneInfo FindTimezone(string id) { try { return string.IsNullOrWhiteSpace(id) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(id); } catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; } catch (InvalidTimeZoneException) { return TimeZoneInfo.Utc; } }
    private static async Task WriteCallbackResponse(HttpListenerResponse response, string message, CancellationToken ct) { var bytes = Encoding.UTF8.GetBytes($"<!doctype html><title>Joakim Home Dashboard</title><body style='font:18px sans-serif;padding:40px;background:#0b1019;color:#f5f7fa'>{WebUtility.HtmlEncode(message)}</body>"); response.ContentType = "text/html; charset=utf-8"; response.ContentLength64 = bytes.Length; await response.OutputStream.WriteAsync(bytes, ct); response.Close(); }
    private static string SafeMessage(Exception ex) => ex is HttpRequestException http && http.StatusCode is not null ? $"Enphase returned HTTP {(int)http.StatusCode} ({http.StatusCode}). Check authorization, API plan access and system permissions." : ex.Message;
    private sealed record EnphaseConfig(string ClientId, string ClientSecret, string ApiKey, string RedirectUri, string SystemId, string AccessToken, string RefreshToken, string ExpiresAt);
}

public sealed record EnphaseSystem(string SystemId, string Name, string Timezone);

public static class EnphaseResponseParser
{
    public static IReadOnlyList<EnphaseSystem> ParseSystems(string json)
    {
        using var document = JsonDocument.Parse(json); if (!document.RootElement.TryGetProperty("systems", out var systems) || systems.ValueKind != JsonValueKind.Array) throw new JsonException("Enphase systems response omitted systems.");
        return systems.EnumerateArray().Select(x => new EnphaseSystem(ReadId(x.GetProperty("system_id")), x.TryGetProperty("name", out var name) ? name.GetString() ?? "Enphase system" : "Enphase system", x.TryGetProperty("timezone", out var zone) ? zone.GetString() ?? "UTC" : "UTC")).ToArray();
    }

    public static IReadOnlyList<DailyEnergyReading> ParseTelemetry(string json, EnergyFlowType flow, TimeZoneInfo timezone)
    {
        using var document = JsonDocument.Parse(json); var intervals = FindArray(document.RootElement, "intervals") ?? throw new JsonException("Enphase telemetry response omitted intervals.");
        var values = new List<(DateOnly Date, decimal Kwh)>();
        foreach (var item in intervals.EnumerateArray())
        {
            if (!TryTimestamp(item, out var timestamp) || !TryWattHours(item, out var wattHours)) continue;
            var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(timestamp), timezone); values.Add((DateOnly.FromDateTime(local.Date), wattHours / 1000m));
        }
        return values.GroupBy(x => x.Date).Select(x => new DailyEnergyReading(x.Key, flow, decimal.Round(x.Sum(v => v.Kwh), 3), 0, "Enphase", $"enphase:{flow}:{x.Key:yyyy-MM-dd}")).ToArray();
    }

    private static JsonElement? FindArray(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object) foreach (var property in element.EnumerateObject()) { if (property.NameEquals(name) && property.Value.ValueKind == JsonValueKind.Array) return property.Value; var found = FindArray(property.Value, name); if (found is not null) return found; }
        if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) { var found = FindArray(child, name); if (found is not null) return found; }
        return null;
    }
    private static bool TryTimestamp(JsonElement item, out long value)
    {
        if (item.TryGetProperty("end_at", out var end) && end.TryGetInt64(out value)) { value -= 1; return true; }
        foreach (var key in new[] { "start_at", "timestamp" }) if (item.TryGetProperty(key, out var element) && element.TryGetInt64(out value)) return true;
        value = 0; return false;
    }
    private static bool TryWattHours(JsonElement item, out decimal value)
    {
        foreach (var key in new[] { "wh_del", "enwh", "watt_hours", "energy" })
            if (item.TryGetProperty(key, out var element) && (element.TryGetDecimal(out value) || element.ValueKind == JsonValueKind.String && decimal.TryParse(element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value))) return true;
        value = 0; return false;
    }
    private static string ReadId(JsonElement element) => element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText();
}
