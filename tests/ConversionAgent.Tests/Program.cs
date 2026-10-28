using ConversionAgent;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Executable test harness, no test-framework dependency. Nonzero exit on failure.
int passed = 0, failed = 0;
async Task Test(string name, Func<Task> test) {
    try { await test(); Console.WriteLine($"PASS {name}"); passed++; }
    catch (Exception ex) { Console.WriteLine($"FAIL {name}: {ex.Message}"); failed++; }
}
void Eq<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}"); }
IConfiguration Config(string mode = "Demo") => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
    ["Agent:Mode"] = mode, ["Data:Provider"] = "Synthetic", ["Llm:ApiKey"] = "test-only-not-a-secret"
}).Build();
var analytics = new SyntheticAnalytics();
var tools = new ToolRunner(analytics);
var input = new ChatInput("Почему упала конверсия?", "2026-10-12", "2026-10-19", "2026-10-05", "2026-10-12");
var demo = new Agent(tools, new FakeModel(), Config());
var calls = new[] {
    new ToolCall("a","describe_metrics","{}"),
    new ToolCall("b","get_funnel","{\"start\":\"2026-10-05\",\"end\":\"2026-10-12\"}"),
    new ToolCall("c","get_funnel","{\"start\":\"2026-10-12\",\"end\":\"2026-10-19\"}"),
    new ToolCall("d","compare_segments","{\"start\":\"2026-10-12\",\"end\":\"2026-10-19\",\"baseline_start\":\"2026-10-05\",\"baseline_end\":\"2026-10-12\",\"dimension\":\"device_source\"}")
};
await Test("baseline 2800/1680/1120 = 40%",async()=>{var r=(await analytics.Query(Fixtures.Baseline,"all",default)).Single();Eq(2800L,r.Visitors);Eq(1680L,r.Checkouts);Eq(1120L,r.Purchasers);Eq<decimal?>(40,r.ConversionPct);});
await Test("current 2800/1470/910 = 32.5%",async()=>{var r=(await analytics.Query(Fixtures.Current,"all",default)).Single();Eq(2800L,r.Visitors);Eq(1470L,r.Checkouts);Eq(910L,r.Purchasers);Eq<decimal?>(32.5m,r.ConversionPct);});
await Test("mobile/paid 40 -> 10, -30 pp",async()=>{var a=(await analytics.Query(Fixtures.Baseline,"device_source",default)).Single(x=>x.Segment=="mobile/paid");var b=(await analytics.Query(Fixtures.Current,"device_source",default)).Single(x=>x.Segment=="mobile/paid");Eq<decimal?>(-30,new ComparisonRow(a.Segment,a,b).DeltaPp);});
await Test("other segments stable",async()=>{var b=await analytics.Query(Fixtures.Current,"device_source",default);Eq(true,b.Where(x=>x.Segment!="mobile/paid").All(x=>x.ConversionPct==40));});
await Test("end exclusive, one day = 400",async()=>Eq(400L,(await analytics.Query(Period.Parse("2026-10-05","2026-10-06"),"all",default)).Single().Visitors));
await Test("empty period -> null, not zero percent",async()=>{var r=(await analytics.Query(Period.Parse("2025-01-01","2025-01-02"),"all",default)).Single();Eq(0L,r.Visitors);Eq<decimal?>(null,r.ConversionPct);});
await Test("bad dates rejected",()=>{try{Period.Parse("yesterday","tomorrow");throw new Exception("accepted");}catch(ArgumentException){}return Task.CompletedTask;});
await Test("reversed and oversized periods rejected",()=>{foreach(var p in new[]{("2026-10-19","2026-10-12"),("2026-01-01","2026-10-19")}){try{Period.Parse(p.Item1,p.Item2);throw new Exception("accepted");}catch(ArgumentException){}}return Task.CompletedTask;});
await Test("ambiguous week -> clarification",async()=>Eq("clarification",(await demo.Run(new("Почему упала за неделю?"),default)).Status));
await Test("overlap rejected",async()=>Eq("invalid_request",(await demo.Run(input with { BaselineEnd="2026-10-15" },default)).Status));
await Test("unequal period lengths rejected",async()=>Eq("invalid_request",(await demo.Run(input with { BaselineStart="2026-10-06" },default)).Status));
await Test("demo complete and trace",async()=>{var r=await demo.Run(input,default);Eq("complete",r.Status);Eq(4,r.Trace.Count);Eq(true,r.Answer.Contains("32,5%"));});
await Test("empty data response",async()=>Eq("no_data",(await demo.Run(input with {Start="2025-01-08",End="2025-01-15",BaselineStart="2025-01-01",BaselineEnd="2025-01-08"},default)).Status));
await Test("unknown tool rejected",async()=>Eq(false,(await tools.Execute(new("bad","execute_sql","{}"),Fixtures.Current,Fixtures.Baseline,default)).Ok));
await Test("SQL injection dimension rejected",async()=>Eq(false,(await tools.Execute(calls[3] with {Arguments=calls[3].Arguments.Replace("device_source","device; DROP TABLE users")},Fixtures.Current,Fixtures.Baseline,default)).Ok));
await Test("malformed JSON rejected",async()=>Eq(false,(await tools.Execute(calls[2] with {Arguments="{"},Fixtures.Current,Fixtures.Baseline,default)).Ok));
await Test("unconfirmed dates rejected",async()=>Eq(false,(await tools.Execute(calls[2] with {Arguments="{\"start\":\"2020-01-01\",\"end\":\"2020-01-02\"}"},Fixtures.Current,Fixtures.Baseline,default)).Ok));
await Test("tool timeout reported, no analysis",async()=>Eq("tool_error",(await new Agent(new ToolRunner(new BrokenAnalytics()),new FakeModel(),Config()).Run(input,default)).Status));
await Test("LLM receives tool results and finishes",async()=>{var m=new FakeModel(calls);var r=await new Agent(tools,m,Config("Llm")).Run(input,default);Eq("complete",r.Status);Eq(4,r.Trace.Count);Eq(4,m.ToolResultsSeen);});
await Test("LLM unsupported answer blocked without evidence",async()=>Eq("insufficient_evidence",(await new Agent(tools,new FakeModel(),Config("Llm")).Run(input,default)).Status));
await Test("LLM tool-call loop bounded",async()=>Eq("limit_reached",(await new Agent(tools,new FakeModel([calls[0]],true),Config("Llm")).Run(input,default)).Status));
await Test("LLM HTTP error sanitized",async()=>{var client=new LlmClient(new HttpClient(new StubHttp(HttpStatusCode.Unauthorized,"private-provider-text")),Config("Llm"));var r=await new Agent(tools,client,Config("Llm")).Run(input,default);Eq("model_error",r.Status);Eq(false,r.Answer.Contains("private-provider-text"));});
await Test("LLM wire-format parses tool call",async()=>{var json="""{"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","content":null,"tool_calls":[{"id":"x","type":"function","function":{"name":"describe_metrics","arguments":"{}"}}]}}]}""";var h=new StubHttp(HttpStatusCode.OK,json);var client=new LlmClient(new HttpClient(h),Config("Llm"));var r=await client.Complete([new(){["role"]="user",["content"]="test"}],default);Eq("describe_metrics",r.Calls.Single().Name);Eq(true,h.Body!.Contains("compare_segments"));});
await Test("LLM malformed provider JSON handled",async()=>{var client=new LlmClient(new HttpClient(new StubHttp(HttpStatusCode.OK,"{}")),Config("Llm"));Eq("model_error",(await new Agent(tools,client,Config("Llm")).Run(input,default)).Status);});

