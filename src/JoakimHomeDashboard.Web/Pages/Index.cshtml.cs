using System.Text.Json;
using JoakimHomeDashboard.Application;
using JoakimHomeDashboard.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace JoakimHomeDashboard.Web.Pages;

public sealed class IndexModel(WebDashboardService dataService, IDashboardRepository repository, ISyncCoordinator sync) : PageModel
{
    public WebDashboardData Data { get; private set; } = null!;
    public string OverviewExplorerJson => JsonSerializer.Serialize(new
    {
        periods = new
        {
            day = PeriodSummary("Today", Data.Snapshot.TodayImportKwh, Data.Snapshot.TodayExportKwh, Data.Snapshot.TodayGasKwh, Data.Snapshot.TodayCost),
            month = PeriodSummary(DateTime.Today.ToString("MMMM yyyy"), Data.Snapshot.MonthImportKwh, Data.Snapshot.MonthExportKwh, Data.Snapshot.MonthGasKwh, Data.Snapshot.MonthCost),
            year = PeriodSummary(DateTime.Today.Year.ToString(), Data.Snapshot.YearImportKwh, Data.Snapshot.YearExportKwh, Data.Snapshot.YearGasKwh, Data.Snapshot.YearCost)
        },
        daily = Data.Snapshot.Daily.OrderBy(x => x.Period).Select(x => new
        {
            date = x.Period.ToString("yyyy-MM-dd"),
            importKwh = x.ImportKwh, exportKwh = x.ExportKwh, gasKwh = x.GasKwh,
            importCost = x.ImportCost, exportIncome = x.ExportIncome, gasCost = x.GasCost, standingCharge = x.StandingChargeGbp, standingExact = x.StandingChargeExact,
            importExact = x.ImportCostExact, exportExact = x.ExportIncomeExact, gasExact = x.GasCostExact,
            peakKwh = x.PeakImportKwh, offPeakKwh = x.OffPeakImportKwh, unknownKwh = x.UnknownImportKwh,
            peakCost = x.PeakImportCost, offPeakCost = x.OffPeakImportCost,
            peakExact = x.PeakCostExact, offPeakExact = x.OffPeakCostExact
        }),
        monthly = Data.Monthly.OrderBy(x => x.Month).Select(x => new
        {
            date = x.Month.ToString("yyyy-MM-dd"),
            importKwh = x.ImportKwh, exportKwh = x.ExportKwh, gasKwh = x.GasKwh,
            importCost = x.ImportCostGbp, exportIncome = x.ExportIncomeGbp, gasCost = x.GasCostGbp, standingCharge = x.StandingChargeGbp, standingExact = x.StandingChargeExact,
            importExact = x.ImportCostExact, exportExact = x.ExportIncomeExact, gasExact = x.GasCostExact,
            peakKwh = x.PeakImportKwh, offPeakKwh = x.OffPeakImportKwh, unknownKwh = Math.Max(0, x.ImportKwh - x.PeakImportKwh - x.OffPeakImportKwh),
            peakCost = x.PeakImportCostGbp, offPeakCost = x.OffPeakImportCostGbp,
            peakExact = x.PeakCostExact, offPeakExact = x.OffPeakCostExact
        }),
        supplierMonths = Data.Snapshot.SupplierAllocationMonths.OrderBy(x => x.Month).Select(x => new
        {
            month = x.Month,
            complete = x.Complete,
            meterKwh = x.MeterKwh,
            grossGbp = x.GrossGbp,
            bands = x.Bands.Select(b => new { band = b.Band, kwh = b.Kwh, grossGbp = b.GrossGbp })
        }),
        events = Data.HomeEvents.OrderBy(x => x.Date).Select(x => new
        {
            date = x.Date.ToString("yyyy-MM-dd"),
            label = x.Label,
            category = x.Category
        })
    });

    public async Task OnGetAsync() => Data = await dataService.LoadAsync(HttpContext.RequestAborted);

    public async Task<IActionResult> OnPostSyncAsync()
    {
        var results = await sync.SyncAllAsync(HttpContext.RequestAborted);
        TempData["Message"] = string.Join(" · ", results.Select(x => $"{x.Source}: {(x.Succeeded ? "OK" : "Failed")}"));
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSolarOverrideAsync(DateTime? solarDate)
    {
        await repository.SetSolarManualOverrideAsync(solarDate is null ? null : DateOnly.FromDateTime(solarDate.Value), HttpContext.RequestAborted);
        TempData["Message"] = solarDate is null ? "Automatic solar detection restored." : $"Solar start override saved: {solarDate:dd MMM yyyy}.";
        return RedirectToPage();
    }

    private static object PeriodSummary(string label, decimal importKwh, decimal exportKwh, decimal gasKwh, EnergyCostPeriod cost)
    {
        var electricityExact = cost.ImportExact && (exportKwh == 0 || cost.ExportExact);
        var gasExact = gasKwh == 0 || cost.GasExact;
        return new
        {
            label,
            importKwh,
            exportKwh,
            gasKwh,
            importCost = cost.ImportCost,
            exportIncome = cost.ExportIncome,
            gasCost = cost.GasCost,
            gasUsageCost = cost.GasCost,
            standingCharge = cost.StandingCharge,
            standingExact = cost.StandingExact,
            electricityNetCost = cost.ImportCost - cost.ExportIncome,
            combinedCost = cost.ImportCost - cost.ExportIncome + cost.GasCost + cost.StandingCharge,
            combinedCostWithoutStanding = cost.ImportCost - cost.ExportIncome + cost.GasCost,
            importExact = cost.ImportExact,
            exportExact = exportKwh == 0 || cost.ExportExact,
            gasExact,
            electricityExact,
            combinedExact = electricityExact && gasExact && cost.StandingExact
        };
    }
}
