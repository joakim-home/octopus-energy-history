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
    public string YearExplorerJson { get; private set; } = "{}";

    public async Task OnGetAsync()
    {
        Data = await dataService.LoadAsync(HttpContext.RequestAborted);
        var years = Data.Years;
        Summaries = YearAnalysisBuilder.BuildSummaries(Data.Monthly, years);
        CostRows = YearAnalysisBuilder.BuildTable(Data.Monthly, YearAnalysisMetric.NetElectricityCost, years);
        UsageRows = YearAnalysisBuilder.BuildTable(Data.Monthly, YearAnalysisMetric.NetGridUsage, years);
        GasRows = YearAnalysisBuilder.BuildTable(Data.Monthly, YearAnalysisMetric.GasCost, years);
        CombinedRows = YearAnalysisBuilder.BuildTable(Data.Monthly, YearAnalysisMetric.CombinedUtilityCost, years);
        YearExplorerJson = JsonSerializer.Serialize(new
        {
            years,
            metrics = new
            {
                cost = MetricPayload("Net electricity cost", "£", YearAnalysisBuilder.BuildSeries(Data.Monthly, YearAnalysisMetric.NetElectricityCost, years), CostRows),
                usage = MetricPayload("Net grid usage", "kWh", YearAnalysisBuilder.BuildSeries(Data.Monthly, YearAnalysisMetric.NetGridUsage, years), UsageRows),
                gas = MetricPayload("Gas cost", "£", YearAnalysisBuilder.BuildSeries(Data.Monthly, YearAnalysisMetric.GasCost, years), GasRows),
                combined = MetricPayload("Combined utility cost", "£", YearAnalysisBuilder.BuildSeries(Data.Monthly, YearAnalysisMetric.CombinedUtilityCost, years), CombinedRows)
            }
        });
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

    private static object MetricPayload(string title, string unit, IReadOnlyList<YearAnalysisSeries> series, IReadOnlyList<YearAnalysisTableRow> rows)
        => new
        {
            title,
            unit,
            series = series.Select(x => new { name = x.Year.ToString(), values = x.Months }),
            rows = rows.Select(row => new
            {
                month = new DateOnly(2000, row.Month, 1).ToString("MMM"),
                values = row.Values.Select(x => new { year = x.Year, value = x.Value }),
                difference = row.Difference,
                percentageDifference = row.PercentageDifference
            })
        };

}