foreach (var url in new[] { "http://localhost:11434/v1/", "http://127.0.0.1:11434/v1/",
    "http://[::1]:11434/v1/", "http://host.docker.internal:11434/v1/", "https://api.openai.com/v1/" })
    await Test($"allowed endpoint {url}", () => { Eq(true, LlmClient.IsAllowedEndpoint(new Uri(url))); return Task.CompletedTask; });
foreach (var url in new[] { "http://example.com/v1/", "http://host.docker.internal.evil.test:11434/",
    "ftp://localhost/file", "https://user:password@example.com/v1/", "http://localhost:11434/v1/?x=1",
    "http://localhost:11434/v1/#fragment" })
    await Test($"rejected endpoint {url}", () => { Eq(false, LlmClient.IsAllowedEndpoint(new Uri(url))); return Task.CompletedTask; });

IConfiguration OllamaConfig(string? explicitProvider = null) => new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string,string?> {
        ["Llm:BaseUrl"] = "http://host.docker.internal:11434/v1/",
        ["Llm:Model"] = "qwen2.5:3b", ["Llm:Provider"] = explicitProvider
    }).Build();
await Test("Ollama auto detection and timeout defaults", () => {
    Eq(true, LlmClient.UsesOllama(OllamaConfig()));
    Eq(false, LlmClient.UsesOllama(OllamaConfig("OpenAI")));
    Eq(180, LlmClient.TimeoutSeconds(OllamaConfig()));
    Eq(600, LlmClient.ResearchTimeoutSeconds(OllamaConfig()));
    Eq(40, LlmClient.TimeoutSeconds(Config()));
    Eq(120, LlmClient.ResearchTimeoutSeconds(Config()));
    return Task.CompletedTask;
});
await Test("Ollama request shape and replay of tool result", async () => {
    var json = """{"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","content":"","tool_calls":[{"id":"call_1","type":"function","function":{"name":"describe_metrics","arguments":"{}"}}]}}]}""";
    var handler = new StubHttp(HttpStatusCode.OK, json);
    var client = new LlmClient(new HttpClient(handler), OllamaConfig());
    var messages = new List<JsonObject> { new() { ["role"]="user", ["content"]="test" } };
    var reply = await client.Complete(messages, default);
    Eq("describe_metrics", reply.Calls.Single().Name);
    var body = JsonNode.Parse(handler.Body!)!;
    Eq("http://host.docker.internal:11434/v1/chat/completions", handler.Url);
    Eq<string?>(null, handler.Authorization);
    Eq(4096, body["max_tokens"]!.GetValue<int>());
    Eq(false, body["stream"]!.GetValue<bool>());
    Eq(true, body["max_completion_tokens"] is null && body["tool_choice"] is null);
    Eq(3, body["tools"]!.AsArray().Count);
    Eq(true, body["tools"]!.AsArray().All(t => t!["function"]!["strict"] is null));
    messages.Add(reply.Raw);
    messages.Add(new() { ["role"]="tool", ["tool_call_id"]="call_1", ["content"]="{}" });
    await client.Complete(messages, default);
    var replay = JsonNode.Parse(handler.Body!)!["messages"]!.AsArray();
    Eq("call_1", replay[1]!["tool_calls"]![0]!["id"]!.GetValue<string>());
    Eq("call_1", replay[2]!["tool_call_id"]!.GetValue<string>());
});
await Test("OpenAI request preserves token limit and strict schemas", async () => {
    var handler = new StubHttp(HttpStatusCode.OK, """{"choices":[{"message":{"role":"assistant","content":"ok"}}]}""");
    await new LlmClient(new HttpClient(handler), Config()).Complete([], default);
    var body = JsonNode.Parse(handler.Body!)!;
    Eq(1800, body["max_completion_tokens"]!.GetValue<int>());
    Eq("auto", body["tool_choice"]!.GetValue<string>());
    Eq(true, body["tools"]![0]!["function"]!["strict"]!.GetValue<bool>());
    Eq("Bearer test-only-not-a-secret", handler.Authorization);
});
await Test("remote HTTP rejected before sending request", async () => {
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
        ["Llm:BaseUrl"]="http://example.com/v1/", ["Llm:ApiKey"]="test"
    }).Build();
    var handler = new StubHttp(HttpStatusCode.OK, "{}");
    try { await new LlmClient(new HttpClient(handler), config).Complete([], default); throw new Exception("accepted"); }
    catch (ModelUnavailableException) { Eq<string?>(null, handler.Body); }
});

