using System.Net;
using System.Text;
using System.Text.Json;

namespace JoakimHomeDashboard.Tests;

internal static class OctopusMetadataFixture
{
    public static HttpResponseMessage Response(string product, string tariff, string group = "single_register_electricity_tariffs", string name = "Intelligent Octopus Go")
        => new(HttpStatusCode.OK) { Content = new StringContent(Json(product, tariff, group, name), Encoding.UTF8, "application/json") };

    public static string Json(string product, string tariff, string group = "single_register_electricity_tariffs", string name = "Intelligent Octopus Go")
    {
        var fuel = group == "gas_tariffs" ? "gas" : "electricity";
        var relations = group == "four_rate_ev_electricity_tariffs"
            ? new[] { "day_unit_rates", "night_unit_rates", "ev_device_peak_unit_rates", "ev_device_off_peak_unit_rates", "standing_charges" }
            : new[] { "standard_unit_rates", "standing_charges" };
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["code"] = product, ["display_name"] = name,
            [group] = new Dictionary<string, object> { ["_A"] = new { direct_debit_monthly = new
            {
                code = tariff,
                links = relations.Select(rel => new { rel, method = "GET", href = $"https://api.octopus.energy/v1/products/{product}/{fuel}-tariffs/{tariff}/{rel.Replace('_', '-')}/" })
            } } }
        });
    }
}
