using JoakimHomeDashboard.Application;
using JoakimHomeDashboard.Domain;

namespace JoakimHomeDashboard.Tests;

public sealed class HomeEventAnalysisTests
{
    [Fact]
    public void Marker_DefinesNonOverlappingBaselineAndAnalysisPeriods()
    {
        var marker = new HomeEvent(7, new(2025, 8, 1), "Solar installed", "Solar");
        var boundary = Assert.Single(HomeEventAnalysis.Build([marker], new(2024, 6, 22), new(2026, 6, 20)));

        Assert.Equal(new DateOnly(2024, 6, 22), boundary.Baseline.From);
        Assert.Equal(new DateOnly(2025, 7, 31), boundary.Baseline.To);
        Assert.Equal(new DateOnly(2025, 8, 1), boundary.Analysis.From);
        Assert.Equal(new DateOnly(2026, 6, 20), boundary.Analysis.To);
        Assert.Equal(marker, boundary.Marker);
    }

    [Fact]
    public void MarkersOutsideCoverage_ProduceOnlyTheAvailableAnalysisSide()
    {
        var before = Assert.Single(HomeEventAnalysis.Build([new(1, new(2020, 1, 1), "Old heating", "Heating")], new(2024, 1, 1), new(2026, 1, 1)));
        Assert.False(before.Baseline.HasData);
        Assert.True(before.Analysis.HasData);
        Assert.Equal(new DateOnly(2024, 1, 1), before.Analysis.From);

        var after = Assert.Single(HomeEventAnalysis.Build([new(2, new(2027, 1, 1), "Future battery", "Battery")], new(2024, 1, 1), new(2026, 1, 1)));
        Assert.True(after.Baseline.HasData);
        Assert.False(after.Analysis.HasData);
    }

    [Fact]
    public void SupportedCategories_MatchTheAnalysisWorkflow()
    {
        foreach (var category in new[] { "Solar", "Battery", "EV", "Heating", "Tariff", "Property", "Other" }) Assert.Contains(category, HomeEventAnalysis.SupportedCategories);
        Assert.Contains("Renovation", HomeEventAnalysis.SupportedCategories);
    }
}
