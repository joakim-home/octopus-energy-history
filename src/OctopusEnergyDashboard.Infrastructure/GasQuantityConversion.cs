namespace OctopusEnergyDashboard.Infrastructure;

public static class GasQuantityConversion
{
    // Ofgem billing convention: m³ × 1.02264 × calorific value ÷ 3.6 = kWh.
    // Octopus household statements for this data set reconcile at the standard 39.5 MJ/m³ value.
    public const decimal VolumeCorrection = 1.02264m;
    public const decimal CalorificValueMjPerCubicMetre = 39.5m;
    public const decimal LegacyCubicMetresToKwhFactor = 11.1868m;
    public const decimal CubicMetresToKwhFactor = VolumeCorrection * CalorificValueMjPerCubicMetre / 3.6m;
    public const decimal LegacyNormalizedCorrectionFactor = CubicMetresToKwhFactor / LegacyCubicMetresToKwhFactor;

    public static decimal CubicMetresToKwh(decimal cubicMetres)
        => decimal.Round(cubicMetres * CubicMetresToKwhFactor, 6);
}
