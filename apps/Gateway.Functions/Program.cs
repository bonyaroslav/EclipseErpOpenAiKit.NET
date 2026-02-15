using EclipseAi.AI;
using EclipseAi.Connectors.Erp;
using EclipseAi.Domain;
using EclipseAi.Governance;
using EclipseAi.Observability;
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

var app = builder.Build();

app.MapPost("/api/chat", async (
    ChatRequest request,
    HttpContext httpContext,
    IAiPlanner planner,
    IOrderExceptionSummarizer summarizer,
    IErpConnector erp,
    IRedactor redactor,
    IAuditStore auditStore,
    IdempotencyCache idempotencyCache,
    ILoggerFactory loggerFactory,
    CancellationToken ct) =>
{
    var logger = loggerFactory.CreateLogger("ChatEndpoint");
    var message = request?.Message ?? string.Empty;
    var incomingCorrelationId = httpContext.Request.Headers["x-correlation-id"].FirstOrDefault();
    var correlationId = Correlation.FromHeaderOrNew(incomingCorrelationId);
    using var correlationScope = CorrelationScope.Push(correlationId);

    var plannedCalls = planner.Plan(message);
    var executedCalls = new List<ToolCall>();
    var evidence = new List<Evidence>();
    var answerParts = new List<string>();

    foreach (var plannedCall in plannedCalls)
    {
        if (!ToolPolicy.IsAllowed(plannedCall.Name))
        {
            continue;
        }

        if (!ToolPolicy.IsDraftWriteAllowed(plannedCall.Name, plannedCall.Args))
        {
            continue;
        }

        switch (plannedCall.Name)
        {
            case "GetInventoryAvailability":
            {
                var itemId = plannedCall.Args["itemId"].ToString() ?? "ITEM-123";
                var warehouseId = plannedCall.Args["warehouseId"].ToString() ?? "MAD";
                var inventory = await erp.GetInventoryAsync(itemId, warehouseId, ct);

                executedCalls.Add(plannedCall);
                answerParts.Add($"{inventory.ItemId} in {inventory.WarehouseId}: {inventory.AvailableQty} available, ETA {inventory.EtaUtc}.");
                evidence.Add(new Evidence("erp.inventory", "itemId", inventory.ItemId));
                evidence.Add(new Evidence("erp.inventory", "warehouseId", inventory.WarehouseId));
                evidence.Add(new Evidence("erp.inventory", "availableQty", inventory.AvailableQty));
                evidence.Add(new Evidence("erp.inventory", "etaUtc", inventory.EtaUtc));
                break;
            }

            case "CreateDraftSalesOrder":
            {
                var idempotencyKey = plannedCall.Args["idempotencyKey"].ToString() ?? string.Empty;
                if (idempotencyCache.TryGet(idempotencyKey, out var cachedDraftId))
                {
                    executedCalls.Add(plannedCall);
                    answerParts.Add($"Draft already created: {cachedDraftId} (idempotent replay).");
                    evidence.Add(new Evidence("erp.draftOrder", "draftId", cachedDraftId));
                    break;
                }

                var dto = BuildDraftRequest(plannedCall.Args);
                var draft = await erp.CreateDraftOrderAsync(dto, ct);
                idempotencyCache.Set(idempotencyKey, draft.DraftId);

                executedCalls.Add(plannedCall);
                answerParts.Add($"Draft created: {draft.DraftId}. Commit remains disabled by default.");
                evidence.Add(new Evidence("erp.draftOrder", "draftId", draft.DraftId));
                evidence.Add(new Evidence("erp.draftOrder", "status", draft.Status));
                break;
            }

            case "ExplainOrderException":
            {
                var orderId = plannedCall.Args["orderId"].ToString() ?? "SO-456";
                var context = await erp.GetOrderExceptionContextAsync(orderId, ct);
                var governedData = BuildGovernedOrderExceptionData(context.Data, redactor);

                executedCalls.Add(plannedCall);
                var summary = summarizer.Summarize(context.OrderId, context.SummaryCode, governedData);
                answerParts.Add(string.IsNullOrWhiteSpace(summary) ? $"Order {context.OrderId} delayed ({context.SummaryCode})." : summary);

                foreach (var pair in governedData)
                {
                    if (!IsAllowlistedEvidenceField(pair.Key))
                    {
                        continue;
                    }

                    evidence.Add(new Evidence("erp.orderException", pair.Key, pair.Value));
                }

                break;
            }
        }
    }

    if (answerParts.Count == 0)
    {
        answerParts.Add("No eligible tool call was executed.");
    }

    var response = new ChatResponse(
        correlationId,
        string.Join(" ", answerParts),
        executedCalls,
        evidence,
        $".audit/{correlationId}.json");

    var auditToolCalls = response.ToolCalls
        .Select(static call => new Dictionary<string, object?>
        {
            ["name"] = call.Name,
            ["args"] = call.Args
        })
        .ToArray();
    var auditEvidence = response.Evidence
        .Select(static item => new Dictionary<string, object?>
        {
            ["source"] = item.Source,
            ["path"] = item.Path,
            ["value"] = item.Value
        })
        .ToArray();

    var redactedPayload = redactor.Redact(new Dictionary<string, object?>
    {
        ["correlationId"] = response.CorrelationId,
        ["answer"] = response.Answer,
        ["toolCalls"] = auditToolCalls,
        ["evidence"] = auditEvidence,
        ["auditRef"] = response.AuditRef
    });

    await auditStore.WriteAsync(correlationId, redactedPayload, ct);
    logger.LogInformation("chat_completed correlationId={CorrelationId} toolCalls={ToolCallCount}", correlationId, executedCalls.Count);

    return Results.Ok(response);
});

