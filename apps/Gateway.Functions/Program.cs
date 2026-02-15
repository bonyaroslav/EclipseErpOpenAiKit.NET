using EclipseAi.AI;
using EclipseAi.Connectors.Erp;
using EclipseAi.Domain;
using EclipseAi.Governance;
using EclipseAi.Observability;
using Gateway.Functions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IAiPlanner>(_ =>
{
    var openAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    return PlannerFactory.Create(openAiApiKey);
});
builder.Services.AddSingleton<IRedactor, MapRedactor>();
builder.Services.AddHttpClient<IErpConnector, HttpErpConnector>(http =>
{
    http.BaseAddress = new Uri("http://localhost:5080");
});
builder.Services.AddSingleton<IAuditStore, FileAuditStore>();
builder.Services.AddSingleton<IdempotencyCache>();

var app = builder.Build();

app.MapPost("/api/chat", async (
    ChatRequest request,
    IAiPlanner planner,
    IErpConnector erp,
    IRedactor redactor,
    IAuditStore auditStore,
    IdempotencyCache idempotencyCache,
    ILoggerFactory loggerFactory,
    CancellationToken ct) =>
{
    var logger = loggerFactory.CreateLogger("ChatEndpoint");
    var message = request?.Message ?? string.Empty;
    var correlationId = Correlation.NewId();
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

                executedCalls.Add(plannedCall);
                answerParts.Add($"Order {context.OrderId} delayed ({context.SummaryCode}).");

                foreach (var pair in context.Data)
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

    var redactedPayload = redactor.Redact(new Dictionary<string, object?>
    {
        ["correlationId"] = response.CorrelationId,
        ["answer"] = response.Answer,
        ["toolCalls"] = response.ToolCalls,
        ["evidence"] = response.Evidence,
        ["auditRef"] = response.AuditRef
    });

    await auditStore.WriteAsync(correlationId, redactedPayload, ct);
    logger.LogInformation("chat_completed correlationId={CorrelationId} toolCalls={ToolCallCount}", correlationId, executedCalls.Count);

    return Results.Ok(response);
});

app.MapGet("/health", () => Results.Ok(new { ok = true }));

app.Run();

public partial class Program;

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
