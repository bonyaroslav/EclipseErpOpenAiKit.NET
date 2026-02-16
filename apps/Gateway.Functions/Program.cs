using EclipseAi.AI;
using EclipseAi.Connectors.Erp;
using EclipseAi.Domain;
using EclipseAi.Governance;
using Gateway.Functions;
using Microsoft.Extensions.DependencyInjection.Extensions;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.TryAddSingleton<IAiPlanner>(sp =>
{
    var openAiApiKey = config["OPENAI_API_KEY"];
    var openAiMode = config["OPENAI_MODE"];
    var openAiClient = sp.GetService<IOpenAiClient>();
    return PlannerFactory.Create(openAiApiKey, openAiMode, openAiClient);
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
builder.Services.AddSingleton<ChatOrchestrator>();

var app = builder.Build();

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