app.MapGet("/health", () => Results.Ok(new { ok = true }));

app.Run();

static CreateDraftOrderDto BuildDraftRequest(IReadOnlyDictionary<string, object> args)
{
    var customerId = args["customerId"].ToString() ?? "ACME";
    var shipDate = args["shipDate"].ToString() ?? "2030-01-01";
    var idempotencyKey = args["idempotencyKey"].ToString() ?? "demo-key-001";

    var lines = new List<DraftLineDto>
    {
        new("ITEM-123", 10)
    };

    if (args.TryGetValue("lines", out var linesObj) && linesObj is IEnumerable<object> rawLines)
    {
        lines.Clear();
        foreach (var rawLine in rawLines)
        {
            if (rawLine is not IReadOnlyDictionary<string, object> map)
            {
                continue;
            }

            var sku = map.TryGetValue("sku", out var skuValue) ? skuValue?.ToString() : "ITEM-123";
            var qty = map.TryGetValue("qty", out var qtyValue) && int.TryParse(qtyValue?.ToString(), out var parsedQty)
                ? parsedQty
                : 10;
            lines.Add(new DraftLineDto(sku ?? "ITEM-123", qty));
        }
    }

    return new CreateDraftOrderDto(customerId, shipDate, lines, idempotencyKey);
}

static bool IsAllowlistedEvidenceField(string field)
{
    return field is "holds" or "backorderedSkus" or "arOverdueDays" or "warehouse";
}

static IReadOnlyDictionary<string, object> BuildGovernedOrderExceptionData(
    IReadOnlyDictionary<string, object> data,
    IRedactor redactor)
{
    var allowlisted = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    foreach (var pair in data)
    {
        if (!IsAllowlistedEvidenceField(pair.Key))
        {
            continue;
        }

        allowlisted[pair.Key] = pair.Value;
    }

    var redacted = redactor.Redact(allowlisted);
    return ToReadOnlyDictionary(redacted, allowlisted);
}

static IReadOnlyDictionary<string, object> ToReadOnlyDictionary(object redacted, IReadOnlyDictionary<string, object> fallback)
{
    return redacted is IReadOnlyDictionary<string, object> map
        ? map
        : fallback;
}

public partial class Program;
