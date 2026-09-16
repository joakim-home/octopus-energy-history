using System.Globalization;
using JoakimHomeDashboard.Application;
using JoakimHomeDashboard.Infrastructure;
using JoakimHomeDashboard.Web;

var hostCulture = CultureInfo.GetCultureInfo("en-GB");
CultureInfo.DefaultThreadCurrentCulture = hostCulture;
CultureInfo.DefaultThreadCurrentUICulture = hostCulture;

var builder = WebApplication.CreateBuilder(args.Where(arg => arg != "--backfill-allocations").ToArray());
var dataPath = Environment.GetEnvironmentVariable("OCTOPUS_DATA_PATH")
    ?? Path.Combine(builder.Environment.ContentRootPath, "data", "dashboard.db");
var keyPath = Environment.GetEnvironmentVariable("OCTOPUS_SECRET_KEY_PATH")
    ?? Path.Combine(builder.Environment.ContentRootPath, "data", "secret.key");

builder.Services.AddRazorPages();
builder.Services.AddSingleton<ISecretProtector>(_ => new FileKeySecretProtector(keyPath));
builder.Services.AddSingleton<IDashboardRepository>(sp => new SqliteDashboardRepository(dataPath, sp.GetRequiredService<ISecretProtector>()));
builder.Services.AddSingleton<IOctopusConfigurationStore>(sp => (SqliteDashboardRepository)sp.GetRequiredService<IDashboardRepository>());
builder.Services.AddSingleton<IOctopusReadingStore>(sp => (SqliteDashboardRepository)sp.GetRequiredService<IDashboardRepository>());
builder.Services.AddSingleton<IHomeEventStore>(sp => (SqliteDashboardRepository)sp.GetRequiredService<IDashboardRepository>());
builder.Services.AddSingleton<IUserPreferences, UserPreferences>();
builder.Services.AddSingleton<DashboardService>();
builder.Services.AddSingleton<IProviderSettingsService, ProviderSettingsService>();
builder.Services.AddHttpClient<OctopusEnergyDataSource>();
builder.Services.AddSingleton<IProviderConnector>(sp => sp.GetRequiredService<OctopusEnergyDataSource>());
builder.Services.AddSingleton<IOctopusDiscoveryService>(sp => sp.GetRequiredService<OctopusEnergyDataSource>());
builder.Services.AddSingleton<ISyncCoordinator, SyncCoordinator>();
builder.Services.AddSingleton<WebDashboardService>();
builder.Services.AddSingleton(_ => new SupplierAllocationStore(dataPath));
builder.Services.AddHttpClient<SupplierAllocationImporter>();
builder.Services.AddSingleton<IProviderConnector, SupplierAllocationSyncConnector>();
builder.Services.AddHttpClient<GasPricingImporter>();
builder.Services.AddSingleton<IProviderConnector>(sp => sp.GetRequiredService<GasPricingImporter>());
var allocationBackfill = args.Contains("--backfill-allocations");
if (!allocationBackfill && Environment.GetEnvironmentVariable("OCTOPUS_DISABLE_SYNC") != "1")
    builder.Services.AddHostedService<CredentialAwareSyncBackgroundService>();

var app = builder.Build();
await app.Services.GetRequiredService<IDashboardRepository>().InitializeAsync();
if (allocationBackfill)
{
    try
    {
        var result = await app.Services.GetRequiredService<SupplierAllocationImporter>().BackfillAsync();
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
        var allocationStore = app.Services.GetRequiredService<SupplierAllocationStore>();
        var daily = await allocationStore.AuditAsync(false);
        var monthly = await allocationStore.SummaryAsync();
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(daily));
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(monthly));
        if (result.Intervals != result.Reconciled || daily.Any(d => !d.Complete) || monthly.Any(m => !m.Complete)) Environment.ExitCode = 2;
    }
    finally { await app.Services.GetRequiredService<IOctopusReadingStore>().RebuildOctopusRollupsAsync(); }
    return;
}
await app.Services.GetRequiredService<IOctopusReadingStore>().RebuildOctopusRollupsAsync();
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error");
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();
app.MapRazorPages();
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "Octopus" }));
app.Run();

public partial class Program;
