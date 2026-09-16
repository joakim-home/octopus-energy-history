using JoakimHomeDashboard.Domain;

namespace JoakimHomeDashboard.Application;

public static class HomeEventAnalysis
{
    public static IReadOnlyList<string> SupportedCategories { get; } = ["Solar", "Battery", "EV", "Heating", "Tariff", "Property", "Holiday", "Away", "Travel", "Renovation", "Appliance", "Cooling", "Other"];

    public static IReadOnlyList<HomeEventAnalysisBoundary> Build(IEnumerable<HomeEvent> markers, DateOnly? dataFrom, DateOnly? dataTo)
        => markers.OrderBy(marker => marker.Date).ThenBy(marker => marker.Id)
            .Select(marker => BuildBoundary(marker, dataFrom, dataTo)).ToArray();

    private static HomeEventAnalysisBoundary BuildBoundary(HomeEvent marker, DateOnly? dataFrom, DateOnly? dataTo)
    {
        var beforeMarker = marker.Date == DateOnly.MinValue ? DateOnly.MinValue : marker.Date.AddDays(-1);
        var baselineTo = dataTo is null || dataTo > beforeMarker ? beforeMarker : dataTo;
        var baseline = dataFrom is not null && baselineTo >= dataFrom ? new EnergyAnalysisPeriod(dataFrom, baselineTo) : new(null, null);
        var analysisFrom = dataFrom is null || dataFrom < marker.Date ? marker.Date : dataFrom;
        var analysis = dataTo is not null && analysisFrom <= dataTo ? new EnergyAnalysisPeriod(analysisFrom, dataTo) : new(null, null);
        return new(marker, baseline, analysis);
    }
}
