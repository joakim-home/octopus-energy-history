using System.Text.Json;
using JoakimHomeDashboard.Application;

namespace JoakimHomeDashboard.Infrastructure;

public enum OctopusProductSemantics
{
    Unsupported,
    StandardIntervalRates,
    IntelligentGoIntervalRates,
    DualRegister,
    FourRateEv
}

public sealed record OctopusTariffMetadata(OctopusProductSemantics Semantics, IReadOnlyDictionary<string, Uri> Endpoints)
{
    public bool CanPriceMeterIntervals => Semantics is OctopusProductSemantics.StandardIntervalRates or OctopusProductSemantics.IntelligentGoIntervalRates;

    public static OctopusTariffMetadata Parse(string json, string tariffCode)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var groups = new[] { "four_rate_ev_electricity_tariffs", "dual_register_electricity_tariffs", "single_register_electricity_tariffs", "gas_tariffs" };
        foreach (var group in groups)
        {
            if (!root.TryGetProperty(group, out var regions) || regions.ValueKind != JsonValueKind.Object) continue;
            var matches = regions.EnumerateObject().SelectMany(region => region.Value.EnumerateObject())
                .Select(payment => payment.Value).Where(tariff => tariff.TryGetProperty("code", out var code) && code.GetString() == tariffCode).ToArray();
            if (matches.Length == 0) continue;
            var endpoints = new Dictionary<string, Uri>(StringComparer.Ordinal);
            foreach (var match in matches)
            {
                if (!match.TryGetProperty("links", out var links)) continue;
                foreach (var link in links.EnumerateArray())
                {
                    if (link.GetProperty("method").GetString() != "GET") continue;
                    var rel = link.GetProperty("rel").GetString()!;
                    var uri = new Uri(link.GetProperty("href").GetString()!, UriKind.Absolute);
                    ValidateEndpoint(uri);
                    if (endpoints.TryGetValue(rel, out var existing) && existing != uri)
                        throw new InvalidOperationException("Octopus returned ambiguous payment-method rate endpoints.");
                    endpoints[rel] = uri;
                }
            }
            // The register shape is decisive: a four-rate product must never enter the legacy adapter.
            var semantics = group switch
            {
                "four_rate_ev_electricity_tariffs" => OctopusProductSemantics.FourRateEv,
                "dual_register_electricity_tariffs" => OctopusProductSemantics.DualRegister,
                "single_register_electricity_tariffs" when root.TryGetProperty("display_name", out var name)
                    && name.GetString() is "Intelligent Octopus Go" or "Intelligent Octopus" => OctopusProductSemantics.IntelligentGoIntervalRates,
                _ => OctopusProductSemantics.StandardIntervalRates
            };
            return new(semantics, endpoints);
        }
        return new(OctopusProductSemantics.Unsupported, new Dictionary<string, Uri>());
    }

    public static void ValidateEndpoint(Uri uri)
    {
        if (uri.Scheme != "https" || uri.Host != "api.octopus.energy" || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.AbsolutePath.StartsWith("/v1/products/", StringComparison.Ordinal))
            throw new InvalidOperationException("Octopus returned an unexpected tariff endpoint.");
    }
}

public static class OctopusAgreementCoverage
{
    public static bool Spans(IEnumerable<OctopusTariffPeriod> periods, DateTimeOffset from, DateTimeOffset to)
    {
        var cursor = from;
        foreach (var period in periods.OrderBy(period => period.ValidFrom ?? DateTimeOffset.MinValue))
        {
            var start = period.ValidFrom ?? DateTimeOffset.MinValue;
            var end = period.ValidTo ?? DateTimeOffset.MaxValue;
            if (end <= cursor || end <= start) continue;
            if (start > cursor) return false;
            cursor = end;
            // Current coverage must also include the instant 'to', not expire exactly at it.
            if (cursor > to) return true;
        }
        return false;
    }
}
