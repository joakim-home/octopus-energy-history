using JoakimHomeDashboard.Domain;

namespace JoakimHomeDashboard.Application;

public interface IDashboardRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task SeedSampleDataAsync(CancellationToken cancellationToken = default);
    Task<DashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken cancellationToken = default);
    Task AddAccountAsync(string name, AccountType type, string provider, decimal balance, CancellationToken cancellationToken = default);
    Task SetSettingAsync(string key, string value, bool isSecret = false, CancellationToken cancellationToken = default);
    Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default);
    Task UpsertDailyEnergyAsync(IReadOnlyCollection<DailyEnergyReading> readings, CancellationToken cancellationToken = default);
    Task RecordSyncResultAsync(SyncResult result, DateTimeOffset startedAt, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProviderSyncStatus>> GetSyncStatusesAsync(CancellationToken cancellationToken = default);
    Task<EnergyDashboardSnapshot> GetEnergyDashboardAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EnergyIntervalDetail>> GetOctopusIntervalsAsync(DateOnly date, CancellationToken cancellationToken = default);
    Task<SolarAnalysisConfiguration> GetSolarAnalysisConfigurationAsync(CancellationToken cancellationToken = default);
    Task SetSolarManualOverrideAsync(DateOnly? date, CancellationToken cancellationToken = default);
}

public interface IDataSourceSync
{
    string Name { get; }
    Task<SyncResult> SyncAsync(CancellationToken cancellationToken);
}

public sealed record SyncResult(string Source, bool Succeeded, int RecordsImported, string Message, DateTimeOffset? RangeFrom = null, DateTimeOffset? RangeTo = null);
public sealed record ConnectionTestResult(bool Succeeded, string Message);

public interface IProviderConnector : IDataSourceSync
{
    Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default);
}

public interface ISyncCoordinator
{
    Task<IReadOnlyList<SyncResult>> SyncAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProviderSyncStatus>> GetStatusesAsync(CancellationToken cancellationToken = default);
}

public sealed record OctopusSettingsInput(string? ApiKey, string Mpan, string MeterSerial, string ExportMpan, string ExportMeterSerial, string ExportProductCode, string ExportTariffCode, string AccountCode, string ProductCode, string TariffCode, string GasMprn, string GasMeterSerial, string GasProductCode, string GasTariffCode, bool GasReadingsInCubicMetres, decimal SolarCapacityKwp = 5.76m);
public sealed record EnphaseSettingsInput(string ClientId, string? ClientSecret, string? ApiKey, string RedirectUri);
public sealed record ProviderSettingsSnapshot(
    string OctopusMpan, string OctopusMeterSerial, string OctopusExportMpan, string OctopusExportMeterSerial, string OctopusExportProductCode, string OctopusExportTariffCode,
    string OctopusAccountCode, string OctopusProductCode, string OctopusTariffCode, string OctopusGasMprn, string OctopusGasMeterSerial,
    string OctopusGasProductCode, string OctopusGasTariffCode, bool OctopusGasReadingsInCubicMetres, decimal SolarCapacityKwp, bool HasOctopusApiKey,
    string EnphaseClientId, string EnphaseRedirectUri, string EnphaseSystemId, bool HasEnphaseClientSecret,
    bool HasEnphaseApiKey, bool IsEnphaseAuthorized);

public interface IProviderSettingsService
{
    Task<ProviderSettingsSnapshot> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveOctopusAsync(OctopusSettingsInput settings, CancellationToken cancellationToken = default);
    Task SaveEnphaseAsync(EnphaseSettingsInput settings, CancellationToken cancellationToken = default);
}

public interface IEnphaseAuthorizationService
{
    Task<ConnectionTestResult> AuthorizeAsync(CancellationToken cancellationToken = default);
}

