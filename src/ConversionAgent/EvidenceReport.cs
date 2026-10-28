using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ConversionAgent;

// Only trusted tool results enter the factual report, never model prose.
public static class EvidenceReport
{
    public static string Build(IReadOnlyList<ToolTrace> trace)
    {
        var text = new StringBuilder("Наблюдения из расчётов инструментов (сформированы сервером):\n");
        string Number(JsonElement e) => e.ValueKind == JsonValueKind.Null ? "не определена" : e.GetDecimal().ToString("0.####", CultureInfo.GetCultureInfo("ru-RU"));
        foreach (var t in trace.Where(t => t.Ok && t.Name is "get_funnel" or "compare_segments"))
        {
            var r = JsonSerializer.SerializeToElement(t.Result, ToolRunner.Json);
            if (t.Name == "get_funnel")
            {
                text.AppendLine($"\nПериод {r.GetProperty("period").GetProperty("start")} — {r.GetProperty("period").GetProperty("end")} (конец исключён), инструмент {t.Id}:");
                foreach (var row in r.GetProperty("rows").EnumerateArray())
                    text.AppendLine($"{row.GetProperty("segment")}: посетители {row.GetProperty("visitors")}, оплатили {row.GetProperty("purchasers")}, конверсия, %: {Number(row.GetProperty("conversionPct"))}.");
            }
            else
            {
                text.AppendLine($"\nСравнение по {r.GetProperty("dimension")}, инструмент {t.Id}; сначала базовый период, затем текущий:");
                var rows = r.GetProperty("rows").EnumerateArray().ToArray();
                foreach (var row in rows)
                    text.AppendLine($"{row.GetProperty("segment")}: {Number(row.GetProperty("baseline").GetProperty("conversionPct"))} → {Number(row.GetProperty("current").GetProperty("conversionPct"))} (%); изменение, п.п.: {Number(row.GetProperty("deltaPp"))}.");
                var declining = rows.Where(x => x.GetProperty("deltaPp").ValueKind == JsonValueKind.Number && x.GetProperty("deltaPp").GetDecimal() < 0).ToArray();
                if (declining.Length > 0)
                {
                    var min = declining.Min(x => x.GetProperty("deltaPp").GetDecimal());
                    text.AppendLine("Наибольшее снижение конверсии среди рассчитанных сегментов: " + string.Join(", ", declining.Where(x => x.GetProperty("deltaPp").GetDecimal() == min).Select(x => x.GetProperty("segment").GetString())) + ".");
                }
                else text.AppendLine("Среди сегментов с определённой конверсией снижение не найдено. Null не означает ноль.");
            }
        }
        text.AppendLine("\nГраницы вывода: показаны только выполненные расчёты. Отсутствующие показатели не восстановлены моделью. Наибольшее снижение доли не обязательно означает наибольший вклад в общий результат.");
        text.AppendLine("\nПричины не установлены. Для проверки причин нужны дополнительные данные: ошибки checkout и оплаты, история релизов и состав трафика. В учебном наборе их нет. Эти направления проверки — общие рекомендации, не обнаруженные факты.");
        text.Append("\nLLM выбирает инструменты; её свободный текст не используется в проверенном отчёте. Данные синтетические.");
        return text.ToString();
    }
}
