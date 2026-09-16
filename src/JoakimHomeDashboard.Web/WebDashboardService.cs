using System.Globalization;
using JoakimHomeDashboard.Application;
using JoakimHomeDashboard.Domain;

namespace JoakimHomeDashboard.Web;

public sealed class WebDashboardService(DashboardService dashboard, IHomeEventStore events)
{
    public async Task<WebDashboardData> LoadAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await dashboard.LoadEnergyAsync(cancellationToken);
        var monthly = EnergyInsightBuilder.BuildMonthly(snapshot.Monthly);
        var solar = await dashboard.LoadSolarConfigurationAsync(cancellationToken);
        var homeEvents = await events.GetHomeEventsAsync(cancellationToken);
        var solarAnalysis = EnergyComparisonEngine.AnalyzeSolar(monthly, solar, homeEvents, false);
        var years = YearAnalysisBuilder.AvailableYears(monthly);
        return new(snapshot, monthly, solar, solarAnalysis, homeEvents, years);
    }

    public static string Money(decimal value, bool exact = true) => exact
        ? value.ToString("C2", CultureInfo.GetCultureInfo("en-GB"))
        : value == 0 ? "Unavailable" : $"≈ {value.ToString("C2", CultureInfo.GetCultureInfo("en-GB"))}";

    public static string Signed(decimal? value, string unit)
    {
        if (value is null) return "—";
        return unit == "£"
            ? value.Value.ToString("+£0.00;-£0.00;£0.00", CultureInfo.GetCultureInfo("en-GB"))
            : $"{value.Value:+0.00;-0.00;0.00} {unit}";
    }
}

public sealed record WebDashboardData(
    EnergyDashboardSnapshot Snapshot,
    IReadOnlyList<MonthlyEnergyInsight> Monthly,
    SolarAnalysisConfiguration Solar,
    SolarBaselineAnalysis SolarAnalysis,
    IReadOnlyList<HomeEvent> HomeEvents,
    IReadOnlyList<int> Years)
{
    public int CoverageDays => Snapshot.DataFrom is null || Snapshot.DataTo is null ? 0 : (Snapshot.DataTo.Value.Date - Snapshot.DataFrom.Value.Date).Days + 1;
    public IReadOnlyList<DataQualityGroup> QualityGroups => DataQualityGrouping.Group(Snapshot.Warnings);
}
