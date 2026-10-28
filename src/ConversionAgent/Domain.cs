using System.Globalization;

namespace ConversionAgent;

public sealed record Period(DateOnly Start, DateOnly End)
{
    public static Period Parse(string start, string end)
    {
        if (!DateOnly.TryParseExact(start, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var a)
            || !DateOnly.TryParseExact(end, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var b))
            throw new ArgumentException("Даты должны иметь формат YYYY-MM-DD.");
        if (b <= a || b.DayNumber - a.DayNumber > 90)
            throw new ArgumentException("Период должен быть от 1 до 90 дней, end не включается.");
        return new(a, b);
    }
    public DateTime StartUtc => Start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
    public DateTime EndUtc => End.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
}

public sealed record FunnelRow(string Segment, long Visitors, long Checkouts, long Purchasers)
{
    public decimal? ConversionPct => Visitors == 0 ? null : Math.Round(100m * Purchasers / Visitors, 4);
    public decimal? CheckoutPct => Visitors == 0 ? null : Math.Round(100m * Checkouts / Visitors, 4);
}
public sealed record ComparisonRow(string Segment, FunnelRow Baseline, FunnelRow Current)
{
    public decimal? DeltaPp => Current.ConversionPct - Baseline.ConversionPct;
}
public sealed record ToolCall(string Id, string Name, string Arguments);
public sealed record ToolTrace(string Id, string Name, string Arguments, bool Ok, object Result, long DurationMs);
public sealed record ChatInput(string Question, string? Start = null, string? End = null,
    string? BaselineStart = null, string? BaselineEnd = null);
public sealed record AgentOutput(string Status, string Answer, string Mode, string DataProvider,
    IReadOnlyList<ToolTrace> Trace);

public interface IAnalytics
{
    Task<IReadOnlyList<FunnelRow>> Query(Period period, string dimension, CancellationToken ct);
}

public static class Fixtures
{
    public const string Start = "2026-10-05";
    public const string End = "2026-10-19";
    public static Period Current => Period.Parse("2026-10-12", End);
    public static Period Baseline => Period.Parse(Start, "2026-10-12");
}
