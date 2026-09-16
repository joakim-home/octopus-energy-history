using JoakimHomeDashboard.Application;
using JoakimHomeDashboard.Domain;

namespace JoakimHomeDashboard.Infrastructure;

public sealed class UserPreferences(IDashboardRepository repository) : IUserPreferences
{
    private const string ViewModeKey = "ui.energyViewMode";
    private const string YearChartTypeKey = "ui.yearAnalysisChartType";
    private const string MonthlyCostChartTypeKey = "ui.monthlyCostChartType";
    private const string CostColumnModeKey = "ui.costColumnMode";
    public async Task<EnergyViewMode> GetEnergyViewModeAsync(CancellationToken cancellationToken = default)
        => Enum.TryParse<EnergyViewMode>(await repository.GetSettingAsync(ViewModeKey, cancellationToken), true, out var mode) ? mode : EnergyViewMode.Energy;
    public Task SetEnergyViewModeAsync(EnergyViewMode mode, CancellationToken cancellationToken = default)
        => repository.SetSettingAsync(ViewModeKey, mode.ToString(), false, cancellationToken);
    public async Task<AnalysisChartType> GetYearAnalysisChartTypeAsync(CancellationToken cancellationToken = default)
        => Enum.TryParse<AnalysisChartType>(await repository.GetSettingAsync(YearChartTypeKey, cancellationToken), true, out var type) ? type : AnalysisChartType.Columns;
    public Task SetYearAnalysisChartTypeAsync(AnalysisChartType type, CancellationToken cancellationToken = default)
        => repository.SetSettingAsync(YearChartTypeKey, type.ToString(), false, cancellationToken);
    public async Task<AnalysisChartType> GetMonthlyCostChartTypeAsync(CancellationToken cancellationToken = default)
        => Enum.TryParse<AnalysisChartType>(await repository.GetSettingAsync(MonthlyCostChartTypeKey, cancellationToken), true, out var type) ? type : AnalysisChartType.Columns;
    public Task SetMonthlyCostChartTypeAsync(AnalysisChartType type, CancellationToken cancellationToken = default)
        => repository.SetSettingAsync(MonthlyCostChartTypeKey, type.ToString(), false, cancellationToken);
    public async Task<CostColumnMode> GetCostColumnModeAsync(CancellationToken cancellationToken = default)
        => Enum.TryParse<CostColumnMode>(await repository.GetSettingAsync(CostColumnModeKey, cancellationToken), true, out var mode) ? mode : CostColumnMode.StackedBreakdown;
    public Task SetCostColumnModeAsync(CostColumnMode mode, CancellationToken cancellationToken = default)
        => repository.SetSettingAsync(CostColumnModeKey, mode.ToString(), false, cancellationToken);
}
