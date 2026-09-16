namespace JoakimHomeDashboard.Application;

public static class FinancialForecasting
{
    public static decimal SolarPaybackPercent(decimal cumulativeSavings, decimal installCost = 11_000m)
        => installCost <= 0 ? 0 : Math.Clamp(cumulativeSavings / installCost * 100m, 0m, 100m);

    public static decimal FutureValue(decimal currentPot, decimal monthlyContribution, decimal annualGrowthRate, int years)
    {
        if (years < 0) throw new ArgumentOutOfRangeException(nameof(years));
        var monthlyRate = (double)annualGrowthRate / 12d;
        var months = years * 12;
        if (monthlyRate == 0) return currentPot + monthlyContribution * months;
        var factor = Math.Pow(1d + monthlyRate, months);
        return decimal.Round(currentPot * (decimal)factor + monthlyContribution * (decimal)((factor - 1d) / monthlyRate), 2);
    }

    public static decimal IsaBridgeRequired(decimal annualSpending, decimal annualPensionIncome, int bridgeYears)
        => Math.Max(0, annualSpending - annualPensionIncome) * Math.Max(0, bridgeYears);
}
