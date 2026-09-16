using System.Globalization;
using JoakimHomeDashboard.Domain;

namespace JoakimHomeDashboard.Application;

public sealed record EnergyDashboardCard(string Label, string Value, string Note, string Accent);

public static class EnergyDashboardCardFactory
{
    private static readonly CultureInfo Gbp = CultureInfo.GetCultureInfo("en-GB");

    public static IReadOnlyList<EnergyDashboardCard> BuildPeriodCards(string period, decimal importKwh, decimal exportKwh, decimal gasKwh, EnergyCostPeriod costs, string dateRange, EnergyViewMode viewMode)
    {
        var netGridKwh = importKwh - exportKwh;
        var electricityNetCost = costs.ImportCost - costs.ExportIncome;
        var electricityNetExact = costs.ImportExact && costs.ExportExact;
        var netCard = viewMode == EnergyViewMode.Cost
            ? new EnergyDashboardCard($"{period} NET ELECTRICITY COST", CostText(electricityNetCost, electricityNetExact), CostNote(electricityNetExact, dateRange), "#57B8FF")
            : new EnergyDashboardCard($"{period} NET GRID USAGE", $"{netGridKwh:N2} kWh", $"{dateRange} · grid import minus export", "#57B8FF");

        return
        [
            netCard,
            new($"{period} IMPORT", Display(importKwh, costs.ImportCost, costs.ImportExact, viewMode), Note("grid import", costs.ImportExact, dateRange, viewMode), "#7C5CFC"),
            new($"{period} EXPORT", Display(exportKwh, costs.ExportIncome, costs.ExportExact, viewMode), Note("export", costs.ExportExact, dateRange, viewMode, true), "#37D7A0"),
            new($"{period} GAS", Display(gasKwh, costs.GasCost, costs.GasExact, viewMode), Note("gas usage", costs.GasExact, dateRange, viewMode), "#FFB454")
        ];
    }

    private static string Display(decimal energyKwh, decimal costGbp, bool exact, EnergyViewMode viewMode) => viewMode switch
    {
        EnergyViewMode.Energy => $"{energyKwh:N2} kWh",
        EnergyViewMode.Cost => CostText(costGbp, exact),
        _ => $"{energyKwh:N2} kWh · {CostText(costGbp, exact)}"
    };

    private static string Note(string energyDescription, bool exact, string dateRange, EnergyViewMode viewMode, bool income = false)
        => viewMode == EnergyViewMode.Cost ? CostNote(exact, dateRange, income) : $"{dateRange} · {energyDescription}";

    private static string CostText(decimal value, bool exact) => exact ? value.ToString("C2", Gbp) : value == 0 ? "Cost unavailable" : $"≈ {value.ToString("C2", Gbp)}";
    private static string CostNote(bool exact, string dateRange, bool income = false) => exact ? $"{dateRange} · Octopus tariff {(income ? "income" : "cost")}" : $"{dateRange} · Estimated/partial tariff coverage";
}
