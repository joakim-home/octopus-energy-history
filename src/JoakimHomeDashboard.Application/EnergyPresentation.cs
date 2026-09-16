using System.Globalization;
using JoakimHomeDashboard.Domain;

namespace JoakimHomeDashboard.Application;

public static class ChartPresentation
{
    public static bool HasFiniteValues(IEnumerable<double> values) => values.Any(double.IsFinite);

    public static string FormatHover(IReadOnlyCollection<ChartHoverPoint> points)
    {
        if (points.Count == 0) return "No plotted value at this date";
        var date = points.First().Date.ToString("d MMM yyyy", CultureInfo.GetCultureInfo("en-GB"));
        return $"{date} · {string.Join(" · ", points.Select(point => $"{point.SeriesName}{Band(point.RateBand)}: {Format(point)}"))}";
    }

    private static string Band(ImportRateBand band) => band == ImportRateBand.Unknown ? "" : $" ({(band == ImportRateBand.OffPeak ? "off-peak" : "peak")})";
    private static string Format(ChartHoverPoint point) => point.Unit == "£"
        ? point.Value.ToString("C4", CultureInfo.GetCultureInfo("en-GB"))
        : $"{point.Value:N3} {point.Unit}";
}
