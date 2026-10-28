using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ConversionAgent;

public sealed record ModelReply(string? Content, IReadOnlyList<ToolCall> Calls, JsonObject Raw);
public interface IModelClient
{
    Task<ModelReply> Complete(IReadOnlyList<JsonObject> messages, CancellationToken ct);
}
public sealed class ModelUnavailableException(string message) : Exception(message);

public sealed class LlmClient(HttpClient http, IConfiguration config) : IModelClient
{
    // Auto preserves existing .env files that already point to local Ollama.
    public static bool UsesOllama(IConfiguration config)
    {
        var provider = config["Llm:Provider"] ?? "Auto";
        if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase)) return true;
        if (provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)) return false;
        if (!provider.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Llm:Provider must be Auto, OpenAI or Ollama.");
        return Uri.TryCreate(config["Llm:BaseUrl"], UriKind.Absolute, out var uri)
            && IsLocalHost(uri) && uri.Port == 11434;
    }

    private static bool IsLocalHost(Uri uri) => uri.IsLoopback ||
        uri.Host.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase);

    public static bool IsAllowedEndpoint(Uri uri) =>
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment) &&
        (uri.Scheme == Uri.UriSchemeHttps ||
         (uri.Scheme == Uri.UriSchemeHttp && IsLocalHost(uri)));

    public static int TimeoutSeconds(IConfiguration config) =>
        Math.Clamp(config.GetValue<int?>("Llm:TimeoutSeconds") ?? (UsesOllama(config) ? 180 : 40), 1, 600);

    public static int ResearchTimeoutSeconds(IConfiguration config) =>
        Math.Clamp(config.GetValue<int?>("Agent:TimeoutSeconds") ?? (UsesOllama(config) ? 600 : 120), 1, 1800);

    public async Task<ModelReply> Complete(IReadOnlyList<JsonObject> messages, CancellationToken ct)
    {
        var ollama = UsesOllama(config);
        var key = config["Llm:ApiKey"];
        if (!ollama && string.IsNullOrWhiteSpace(key)) throw new ModelUnavailableException("На сервере не задан Llm__ApiKey.");
        var baseUrl = config["Llm:BaseUrl"] ?? "https://api.openai.com/v1/";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || !IsAllowedEndpoint(baseUri))
            throw new ModelUnavailableException("Некорректный адрес LLM API. Нужен HTTPS либо HTTP для localhost, loopback или host.docker.internal, без логина, query и fragment.");
        var endpoint = new Uri(baseUrl.TrimEnd('/') + "/chat/completions");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!ollama) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var schemas = JsonSerializer.SerializeToNode(ToolSchemas, ToolRunner.Json)!.AsArray();
        if (ollama)
            foreach (var tool in schemas) tool!["function"]!.AsObject().Remove("strict");
        var payload = new JsonObject {
            ["model"] = config["Llm:Model"] ?? (ollama ? "qwen2.5:3b" : "gpt-4.1-mini"),
            ["messages"] = JsonSerializer.SerializeToNode(messages, ToolRunner.Json),
            ["tools"] = schemas,
            ["stream"] = false
        };
        var maxTokens = Math.Clamp(config.GetValue<int?>("Llm:MaxTokens") ?? (ollama ? 4096 : 1800), 128, 32768);
        if (ollama) payload["max_tokens"] = maxTokens;
        else {
            payload["tool_choice"] = "auto";
            payload["max_completion_tokens"] = maxTokens;
        }
        request.Content = new StringContent(payload.ToJsonString(ToolRunner.Json), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new ModelUnavailableException($"LLM API вернул HTTP {(int)response.StatusCode}. Проверьте настройки модели, квоту и ключ.");
        try
        {
            var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            var choice = root?["choices"]?[0] ?? throw new JsonException();
            if (choice["finish_reason"]?.GetValue<string>() == "length")
                throw new ModelUnavailableException("Ответ модели оборван по лимиту токенов. Сократите вопрос.");
            var msg = choice["message"]?.AsObject() ?? throw new JsonException();
            var calls = new List<ToolCall>();
            if (msg["tool_calls"] is JsonArray array)
                foreach (var node in array)
                    calls.Add(new(node!["id"]!.GetValue<string>(), node["function"]!["name"]!.GetValue<string>(),
                        node["function"]!["arguments"]!.GetValue<string>()));
            return new(msg["content"]?.GetValue<string>(), calls, (JsonObject)msg.DeepClone());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NullReferenceException or ArgumentOutOfRangeException)
        {
            throw new ModelUnavailableException("LLM API вернул неподдерживаемый формат ответа.");
        }
    }

    private static object Schema(string name, string description, params string[] keys)
    {
        var properties = keys.ToDictionary(k => k, k => (object)(k == "dimension"
            ? new { type = "string", @enum = new[] { "device", "source", "device_source" } }
            : new { type = "string", description = "UTC date YYYY-MM-DD; end is exclusive" }));
        return new { type = "function", function = new {
            name, description, strict = true,
            parameters = new { type = "object", properties, required = keys, additionalProperties = false }
        } };
    }
    public static object[] ToolSchemas => [
        Schema("describe_metrics", "Describe metric definitions, dataset coverage and limitations."),
        Schema("get_funnel", "Calculate distinct visitor → checkout → purchaser funnel for a confirmed period.", "start", "end"),
        Schema("compare_segments", "Compare conversion by device, source or their intersection for confirmed periods.",
            "start", "end", "baseline_start", "baseline_end", "dimension")
    ];
}
