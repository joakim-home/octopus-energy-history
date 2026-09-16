using System.Text.Json;
using JoakimHomeDashboard.Application;
using JoakimHomeDashboard.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace JoakimHomeDashboard.Web.Pages;

public sealed class YearAnalysisModel(WebDashboardService dataService, IHomeEventStore events) : PageModel
{
    public WebDashboardData Data { get; private set; } = null!;
    public IReadOnlyList<YearEnergySummary> Summaries { get; private set; } = [];
    public IReadOnlyList<YearAnalysisTableRow> CostRows { get; private set; } = [];
    public IReadOnlyList<YearAnalysisTableRow> UsageRows { get; private set; } = [];
    public IReadOnlyList<YearAnalysisTableRow> GasRows { get; private set; } = [];
    public IReadOnlyList<YearAnalysisTableRow> CombinedRows { get; private set; } = [];
    public string CostChart { get; private set; } = "{}";
    public string UsageChart { get; private set; } = "{}";
    public string GasChart { get; private set; } = "{}";
    public string CombinedChart { get; private set; } = "{}";

    public async Task OnGetAsync()
    {
        Data = await dataService.LoadAsync(HttpContext.RequestAborted);
        var years = Data.Years;
        Summaries = YearAnalysisBuilder.BuildSummaries(Data.Monthly, years);
        CostRows = YearAnalysisBuilder.BuildTable(Data.Monthly, YearAnalysisMetric.NetElectricityCost, years);
        UsageRows = YearAnalysisBuilder.BuildTable(Data.Monthly, YearAnalysisMetric.NetGridUsage, years);
        GasRows = YearAnalysisBuilder.BuildTable(Data.Monthly, YearAnalysisMetric.GasCost, years);
        CombinedRows = YearAnalysisBuilder.BuildTable(Data.Monthly, YearAnalysisMetric.CombinedUtilityCost, years);
        CostChart = Chart(YearAnalysisBuilder.BuildSeries(Data.Monthly, YearAnalysisMetric.NetElectricityCost, years));
        UsageChart = Chart(YearAnalysisBuilder.BuildSeries(Data.Monthly, YearAnalysisMetric.NetGridUsage, years));
        GasChart = Chart(YearAnalysisBuilder.BuildSeries(Data.Monthly, YearAnalysisMetric.GasCost, years));
        CombinedChart = Chart(YearAnalysisBuilder.BuildSeries(Data.Monthly, YearAnalysisMetric.CombinedUtilityCost, years));
    }

    public async Task<IActionResult> OnPostAddEventAsync(DateTime eventDate, string eventLabel, string eventCategory)
    {
        if (!string.IsNullOrWhiteSpace(eventLabel))
            await events.SaveHomeEventAsync(new(0, DateOnly.FromDateTime(eventDate), eventLabel.Trim(), eventCategory), HttpContext.RequestAborted);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteEventAsync(long id)
    {
        await events.DeleteHomeEventAsync(id, HttpContext.RequestAborted);
        return RedirectToPage();
    }

    private static string Chart(IReadOnlyList<YearAnalysisSeries> series)
        => JsonSerializer.Serialize(new { labels = Enumerable.Range(1, 12).Select(x => new DateOnly(2000, x, 1).ToString("MMM")), series = series.Select((x, i) => new { name = x.Year.ToString(), values = x.Months, color = new[] { "#57b8ff", "#9d7bff", "#37d7a0", "#ffb454" }[i % 4] }) });
}
