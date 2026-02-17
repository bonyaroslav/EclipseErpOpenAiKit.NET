using EclipseAi.AI;
using EclipseAi.Connectors.Erp;
using EclipseAi.Domain;
using EclipseAi.Governance;
using Gateway.Functions;
using Microsoft.Extensions.DependencyInjection.Extensions;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.AddHttpClient<IOpenAiClient, HttpOpenAiClient>(http =>
{
    http.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.TryAddSingleton<IAiPlanner>(sp =>
{
    var openAiApiKey = config["OPENAI_API_KEY"];
    var openAiMode = config["OPENAI_MODE"];
    var openAiClient = sp.GetService<IOpenAiClient>();
    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("OpenAiPlanner");
    return PlannerFactory.Create(
        openAiApiKey,
        openAiMode,
        openAiClient,
        onFallback: details => logger.LogWarning("openai_planner_fallback {Details}", details),
        onDecision: details => logger.LogInformation("openai_planner_decision {Details}", details));
});
builder.Services.TryAddSingleton<IOrderExceptionSummarizer>(sp =>
{
    var openAiApiKey = config["OPENAI_API_KEY"];
    var openAiMode = config["OPENAI_MODE"];
    var enableSummarization = string.Equals(config["OPENAI_SUMMARIZE"], "1", StringComparison.OrdinalIgnoreCase);
    var openAiClient = sp.GetService<IOpenAiClient>();
    return PlannerFactory.CreateSummarizer(openAiApiKey, openAiMode, enableSummarization, openAiClient);
});
builder.Services.TryAddSingleton<IRedactor, MapRedactor>();
builder.Services.AddHttpClient<IErpConnector, HttpErpConnector>(http =>
{
    http.BaseAddress = new Uri("http://localhost:5080");
});
builder.Services.AddSingleton<IAuditStore, FileAuditStore>();
builder.Services.AddSingleton<IdempotencyCache>();
builder.Services.AddSingleton<IChatToolHandler, InventoryToolHandler>();
builder.Services.AddSingleton<IChatToolHandler, DraftSalesOrderToolHandler>();
builder.Services.AddSingleton<IChatToolHandler, ExplainOrderExceptionToolHandler>();
builder.Services.AddSingleton<ChatOrchestrator>();

var app = builder.Build();

app.Logger.LogInformation(
    "gateway_startup openai_mode={OpenAiMode} openai_key_present={OpenAiKeyPresent} openai_summarize={OpenAiSummarize}",
    string.IsNullOrWhiteSpace(config["OPENAI_MODE"]) ? "emulated" : config["OPENAI_MODE"],
    !string.IsNullOrWhiteSpace(config["OPENAI_API_KEY"]),
    string.Equals(config["OPENAI_SUMMARIZE"], "1", StringComparison.OrdinalIgnoreCase));

app.MapPost("/api/chat", async (
    ChatRequest request,
    HttpContext httpContext,
    ChatOrchestrator orchestrator,
    CancellationToken ct) =>
{
    var incomingCorrelationId = httpContext.Request.Headers["x-correlation-id"].FirstOrDefault();
    var response = await orchestrator.HandleAsync(request, incomingCorrelationId, ct);
    return Results.Ok(response);
});

app.MapGet("/health", () => Results.Ok(new { ok = true }));

app.Run();

public partial class Program;
