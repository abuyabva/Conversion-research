using ConversionAgent;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 16_384);
var provider = builder.Configuration["Data:Provider"] ?? "Synthetic";
if (provider == "Postgres")
{
    var connection = builder.Configuration.GetConnectionString("Analytics")
        ?? throw new InvalidOperationException("ConnectionStrings__Analytics is required for Postgres.");
    builder.Services.AddSingleton(NpgsqlDataSource.Create(connection));
    builder.Services.AddSingleton<IAnalytics, PostgresAnalytics>();
}
else if (provider == "Synthetic") builder.Services.AddSingleton<IAnalytics, SyntheticAnalytics>();
else throw new InvalidOperationException("Data__Provider must be Synthetic or Postgres.");
builder.Services.AddHttpClient<IModelClient, LlmClient>(c =>
    c.Timeout = TimeSpan.FromSeconds(LlmClient.TimeoutSeconds(builder.Configuration)));
builder.Services.AddTransient<ToolRunner>();
builder.Services.AddTransient<Agent>();
builder.Services.AddRateLimiter(options => {
    options.RejectionStatusCode = 429;
    options.AddFixedWindowLimiter("chat", o => {
        o.PermitLimit = 10; o.Window = TimeSpan.FromMinutes(1); o.QueueLimit = 0;
        o.AutoReplenishment = true;
    });
});
var app = builder.Build();
var researchTimeout = LlmClient.ResearchTimeoutSeconds(builder.Configuration);
app.Use(async (context, next) => {
    context.Response.Headers["Cache-Control"] = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self'; script-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'none'";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.MapGet("/api/info", (IConfiguration config) => new {
    mode = config["Agent:Mode"] ?? "Demo", dataProvider = provider,
    researchTimeoutSeconds = researchTimeout,
    datasetStart = Fixtures.Start, datasetEndExclusive = Fixtures.End,
    current = Fixtures.Current, baseline = Fixtures.Baseline
});
app.MapGet("/api/health", async (IAnalytics analytics, CancellationToken ct) => {
    try {
        await analytics.Query(Fixtures.Current, "all", ct);
        return Results.Ok(new { status = "ok", dataProvider = provider });
    } catch (Exception ex) when (ex is NpgsqlException or TimeoutException) {
        return Results.Json(new { status = "data_unavailable" }, statusCode: 503);
    }
});
app.MapPost("/api/chat", async (ChatInput input, Agent agent, HttpContext context) => {
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    deadline.CancelAfter(TimeSpan.FromSeconds(researchTimeout));
    try { return Results.Ok(await agent.Run(input, deadline.Token)); }
    catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) {
        return Results.Json(new { status = "timeout", answer = $"Исследование превысило {researchTimeout} секунд. Проверьте скорость модели или уточните запрос." }, statusCode: 504);
    }
}).RequireRateLimiting("chat");
app.Run();
