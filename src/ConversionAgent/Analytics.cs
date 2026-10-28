using Npgsql;

namespace ConversionAgent;

// Offline fixture matches db/init.sql. Production SQL is tested separately.
public sealed class SyntheticAnalytics : IAnalytics
{
    public Task<IReadOnlyList<FunnelRow>> Query(Period period, string dimension, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var rows = new List<FunnelRow>();
        for (int day = 0; day < 14; day++)
        {
            var date = new DateOnly(2026, 10, 5).AddDays(day);
            if (date < period.Start || date >= period.End) continue;
            for (int s = 0; s < 4; s++)
            {
                var device = s >= 2 ? "mobile" : "desktop";
                var source = s % 2 == 1 ? "paid" : "organic";
                var key = dimension switch {
                    "device" => device, "source" => source,
                    "device_source" => $"{device}/{source}", "all" => "all",
                    _ => throw new ArgumentException("Неизвестное измерение.")
                };
                bool dropped = day >= 7 && s == 3;
                rows.Add(new(key, 100, dropped ? 30 : 60, dropped ? 10 : 40));
            }
        }
        IReadOnlyList<FunnelRow> result = rows.GroupBy(x => x.Segment).OrderBy(x => x.Key)
            .Select(g => new FunnelRow(g.Key, g.Sum(x => x.Visitors), g.Sum(x => x.Checkouts), g.Sum(x => x.Purchasers))).ToArray();
        if (dimension == "all" && result.Count == 0) result = [new("all", 0, 0, 0)];
        return Task.FromResult(result);
    }
}

public sealed class PostgresAnalytics(NpgsqlDataSource dataSource) : IAnalytics
{
    // Only this allowlist is interpolated. Dates are typed parameters, never model SQL.
    public async Task<IReadOnlyList<FunnelRow>> Query(Period period, string dimension, CancellationToken ct)
    {
        var expression = dimension switch {
            "all" => "'all'", "device" => "device", "source" => "source",
            "device_source" => "device || '/' || source",
            _ => throw new ArgumentException("Неизвестное измерение.")
        };
        var sql = $"""
            SELECT {expression} AS segment, count(*) AS visitors,
                count(*) FILTER (WHERE checkout_at < $2) AS checkouts,
                count(*) FILTER (WHERE paid_at < $2) AS purchasers
            FROM funnel_users
            WHERE visit_at >= $1 AND visit_at < $2
            GROUP BY 1 ORDER BY 1
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.CommandTimeout = 10;
        command.Parameters.AddWithValue(period.StartUtc);
        command.Parameters.AddWithValue(period.EndUtc);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<FunnelRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)));
        if (dimension == "all" && rows.Count == 0) rows.Add(new("all", 0, 0, 0));
        return rows;
    }
}