await Test("factual report uses tools instead of model prose", async () => {
    var result = await new Agent(tools, new FakeModel(calls), Config("Llm")).Run(input, default);
    Eq("complete", result.Status);
    Eq(true, result.Answer.Contains("desktop/paid: 40 → 40"));
    Eq(true, result.Answer.Contains("mobile/paid: 40 → 10"));
    Eq(true, result.Answer.Contains("Наибольшее снижение конверсии среди рассчитанных сегментов: mobile/paid."));
    Eq(false, result.Answer.Contains("Наблюдения подтверждены инструментами."));
});
await Test("empty evidence report never invents conversion", () => {
    var report = EvidenceReport.Build([]);
    Eq(false, report.Contains("40 → 10"));
    Eq(true, report.Contains("Причины не установлены"));
    return Task.CompletedTask;
});

var pg = Environment.GetEnvironmentVariable("TEST_POSTGRES");
if (!string.IsNullOrWhiteSpace(pg)) {
    await using var source = NpgsqlDataSource.Create(pg);
    var repository = new PostgresAnalytics(source);
    foreach(var period in new[]{Fixtures.Baseline,Fixtures.Current,Period.Parse("2025-01-01","2025-01-02")})
        foreach(var dim in new[]{"all","device","source","device_source"})
            await Test($"Postgres parity {period.Start} {dim}",async()=>{
                var expected=await analytics.Query(period,dim,default);var actual=await repository.Query(period,dim,default);
                Eq(JsonSerializer.Serialize(expected),JsonSerializer.Serialize(actual));
            });
} else Console.WriteLine("SKIP PostgreSQL integration: set TEST_POSTGRES to an initialized test database.");
Console.WriteLine($"RESULT: {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

sealed class BrokenAnalytics : IAnalytics {
    public Task<IReadOnlyList<FunnelRow>> Query(Period p,string d,CancellationToken ct) => throw new TimeoutException("private connection info");
}
sealed class FakeModel(ToolCall[]? calls=null,bool loop=false) : IModelClient {
    int round; public int ToolResultsSeen {get;private set;}
    public Task<ModelReply> Complete(IReadOnlyList<JsonObject> messages,CancellationToken ct) {
        ToolResultsSeen=messages.Count(m=>m["role"]?.GetValue<string>()=="tool");
        var useCalls=(round++==0||loop)?calls??[]:[];
        return Task.FromResult(new ModelReply("Наблюдения подтверждены инструментами. Причина требует проверки.",useCalls,new JsonObject{["role"]="assistant",["content"]="test"}));
    }
}
sealed class StubHttp(HttpStatusCode status,string json) : HttpMessageHandler {
    public string? Body {get;private set;}
    public string? Url {get;private set;}
    public string? Authorization {get;private set;}
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
        Url=request.RequestUri!.ToString();
        Authorization=request.Headers.Authorization?.ToString();
        Body=await request.Content!.ReadAsStringAsync(ct);
        return new HttpResponseMessage(status){Content=new StringContent(json,Encoding.UTF8,"application/json")};
    }
}
