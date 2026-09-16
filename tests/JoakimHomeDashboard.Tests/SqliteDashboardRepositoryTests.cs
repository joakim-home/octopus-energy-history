using JoakimHomeDashboard.Domain;
using JoakimHomeDashboard.Infrastructure;

namespace JoakimHomeDashboard.Tests;

public sealed class SqliteDashboardRepositoryTests : IAsyncLifetime
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"joakim-dashboard-{Guid.NewGuid():N}.db");
    private SqliteDashboardRepository Repository => new(databasePath);

    public async Task InitializeAsync()
    {
        await Repository.InitializeAsync();
        await Repository.SeedSampleDataAsync();
    }

    [Fact]
    public async Task SeededDashboard_HasOnlyEnergyPlaceholderData()
    {
        var dashboard = await Repository.GetDashboardAsync();
        Assert.Equal(0, dashboard.TotalCash);
        Assert.Equal(0, dashboard.Investments);
        Assert.Equal(0, dashboard.Pensions);
        Assert.Equal(0, dashboard.PropertyEquity);
        Assert.True(dashboard.EnergyHistory.Count > 0);
        Assert.Empty(dashboard.UpcomingVesting);
    }

    [Fact]
    public async Task AddAccount_PersistsManualAccount()
    {
        await Repository.AddAccountAsync("Premium Bonds", AccountType.Savings, "NS&I", 5_000m);
        var accounts = await Repository.GetAccountsAsync();
        Assert.Contains(accounts, x => x.Name == "Premium Bonds" && x.Balance == 5_000m);
    }

    public Task DisposeAsync()
    {
        if (File.Exists(databasePath)) File.Delete(databasePath);
        return Task.CompletedTask;
    }
}
