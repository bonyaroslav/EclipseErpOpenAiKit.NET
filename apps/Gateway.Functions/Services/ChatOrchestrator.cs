using EclipseAi.AI;
using EclipseAi.Connectors.Erp;
using EclipseAi.Domain;
using EclipseAi.Governance;
using EclipseAi.Observability;

namespace Gateway.Functions;

public sealed class ChatOrchestrator(
    IAiPlanner planner,
    IOrderExceptionSummarizer summarizer,
    IErpConnector erp,
    IRedactor redactor,
    IAuditStore auditStore,
    IdempotencyCache idempotencyCache,
    ILogger<ChatOrchestrator> logger)
{
    public async Task<ChatResponse> HandleAsync(ChatRequest request, string? incomingCorrelationId, CancellationToken ct)
    {
        var message = request?.Message ?? string.Empty;
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
                    if (!TryGetRequiredString(plannedCall.Args, "itemId", out var itemId, out var error) ||
                        !TryGetRequiredString(plannedCall.Args, "warehouseId", out var warehouseId, out error))
                    {
                        RejectToolCall(plannedCall, error, answerParts);
                        break;
                    }

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
                    if (!TryBuildDraftRequest(plannedCall.Args, out var dto, out var error))
                    {
                        RejectToolCall(plannedCall, error, answerParts);
                        break;
                    }

                    var idempotencyKey = dto.IdempotencyKey;
                    var payloadHash = IdempotencyCache.ComputePayloadHash(dto);
                    var reservation = idempotencyCache.ReserveDraft(idempotencyKey, payloadHash);
                    if (reservation.Status == IdempotencyStatus.Existing)
                    {
                        executedCalls.Add(plannedCall);
                        answerParts.Add($"Draft already created: {reservation.DraftId} (idempotent replay).");
                        evidence.Add(new Evidence("erp.draftOrder", "draftId", reservation.DraftId));
                        break;
                    }

                    if (reservation.Status == IdempotencyStatus.InProgress)
                    {
                        answerParts.Add($"Draft creation already in progress for idempotency key {idempotencyKey}.");
                        break;
                    }

                    if (reservation.Status == IdempotencyStatus.Conflict)
                    {
                        answerParts.Add($"Idempotency key reuse detected with different payload: {idempotencyKey}.");
                        break;
                    }

                    DraftOrderDto draft;
                    try
                    {
                        draft = await erp.CreateDraftOrderAsync(dto, ct);
                        idempotencyCache.CompleteDraft(idempotencyKey, payloadHash, draft.DraftId);
                    }
                    catch
                    {
                        idempotencyCache.FailDraft(idempotencyKey, payloadHash);
                        throw;
                    }

                    executedCalls.Add(plannedCall);
                    answerParts.Add($"Draft created: {draft.DraftId}. Commit remains disabled by default.");
                    evidence.Add(new Evidence("erp.draftOrder", "draftId", draft.DraftId));
                    evidence.Add(new Evidence("erp.draftOrder", "status", draft.Status));
                    break;
                }

                case "ExplainOrderException":
                {
                    if (!TryGetRequiredString(plannedCall.Args, "orderId", out var orderId, out var error))
                    {
                        RejectToolCall(plannedCall, error, answerParts);
                        break;
                    }

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
        TryLogInformation("chat_completed correlationId={CorrelationId} toolCalls={ToolCallCount}", correlationId, executedCalls.Count);

        return response;
    }

    private void RejectToolCall(ToolCall plannedCall, string reason, List<string> answerParts)
    {
        TryLogWarning("tool_call_rejected name={ToolName} reason={Reason}", plannedCall.Name, reason);
        answerParts.Add($"Tool call rejected: {plannedCall.Name} invalid arguments ({reason}).");
    }

    private void TryLogInformation(string message, params object[] args)
    {
        try
        {
            logger.LogInformation(message, args);
        }
        catch
        {
            // Keep chat flow resilient when host logging sinks are unavailable.
        }
    }

    private void TryLogWarning(string message, params object[] args)
    {
        try
        {
            logger.LogWarning(message, args);
        }
        catch
        {
            // Keep chat flow resilient when host logging sinks are unavailable.
        }
    }

    private static bool TryBuildDraftRequest(
        IReadOnlyDictionary<string, object> args,
        out CreateDraftOrderDto dto,
        out string error)
    {
        dto = default!;
        if (!TryGetRequiredString(args, "customerId", out var customerId, out error))
        {
            return false;
        }

        if (!TryGetRequiredString(args, "shipDate", out var shipDate, out error))
        {
            return false;
        }

        if (!TryGetRequiredString(args, "idempotencyKey", out var idempotencyKey, out error))
        {
            return false;
        }

        if (!TryGetRequiredLines(args, out var lines, out error))
        {
            return false;
        }

        dto = new CreateDraftOrderDto(customerId, shipDate, lines, idempotencyKey);
        return true;
    }

    private static bool TryGetRequiredLines(
        IReadOnlyDictionary<string, object> args,
        out List<DraftLineDto> lines,
        out string error)
    {
        lines = new List<DraftLineDto>();
        error = string.Empty;

        if (!args.TryGetValue("lines", out var rawLines) || rawLines is null)
        {
            error = "missing 'lines'";
            return false;
        }

        if (rawLines is not IEnumerable<object> entries)
        {
            error = "invalid 'lines'";
            return false;
        }

        foreach (var entry in entries)
        {
            if (entry is not IReadOnlyDictionary<string, object> lineMap)
            {
                error = "invalid 'lines'";
                return false;
            }

            if (!TryGetRequiredString(lineMap, "sku", out var sku, out error))
            {
                return false;
            }

            if (!TryGetRequiredInt(lineMap, "qty", out var qty, out error))
            {
                return false;
            }

            if (qty <= 0)
            {
                error = "invalid 'qty'";
                return false;
            }

            lines.Add(new DraftLineDto(sku, qty));
        }

        if (lines.Count == 0)
        {
            error = "missing 'lines'";
            return false;
        }

        return true;
    }

    private static bool TryGetRequiredString(
        IReadOnlyDictionary<string, object> args,
        string key,
        out string value,
        out string error)
    {
        value = string.Empty;
        error = string.Empty;

        if (!args.TryGetValue(key, out var raw) || raw is null)
        {
            error = $"missing '{key}'";
            return false;
        }

        var text = raw is string rawString ? rawString : raw.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            error = $"missing '{key}'";
            return false;
        }

        value = text.Trim();
        return true;
    }

    private static bool TryGetRequiredInt(
        IReadOnlyDictionary<string, object> args,
        string key,
        out int value,
        out string error)
    {
        value = 0;
        error = string.Empty;

        if (!args.TryGetValue(key, out var raw) || raw is null)
        {
            error = $"missing '{key}'";
            return false;
        }

        switch (raw)
        {
            case int intValue:
                value = intValue;
                return true;
            case long longValue when longValue is >= int.MinValue and <= int.MaxValue:
                value = (int)longValue;
                return true;
            case double doubleValue when Math.Abs(doubleValue % 1) < 0.000001:
                value = (int)doubleValue;
                return true;
            default:
                if (int.TryParse(raw.ToString(), out var parsed))
                {
                    value = parsed;
                    return true;
                }

                error = $"invalid '{key}'";
                return false;
        }
    }

    private static bool IsAllowlistedEvidenceField(string field)
    {
        return field is "holds" or "backorderedSkus" or "arOverdueDays" or "warehouse";
    }

    private static IReadOnlyDictionary<string, object> BuildGovernedOrderExceptionData(
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

    private static IReadOnlyDictionary<string, object> ToReadOnlyDictionary(object redacted, IReadOnlyDictionary<string, object> fallback)
    {
        return redacted is IReadOnlyDictionary<string, object> map
            ? map
            : fallback;
    }
}
