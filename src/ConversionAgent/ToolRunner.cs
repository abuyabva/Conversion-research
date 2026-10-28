using System.Diagnostics;
using System.Text.Json;

namespace ConversionAgent;

public sealed class ToolRunner(IAnalytics analytics)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static object Metrics => new {
        dataset = "Synthetic, UTC, 2026-10-05 <= visit < 2026-10-19",
        funnel = "Distinct users: first visit → later checkout → later paid order, all before end (exclusive).",
        conversion = "100 * purchasers / visitors; null when visitors=0. NOT purchasers/checkouts.",
        delta = "Current conversion minus baseline conversion, in percentage points (pp).",
        dimensions = new[] { "device", "source", "device_source" },
        limitation = "One visit per user in fixture. Period windows truncate later purchases; not a fixed-window cohort metric. Segment association is not causation."
    };

    public async Task<ToolTrace> Execute(ToolCall call, Period current, Period baseline, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var doc = JsonDocument.Parse(call.Arguments);
            var args = doc.RootElement;
            if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("Аргументы должны быть объектом.");
            object result;
            switch (call.Name)
            {
                case "describe_metrics":
                    CheckKeys(args, []);
                    result = Metrics;
                    break;
                case "get_funnel":
                    CheckKeys(args, ["start", "end"]);
                    var p = ParsePeriod(args, "start", "end");
                    if (p != current && p != baseline) throw new ArgumentException("Период не совпадает с подтверждёнными датами.");
                    result = new { period = p, rows = await analytics.Query(p, "all", ct) };
                    break;
                case "compare_segments":
                    CheckKeys(args, ["start", "end", "baseline_start", "baseline_end", "dimension"]);
                    var cur = ParsePeriod(args, "start", "end");
                    var prev = ParsePeriod(args, "baseline_start", "baseline_end");
                    if (cur != current || prev != baseline) throw new ArgumentException("Сравнение не совпадает с подтверждёнными датами.");
                    var dimension = args.GetProperty("dimension").GetString() ?? "";
                    if (dimension is not ("device" or "source" or "device_source")) throw new ArgumentException("Недопустимое измерение.");
                    var a = await analytics.Query(prev, dimension, ct);
                    var b = await analytics.Query(cur, dimension, ct);
                    var rows = a.Select(x => x.Segment).Union(b.Select(x => x.Segment)).Order()
                        .Select(s => new ComparisonRow(s, a.FirstOrDefault(x => x.Segment == s) ?? new(s,0,0,0),
                            b.FirstOrDefault(x => x.Segment == s) ?? new(s,0,0,0))).ToArray();
                    result = new { current = cur, baseline = prev, dimension, rows };
                    break;
                default: throw new ArgumentException("Неизвестный инструмент.");
            }
            return new(call.Id, call.Name, call.Arguments, true, result, timer.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return new(call.Id, call.Name, call.Arguments, false,
                new { error = "invalid_arguments", message = "Проверьте имя инструмента, даты, измерение и JSON-параметры." }, timer.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or TimeoutException)
        {
            return new(call.Id, call.Name, call.Arguments, false,
                new { error = "data_unavailable", message = "Источник данных недоступен. Расчёт не выполнен." }, timer.ElapsedMilliseconds);
        }
    }

    private static Period ParsePeriod(JsonElement args, string a, string b) =>
        Period.Parse(args.GetProperty(a).GetString() ?? "", args.GetProperty(b).GetString() ?? "");
    private static void CheckKeys(JsonElement args, string[] keys)
    {
        var actual = args.EnumerateObject().Select(x => x.Name).ToArray();
        if (actual.Length != keys.Length || actual.Except(keys).Any() || keys.Except(actual).Any())
            throw new ArgumentException("Неверный набор параметров.");
    }
}
