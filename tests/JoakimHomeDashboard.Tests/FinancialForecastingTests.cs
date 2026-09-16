using JoakimHomeDashboard.Application;

namespace JoakimHomeDashboard.Tests;

public sealed class FinancialForecastingTests
{
    [Fact]
    public void SolarPayback_IsBasedOnElevenThousandPoundCost()
        => Assert.Equal(25m, FinancialForecasting.SolarPaybackPercent(2_750m));

    [Fact]
    public void FutureValue_WithoutGrowth_AddsContributions()
        => Assert.Equal(22_000m, FinancialForecasting.FutureValue(10_000m, 500m, 0m, 2));

    [Fact]
    public void IsaBridge_NeverReturnsNegativeRequirement()
        => Assert.Equal(0m, FinancialForecasting.IsaBridgeRequired(20_000m, 24_000m, 5));
}
