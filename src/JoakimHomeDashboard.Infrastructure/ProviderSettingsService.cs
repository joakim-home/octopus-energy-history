using System.Globalization;
using JoakimHomeDashboard.Application;

namespace JoakimHomeDashboard.Infrastructure;

internal static class SettingKeys
{
    public const string OctopusApiKey = "octopus.apiKey"; public const string OctopusMpan = "octopus.mpan"; public const string OctopusSerial = "octopus.meterSerial";
    public const string OctopusExportMpan = "octopus.exportMpan"; public const string OctopusExportSerial = "octopus.exportMeterSerial"; public const string OctopusAccount = "octopus.accountCode";
    public const string OctopusExportProduct = "octopus.exportProductCode"; public const string OctopusExportTariff = "octopus.exportTariffCode";
    public const string OctopusProduct = "octopus.productCode"; public const string OctopusTariff = "octopus.tariffCode";
    public const string OctopusGasMprn = "octopus.gasMprn"; public const string OctopusGasSerial = "octopus.gasMeterSerial"; public const string OctopusGasProduct = "octopus.gasProductCode";
    public const string OctopusGasTariff = "octopus.gasTariffCode"; public const string OctopusGasCubicMetres = "octopus.gasReadingsInCubicMetres";
    public const string SolarCapacityKwp = "energy.solarCapacityKwp";
}

public sealed class ProviderSettingsService(IDashboardRepository repository) : IProviderSettingsService
{
    private async Task<string> Get(string key, CancellationToken ct) => await repository.GetSettingAsync(key, ct) ?? "";

    public async Task<ProviderSettingsSnapshot> LoadAsync(CancellationToken cancellationToken = default)
        => new(
            await Get(SettingKeys.OctopusMpan, cancellationToken), await Get(SettingKeys.OctopusSerial, cancellationToken),
            await Get(SettingKeys.OctopusExportMpan, cancellationToken), await Get(SettingKeys.OctopusExportSerial, cancellationToken), await Get(SettingKeys.OctopusExportProduct, cancellationToken), await Get(SettingKeys.OctopusExportTariff, cancellationToken),
            await Get(SettingKeys.OctopusAccount, cancellationToken), await Get(SettingKeys.OctopusProduct, cancellationToken), await Get(SettingKeys.OctopusTariff, cancellationToken),
            await Get(SettingKeys.OctopusGasMprn, cancellationToken), await Get(SettingKeys.OctopusGasSerial, cancellationToken), await Get(SettingKeys.OctopusGasProduct, cancellationToken), await Get(SettingKeys.OctopusGasTariff, cancellationToken),
            bool.TryParse(await Get(SettingKeys.OctopusGasCubicMetres, cancellationToken), out var gasCubic) && gasCubic,
            ParseSolarCapacity(await Get(SettingKeys.SolarCapacityKwp, cancellationToken)),
            !string.IsNullOrWhiteSpace(await Get(SettingKeys.OctopusApiKey, cancellationToken)));

    public async Task SaveOctopusAsync(OctopusSettingsInput settings, CancellationToken cancellationToken = default)
    {
        if (settings.ApiKey is { Length: > 0 }) await repository.SetSettingAsync(SettingKeys.OctopusApiKey, settings.ApiKey.Trim(), true, cancellationToken);
        await Save(SettingKeys.OctopusMpan, settings.Mpan); await Save(SettingKeys.OctopusSerial, settings.MeterSerial);
        await Save(SettingKeys.OctopusExportMpan, settings.ExportMpan); await Save(SettingKeys.OctopusExportSerial, settings.ExportMeterSerial);
        await Save(SettingKeys.OctopusExportProduct, settings.ExportProductCode); await Save(SettingKeys.OctopusExportTariff, settings.ExportTariffCode);
        await Save(SettingKeys.OctopusAccount, settings.AccountCode); await Save(SettingKeys.OctopusProduct, settings.ProductCode); await Save(SettingKeys.OctopusTariff, settings.TariffCode);
        await Save(SettingKeys.OctopusGasMprn, settings.GasMprn); await Save(SettingKeys.OctopusGasSerial, settings.GasMeterSerial);
        await Save(SettingKeys.OctopusGasProduct, settings.GasProductCode); await Save(SettingKeys.OctopusGasTariff, settings.GasTariffCode);
        await Save(SettingKeys.OctopusGasCubicMetres, settings.GasReadingsInCubicMetres.ToString());
        await Save(SettingKeys.SolarCapacityKwp, NormalizeSolarCapacity(settings.SolarCapacityKwp).ToString("0.###", CultureInfo.InvariantCulture));
        async Task Save(string key, string value) => await repository.SetSettingAsync(key, value.Trim(), false, cancellationToken);
    }


    private static decimal ParseSolarCapacity(string value)
        => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var capacity) ? NormalizeSolarCapacity(capacity) : 5.76m;

    private static decimal NormalizeSolarCapacity(decimal capacity) => capacity > 0 ? capacity : 5.76m;
}
