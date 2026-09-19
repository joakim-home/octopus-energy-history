using System.Globalization;
using JoakimHomeDashboard.Application;
using JoakimHomeDashboard.Infrastructure;
using JoakimHomeDashboard.Web;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var hostCulture = CultureInfo.GetCultureInfo("en-GB");
CultureInfo.DefaultThreadCurrentCulture = hostCulture;
CultureInfo.DefaultThreadCurrentUICulture = hostCulture;

var allocationBackfill = args.Contains("--backfill-allocations");
var resetAdmin = args.Contains("--reset-admin");
var builder = WebApplication.CreateBuilder(args.Where(arg => arg is not "--backfill-allocations" and not "--reset-admin").ToArray());
var dataPath = Environment.GetEnvironmentVariable("OCTOPUS_DATA_PATH")
    ?? Path.Combine(builder.Environment.ContentRootPath, "data", "dashboard.db");
var keyPath = Environment.GetEnvironmentVariable("OCTOPUS_SECRET_KEY_PATH")
    ?? Path.Combine(builder.Environment.ContentRootPath, "data", "secret.key");
var dataDirectory = Path.GetDirectoryName(Path.GetFullPath(dataPath))
    ?? throw new InvalidOperationException("Unable to determine the dashboard data directory.");
var protectionKeysPath = Path.Combine(dataDirectory, "data-protection-keys");
var authDisabled = string.Equals(
    Environment.GetEnvironmentVariable("OCTOPUS_AUTH_DISABLED"),
    "1",
    StringComparison.Ordinal);

builder.Services.AddSingleton(new LocalAuthOptions(authDisabled));
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(protectionKeysPath))
    .SetApplicationName("OctopusEnergyDashboard");
builder.Services.AddRazorPages(options =>
{
    if (!authDisabled)
    {
        options.Conventions.AuthorizeFolder("/");
        options.Conventions.AllowAnonymousToPage("/Account/Login");
        options.Conventions.AllowAnonymousToPage("/Account/Setup");
        options.Conventions.AllowAnonymousToPage("/Error");
    }
});
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/Login";
        options.Cookie.Name = "OctopusEnergyDashboard.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("login", limiter =>
    {
        limiter.PermitLimit = 10;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
});
builder.Services.AddSingleton<IPasswordHasher<LocalAdminUser>, PasswordHasher<LocalAdminUser>>();
builder.Services.AddSingleton<LocalAdminAuthService>();
builder.Services.AddSingleton<ISecretProtector>(_ => new FileKeySecretProtector(keyPath));
builder.Services.AddSingleton<IDashboardRepository>(sp => new SqliteDashboardRepository(dataPath, sp.GetRequiredService<ISecretProtector>()));
builder.Services.AddSingleton<IOctopusConfigurationStore>(sp => (SqliteDashboardRepository)sp.GetRequiredService<IDashboardRepository>());
builder.Services.AddSingleton<IOctopusReadingStore>(sp => (SqliteDashboardRepository)sp.GetRequiredService<IDashboardRepository>());
builder.Services.AddSingleton<IHomeEventStore>(sp => (SqliteDashboardRepository)sp.GetRequiredService<IDashboardRepository>());
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
if (!allocationBackfill && !resetAdmin && Environment.GetEnvironmentVariable("OCTOPUS_DISABLE_SYNC") != "1")
    builder.Services.AddHostedService<CredentialAwareSyncBackgroundService>();

var app = builder.Build();
await app.Services.GetRequiredService<IDashboardRepository>().InitializeAsync();
if (resetAdmin)
{
    await app.Services.GetRequiredService<LocalAdminAuthService>().ResetAsync();
    if (Directory.Exists(protectionKeysPath))
        Directory.Delete(protectionKeysPath, recursive: true);
    Console.WriteLine("Local administrator reset and existing login sessions revoked. Create a new administrator on the next web start.");
    return;
}
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
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "Octopus" }));
app.Run();

public partial class Program;
