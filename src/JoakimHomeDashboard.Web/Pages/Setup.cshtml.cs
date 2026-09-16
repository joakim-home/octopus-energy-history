using JoakimHomeDashboard.Application;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace JoakimHomeDashboard.Web.Pages;

public sealed class SetupModel(
    IProviderSettingsService settings,
    IOctopusDiscoveryService discovery,
    IEnumerable<IProviderConnector> connectors,
    ISyncCoordinator sync) : PageModel
{
    [BindProperty] public SetupInput Input { get; set; } = new();
    public ProviderSettingsSnapshot Snapshot { get; private set; } = null!;
    public IReadOnlyList<OctopusMeterPoint> Meters { get; private set; } = [];

    public async Task OnGetAsync() => await Load();

    public async Task<IActionResult> OnPostSaveAsync() { await SaveConnection(); TempData["Message"] = "Octopus settings saved securely."; return RedirectToPage(); }
    public async Task<IActionResult> OnPostSaveOverridesAsync() { await SaveOverrides(); TempData["Message"] = "Advanced overrides saved."; return RedirectToPage(); }
    public async Task<IActionResult> OnPostDiscoverAsync() { await SaveConnection(); var result = await discovery.DiscoverAccountAsync(HttpContext.RequestAborted); TempData["Message"] = $"Discovered {result.MeterPoints.Count} meter configuration(s) for {result.AccountNumber}."; return RedirectToPage(); }
    public async Task<IActionResult> OnPostTestAsync() { await SaveConnection(); var result = await connectors.First().TestConnectionAsync(HttpContext.RequestAborted); TempData["Message"] = result.Message; return RedirectToPage(); }
    public async Task<IActionResult> OnPostSyncAsync() { var result = await sync.SyncAllAsync(HttpContext.RequestAborted); TempData["Message"] = string.Join(" · ", result.Select(x => $"{x.Source}: {(x.Succeeded ? "OK" : x.Message)}")); return RedirectToPage(); }

    private async Task SaveConnection()
    {
        var current = await settings.LoadAsync(HttpContext.RequestAborted);
        await settings.SaveOctopusAsync(new(Input.ApiKey, current.OctopusMpan, current.OctopusMeterSerial, current.OctopusExportMpan, current.OctopusExportMeterSerial, current.OctopusExportProductCode, current.OctopusExportTariffCode, Input.AccountCode ?? current.OctopusAccountCode, current.OctopusProductCode, current.OctopusTariffCode, current.OctopusGasMprn, current.OctopusGasMeterSerial, current.OctopusGasProductCode, current.OctopusGasTariffCode, current.OctopusGasReadingsInCubicMetres, current.SolarCapacityKwp), HttpContext.RequestAborted);
    }

    private async Task SaveOverrides()
    {
        var current = await settings.LoadAsync(HttpContext.RequestAborted);
        await settings.SaveOctopusAsync(new(null, Input.Mpan ?? current.OctopusMpan, Input.MeterSerial ?? current.OctopusMeterSerial, Input.ExportMpan ?? current.OctopusExportMpan, Input.ExportMeterSerial ?? current.OctopusExportMeterSerial, Input.ExportProductCode ?? current.OctopusExportProductCode, Input.ExportTariffCode ?? current.OctopusExportTariffCode, current.OctopusAccountCode, Input.ProductCode ?? current.OctopusProductCode, Input.TariffCode ?? current.OctopusTariffCode, Input.GasMprn ?? current.OctopusGasMprn, Input.GasMeterSerial ?? current.OctopusGasMeterSerial, Input.GasProductCode ?? current.OctopusGasProductCode, Input.GasTariffCode ?? current.OctopusGasTariffCode, Input.GasReadingsInCubicMetres, Input.SolarCapacityKwp), HttpContext.RequestAborted);
    }

    private async Task Load()
    {
        Snapshot = await settings.LoadAsync(HttpContext.RequestAborted);
        Meters = await discovery.GetDiscoveredConfigurationAsync(HttpContext.RequestAborted);
        Input = new() { AccountCode = Snapshot.OctopusAccountCode, Mpan = Snapshot.OctopusMpan, MeterSerial = Snapshot.OctopusMeterSerial, ExportMpan = Snapshot.OctopusExportMpan, ExportMeterSerial = Snapshot.OctopusExportMeterSerial, ExportProductCode = Snapshot.OctopusExportProductCode, ExportTariffCode = Snapshot.OctopusExportTariffCode, ProductCode = Snapshot.OctopusProductCode, TariffCode = Snapshot.OctopusTariffCode, GasMprn = Snapshot.OctopusGasMprn, GasMeterSerial = Snapshot.OctopusGasMeterSerial, GasProductCode = Snapshot.OctopusGasProductCode, GasTariffCode = Snapshot.OctopusGasTariffCode, GasReadingsInCubicMetres = Snapshot.OctopusGasReadingsInCubicMetres, SolarCapacityKwp = Snapshot.SolarCapacityKwp };
    }

    public sealed class SetupInput
    {
        public string? ApiKey { get; set; } public string? AccountCode { get; set; } public string? Mpan { get; set; } public string? MeterSerial { get; set; }
        public string? ExportMpan { get; set; } public string? ExportMeterSerial { get; set; } public string? ExportProductCode { get; set; } public string? ExportTariffCode { get; set; }
        public string? ProductCode { get; set; } public string? TariffCode { get; set; } public string? GasMprn { get; set; } public string? GasMeterSerial { get; set; }
        public string? GasProductCode { get; set; } public string? GasTariffCode { get; set; } public bool GasReadingsInCubicMetres { get; set; } public decimal SolarCapacityKwp { get; set; } = 5.76m;
    }
}
