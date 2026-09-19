namespace JoakimHomeDashboard.Application;

public static class HomeEventAnalysis
{
    public static IReadOnlyList<string> SupportedCategories { get; } = ["Solar", "Battery", "EV", "Heating", "Tariff", "Property", "Holiday", "Away", "Travel", "Renovation", "Appliance", "Cooling", "Other"];
}
