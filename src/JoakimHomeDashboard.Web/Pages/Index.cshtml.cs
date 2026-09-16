using System.Text.Json;
using JoakimHomeDashboard.Application;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace JoakimHomeDashboard.Web.Pages;

public sealed class IndexModel(WebDashboardService dataService, DashboardService dashboard, ISyncCoordinator sync) : PageModel
{
    public WebDashboardData Data { get; private set; } = null!;
    public string MonthlyElectricityChart => ChartJson(Data.Monthly.Select(x => x.Month),
        ("Import", Data.Monthly.Select(x => x.ImportKwh), "#57b8ff"),
        ("Export", Data.Monthly.Select(x => x.ExportKwh), "#37d7a0"));
    public string MonthlyCostChart => NullableChartJson(Data.Monthly.Select(x => x.Month),
        ("Import cost", Data.Monthly.Select(x => AllocationCost(x.Month, x.ImportCostGbp)), "#ff6b8a"),
        ("Export income", Data.Monthly.Select(x => (decimal?)-x.ExportIncomeGbp), "#37d7a0"),
        ("Net electricity", Data.Monthly.Select(x => AllocationCost(x.Month, x.NetElectricityCostGbp)), "#9d7bff"));

    private decimal? AllocationCost(DateOnly month, decimal value) => Data.Snapshot.SupplierAllocationMonths.Any(x => x.Month == month.ToString("yyyy-MM") && !x.Complete) ? null : value;
    public string MonthlyGasChart => ChartJson(Data.Monthly.Select(x => x.Month),
        ("Gas", Data.Monthly.Select(x => x.GasKwh), "#ffb454"));
    public string DailyElectricityChart => ChartJson(Data.Snapshot.Daily.TakeLast(365).Select(x => x.Period),
        ("Import", Data.Snapshot.Daily.TakeLast(365).Select(x => x.ImportKwh), "#57b8ff"),
        ("Export", Data.Snapshot.Daily.TakeLast(365).Select(x => x.ExportKwh), "#37d7a0"));

    public async Task OnGetAsync() => Data = await dataService.LoadAsync(HttpContext.RequestAborted);

    public async Task<IActionResult> OnPostSyncAsync()
    {
        var results = await sync.SyncAllAsync(HttpContext.RequestAborted);
        TempData["Message"] = string.Join(" · ", results.Select(x => $"{x.Source}: {(x.Succeeded ? "OK" : "Failed")}"));
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSolarOverrideAsync(DateTime? solarDate)
    {
        await dashboard.SetSolarManualOverrideAsync(solarDate is null ? null : DateOnly.FromDateTime(solarDate.Value), HttpContext.RequestAborted);
        TempData["Message"] = solarDate is null ? "Automatic solar detection restored." : $"Solar start override saved: {solarDate:dd MMM yyyy}.";
        return RedirectToPage();
    }

    private static string ChartJson(IEnumerable<DateOnly> labels, params (string Name, IEnumerable<decimal> Values, string Color)[] series)
        => JsonSerializer.Serialize(new { labels = labels.Select(x => x.ToString("yyyy-MM-dd")), series = series.Select(x => new { name = x.Name, values = x.Values, color = x.Color }) });

    private static string NullableChartJson(IEnumerable<DateOnly> labels, params (string Name, IEnumerable<decimal?> Values, string Color)[] series)
        => JsonSerializer.Serialize(new { labels = labels.Select(x => x.ToString("yyyy-MM-dd")), series = series.Select(x => new { name = x.Name, values = x.Values, color = x.Color }) });
}
