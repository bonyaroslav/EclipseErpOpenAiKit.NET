using EclipseAi.AI;
using EclipseAi.Connectors.Erp;
using EclipseAi.Domain;
using EclipseAi.Governance;
using Gateway.Functions;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;

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
var erpMode = config["ERP_MODE"];
if (string.Equals(erpMode, "infor", StringComparison.OrdinalIgnoreCase))
{
    var inforBaseUrl = config["INFOR_BASE_URL"] ?? "http://localhost:5080";
    builder.Services.AddHttpClient("InforToken", http =>
    {
        http.BaseAddress = new Uri(inforBaseUrl);
        http.Timeout = TimeSpan.FromSeconds(30);
    });
    builder.Services.AddHttpClient("InforApi", http =>
    {
        http.BaseAddress = new Uri(inforBaseUrl);
        http.Timeout = TimeSpan.FromSeconds(30);
    });
    builder.Services.AddSingleton<IInforTokenClient>(sp =>
    {
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("InforToken");
        var settings = new InforTokenClientSettings(
            config["INFOR_CLIENT_ID"] ?? string.Empty,
            config["INFOR_CLIENT_SECRET"] ?? string.Empty,
            config["INFOR_SCOPE"],
            config["INFOR_TOKEN_ENDPOINT"] ?? "/oauth/token");
        return new InforTokenClient(http, settings);
    });
    builder.Services.AddSingleton(sp =>
        new InforApiClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("InforApi"),
            sp.GetRequiredService<IInforTokenClient>()));
    builder.Services.AddSingleton<IErpConnector, InforErpConnector>();
}
else
{
    builder.Services.AddHttpClient<IErpConnector, HttpErpConnector>(http =>
    {
        http.BaseAddress = new Uri("http://localhost:5080");
    });
}
builder.Services.AddSingleton<IAuditStore, FileAuditStore>();
builder.Services.AddSingleton<IdempotencyCache>();
builder.Services.AddSingleton<IChatToolHandler, InventoryToolHandler>();
builder.Services.AddSingleton<IChatToolHandler, DraftSalesOrderToolHandler>();
builder.Services.AddSingleton<IChatToolHandler, ExplainOrderExceptionToolHandler>();
builder.Services.AddSingleton<ChatOrchestrator>();

var app = builder.Build();
var openAiLogPayloads = config["OPENAI_LOG_PAYLOADS"];
var isOpenAiPayloadLoggingEnabled =
    string.Equals(openAiLogPayloads?.Trim().Trim('"', '\''), "1", StringComparison.OrdinalIgnoreCase)
    || string.Equals(openAiLogPayloads?.Trim().Trim('"', '\''), "true", StringComparison.OrdinalIgnoreCase)
    || string.Equals(openAiLogPayloads?.Trim().Trim('"', '\''), "yes", StringComparison.OrdinalIgnoreCase)
    || string.Equals(openAiLogPayloads?.Trim().Trim('"', '\''), "on", StringComparison.OrdinalIgnoreCase);

app.Logger.LogInformation(
    "gateway_startup openai_mode={OpenAiMode} openai_key_present={OpenAiKeyPresent} openai_summarize={OpenAiSummarize} openai_log_payloads={OpenAiLogPayloads}",
    string.IsNullOrWhiteSpace(config["OPENAI_MODE"]) ? "emulated" : config["OPENAI_MODE"],
    !string.IsNullOrWhiteSpace(config["OPENAI_API_KEY"]),
    string.Equals(config["OPENAI_SUMMARIZE"], "1", StringComparison.OrdinalIgnoreCase),
    isOpenAiPayloadLoggingEnabled);

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