public sealed record OctopusGasDiscovery(string Mprn, string MeterSerial, string ProductCode, string TariffCode);
public sealed record OctopusMeterPoint(string AccountNumber, long PropertyId, string FuelType, bool IsExport, string MeterPoint, string MeterSerial, string TariffCode, string ProductCode, DateTimeOffset? ValidFrom, DateTimeOffset? ValidTo);
public sealed record OctopusTariffPeriod(string AccountNumber, long PropertyId, string FuelType, bool IsExport, string MeterPoint, string TariffCode, string ProductCode, DateTimeOffset? ValidFrom, DateTimeOffset? ValidTo);
public sealed record OctopusDiscoveryResult(string AccountNumber, int PropertyCount, IReadOnlyList<OctopusMeterPoint> MeterPoints, IReadOnlyList<string> Warnings, IReadOnlyList<OctopusTariffPeriod>? TariffPeriods = null);
public interface IOctopusConfigurationStore
{
    Task RegisterFourRateTariffAsync(OctopusTariffPeriod period, string metadataJson, CancellationToken cancellationToken = default);
    Task ReplaceOctopusConfigurationAsync(OctopusDiscoveryResult discovery, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OctopusMeterPoint>> GetOctopusMeterPointsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OctopusTariffPeriod>> GetOctopusTariffPeriodsAsync(CancellationToken cancellationToken = default);
}
public interface IOctopusReadingStore
{
    Task<DateTimeOffset?> GetLatestOctopusReadingAsync(string meterPoint, string meterSerial, EnergyFlowType flowType, CancellationToken cancellationToken = default);
    Task<DateTimeOffset?> GetEarliestUncostedOctopusReadingAsync(string meterPoint, string meterSerial, EnergyFlowType flowType, CancellationToken cancellationToken = default);
    Task<int> DeleteReadingsOutsideConfigurationAsync(IReadOnlyCollection<OctopusMeterPoint> meters, CancellationToken cancellationToken = default);
    Task UpsertOctopusRawReadingsAsync(IReadOnlyCollection<OctopusRawReading> readings, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OctopusRawReading>> InsertMissingOctopusRawReadingsAsync(IReadOnlyCollection<OctopusRawReading> readings, CancellationToken cancellationToken = default);
    Task UpsertOctopusStandingChargesAsync(IReadOnlyCollection<OctopusStandingCharge> charges, CancellationToken cancellationToken = default);
    Task InsertMissingOctopusStandingChargesAsync(IReadOnlyCollection<OctopusStandingCharge> charges, CancellationToken cancellationToken = default);
    Task RebuildOctopusRollupsAsync(CancellationToken cancellationToken = default);
}
public interface IHomeEventStore
{
    Task<IReadOnlyList<HomeEvent>> GetHomeEventsAsync(CancellationToken cancellationToken = default);
    Task SaveHomeEventAsync(HomeEvent homeEvent, CancellationToken cancellationToken = default);
    Task DeleteHomeEventAsync(long id, CancellationToken cancellationToken = default);
}
public interface IUserPreferences
{
    Task<EnergyViewMode> GetEnergyViewModeAsync(CancellationToken cancellationToken = default);
    Task SetEnergyViewModeAsync(EnergyViewMode mode, CancellationToken cancellationToken = default);
    Task<AnalysisChartType> GetYearAnalysisChartTypeAsync(CancellationToken cancellationToken = default);
    Task SetYearAnalysisChartTypeAsync(AnalysisChartType type, CancellationToken cancellationToken = default);
    Task<AnalysisChartType> GetMonthlyCostChartTypeAsync(CancellationToken cancellationToken = default);
    Task SetMonthlyCostChartTypeAsync(AnalysisChartType type, CancellationToken cancellationToken = default);
    Task<CostColumnMode> GetCostColumnModeAsync(CancellationToken cancellationToken = default);
    Task SetCostColumnModeAsync(CostColumnMode mode, CancellationToken cancellationToken = default);
}
public interface IOctopusDiscoveryService
{
    Task<OctopusDiscoveryResult> DiscoverAccountAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OctopusMeterPoint>> GetDiscoveredConfigurationAsync(CancellationToken cancellationToken = default);
}

public sealed class DashboardService(IDashboardRepository repository)
{
    public Task<DashboardSnapshot> LoadAsync(CancellationToken cancellationToken = default) => repository.GetDashboardAsync(cancellationToken);
    public Task<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken cancellationToken = default) => repository.GetAccountsAsync(cancellationToken);
    public Task<EnergyDashboardSnapshot> LoadEnergyAsync(CancellationToken cancellationToken = default) => repository.GetEnergyDashboardAsync(cancellationToken);
    public Task<IReadOnlyList<EnergyIntervalDetail>> LoadIntervalsAsync(DateOnly date, CancellationToken cancellationToken = default) => repository.GetOctopusIntervalsAsync(date, cancellationToken);
    public Task<SolarAnalysisConfiguration> LoadSolarConfigurationAsync(CancellationToken cancellationToken = default) => repository.GetSolarAnalysisConfigurationAsync(cancellationToken);
    public Task SetSolarManualOverrideAsync(DateOnly? date, CancellationToken cancellationToken = default) => repository.SetSolarManualOverrideAsync(date, cancellationToken);

    public Task AddAccountAsync(string name, AccountType type, string provider, decimal balance, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Account name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(provider)) provider = "Manual";
        return repository.AddAccountAsync(name.Trim(), type, provider.Trim(), balance, cancellationToken);
    }
}
