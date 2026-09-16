using JoakimHomeDashboard.Application;
using JoakimHomeDashboard.Domain;

namespace JoakimHomeDashboard.Tests;

public sealed class EnergyDashboardCardTests
{
    [Fact]
    public void YearEnergyMode_RendersNetGridUsageNotNetElectricityCost()
    {
        var cards = EnergyDashboardCardFactory.BuildPeriodCards(
            "YEAR", 17.20m, 468.42m, 0m,
            new EnergyCostPeriod(12.34m, 70.26m, 0m, true, true, true),
            "2026", EnergyViewMode.Energy);

        var net = Assert.Single(cards, card => card.Label == "YEAR NET GRID USAGE");
        Assert.Equal("-451.22 kWh", net.Value);
        Assert.Equal("2026 · grid import minus export", net.Note);
        Assert.DoesNotContain(cards, card => card.Label == "YEAR NET ELECTRICITY");
        Assert.DoesNotContain(net.Note, "Octopus tariff cost", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void YearCostMode_RendersNetElectricityCostInPounds()
    {
        var cards = EnergyDashboardCardFactory.BuildPeriodCards(
            "YEAR", 17.20m, 468.42m, 0m,
            new EnergyCostPeriod(12.34m, 70.26m, 0m, true, true, true),
            "2026", EnergyViewMode.Cost);

        var net = Assert.Single(cards, card => card.Label == "YEAR NET ELECTRICITY COST");
        Assert.Equal("-£57.92", net.Value);
        Assert.Equal("2026 · Octopus tariff cost", net.Note);
    }
}
