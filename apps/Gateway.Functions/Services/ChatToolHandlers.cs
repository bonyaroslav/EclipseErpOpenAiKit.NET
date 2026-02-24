using System.Text.Json;
using EclipseAi.AI;
using EclipseAi.Connectors.Erp;
using EclipseAi.Domain;
using EclipseAi.Governance;

namespace Gateway.Functions;

public interface IChatToolHandler
{
    string ToolName { get; }
    Task<ToolExecutionResult> ExecuteAsync(ToolCall plannedCall, CancellationToken ct);
}

public sealed record ToolExecutionResult(bool Executed, string? AnswerPart, IReadOnlyList<Evidence> Evidence)
{
    public static ToolExecutionResult Skipped(string? answerPart = null)
        => new(false, answerPart, Array.Empty<Evidence>());

    public static ToolExecutionResult Done(string answerPart, IReadOnlyList<Evidence> evidence)
        => new(true, answerPart, evidence);
}

public sealed class InventoryToolHandler(IErpConnector erp) : IChatToolHandler
{
    public string ToolName => "GetInventoryAvailability";

    public async Task<ToolExecutionResult> ExecuteAsync(ToolCall plannedCall, CancellationToken ct)
    {
        if (!ToolArgReaders.TryGetRequiredString(plannedCall.Args, "itemId", out var itemId, out var error) ||
            !ToolArgReaders.TryGetRequiredString(plannedCall.Args, "warehouseId", out var warehouseId, out error))
        {
            return ToolExecutionResult.Skipped(ToolArgReaders.RejectedCallMessage(plannedCall.Name, error));
        }

        var inventory = await erp.GetInventoryAsync(itemId, warehouseId, ct);
        return ToolExecutionResult.Done(
            ChatResponseText.InventoryAvailability(inventory.ItemId, inventory.WarehouseId, inventory.AvailableQty, inventory.EtaUtc),
            new[]
            {
                new Evidence("erp.inventory", "itemId", inventory.ItemId),
                new Evidence("erp.inventory", "warehouseId", inventory.WarehouseId),
                new Evidence("erp.inventory", "availableQty", inventory.AvailableQty),
                new Evidence("erp.inventory", "etaUtc", inventory.EtaUtc)
            });
    }
}

public sealed class DraftSalesOrderToolHandler(IErpConnector erp, IdempotencyCache idempotencyCache) : IChatToolHandler
{
    public string ToolName => "CreateDraftSalesOrder";

    public async Task<ToolExecutionResult> ExecuteAsync(ToolCall plannedCall, CancellationToken ct)
    {
        if (!ToolArgReaders.TryBuildDraftRequest(plannedCall.Args, out var dto, out var error))
        {
            return ToolExecutionResult.Skipped(ToolArgReaders.RejectedCallMessage(plannedCall.Name, error));
        }

        var idempotencyKey = dto.IdempotencyKey;
        var payloadHash = IdempotencyCache.ComputePayloadHash(dto);
        var reservation = idempotencyCache.ReserveDraft(idempotencyKey, payloadHash);
        if (reservation.Status == IdempotencyStatus.Existing)
        {
            return ToolExecutionResult.Done(
                ChatResponseText.DraftAlreadyCreated(reservation.DraftId!),
                BuildDraftEvidence(reservation.DraftId, reservation.ExternalOrderNumber, reservation.DraftStatus));
        }

        if (reservation.Status == IdempotencyStatus.InProgress)
        {
            return ToolExecutionResult.Skipped(
                ChatResponseText.DraftInProgress(idempotencyKey));
        }

        if (reservation.Status == IdempotencyStatus.Conflict)
        {
            return ToolExecutionResult.Skipped(
                ChatResponseText.IdempotencyKeyConflict(idempotencyKey));
        }

        DraftOrderDto draft;
        try
        {
            draft = await erp.CreateDraftOrderAsync(dto, ct);
            idempotencyCache.CompleteDraft(idempotencyKey, payloadHash, draft);
        }
        catch
        {
            idempotencyCache.FailDraft(idempotencyKey, payloadHash);
            throw;
        }

        return ToolExecutionResult.Done(
            ChatResponseText.DraftCreated(draft.DraftId),
            BuildDraftEvidence(draft.DraftId, draft.ExternalOrderNumber, draft.Status));
    }

    private static IReadOnlyList<Evidence> BuildDraftEvidence(string? draftId, string? externalOrderNumber, string? status)
    {
        var evidence = new List<Evidence>();
        if (!string.IsNullOrWhiteSpace(draftId))
        {
            evidence.Add(new Evidence("erp.draftOrder", "draftId", draftId));
        }

        if (!string.IsNullOrWhiteSpace(externalOrderNumber))
        {
            evidence.Add(new Evidence("erp.draftOrder", "externalOrderNumber", externalOrderNumber));
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            evidence.Add(new Evidence("erp.draftOrder", "status", status));
        }

        return evidence;
    }
}

public sealed class ExplainOrderExceptionToolHandler(
    IErpConnector erp,
    IOrderExceptionSummarizer summarizer,
    IRedactor redactor) : IChatToolHandler
{
    public string ToolName => "ExplainOrderException";

    public async Task<ToolExecutionResult> ExecuteAsync(ToolCall plannedCall, CancellationToken ct)
    {
        if (!ToolArgReaders.TryGetRequiredString(plannedCall.Args, "orderId", out var orderId, out var error))
        {
            return ToolExecutionResult.Skipped(ToolArgReaders.RejectedCallMessage(plannedCall.Name, error));
        }

        var context = await erp.GetOrderExceptionContextAsync(orderId, ct);
        var governedData = BuildGovernedOrderExceptionData(context.Data, redactor);
        var summary = summarizer.Summarize(context.OrderId, context.SummaryCode, governedData);
        var answer = ChatResponseText.OrderExceptionSummary(context.OrderId, context.SummaryCode, summary);

        var evidence = new List<Evidence>();
        foreach (var pair in governedData)
        {
            if (!IsAllowlistedEvidenceField(pair.Key))
            {
                continue;
            }

            evidence.Add(new Evidence("erp.orderException", pair.Key, pair.Value));
        }

        return ToolExecutionResult.Done(answer, evidence);
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
        return redacted is IReadOnlyDictionary<string, object> map ? map : allowlisted;
    }
}

internal static class ToolArgReaders
{
    public static string RejectedCallMessage(string toolName, string reason)
        => ChatResponseText.RejectedToolCall(toolName, reason);

    public static bool TryBuildDraftRequest(
        IReadOnlyDictionary<string, object> args,
        out CreateDraftOrderDto dto,
        out string error)
    {
        dto = default!;
        if (!TryGetRequiredString(args, "customerId", out var customerId, out error))
        {
            return false;
        }

        if (!TryGetRequiredString(args, "requestedDate", out var requestedDate, out error))
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

        dto = new CreateDraftOrderDto(customerId, requestedDate, lines, idempotencyKey);
        return true;
    }

    public static bool TryGetRequiredString(
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

            if (!TryGetRequiredString(lineMap, "item", out var item, out error))
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

            if (!TryGetRequiredDecimal(lineMap, "unitPrice", out var unitPrice, out error))
            {
                return false;
            }

            if (unitPrice <= 0)
            {
                error = "invalid 'unitPrice'";
                return false;
            }

            lines.Add(new DraftLineDto(item, qty, unitPrice));
        }

        if (lines.Count == 0)
        {
            error = "missing 'lines'";
            return false;
        }

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
            case JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var elementValue):
                value = elementValue;
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

    private static bool TryGetRequiredDecimal(
        IReadOnlyDictionary<string, object> args,
        string key,
        out decimal value,
        out string error)
    {
        value = 0m;
        error = string.Empty;

        if (!args.TryGetValue(key, out var raw) || raw is null)
        {
            error = $"missing '{key}'";
            return false;
        }

        switch (raw)
        {
            case decimal decimalValue:
                value = decimalValue;
                return true;
            case double doubleValue:
                value = Convert.ToDecimal(doubleValue);
                return true;
            case JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var decimalValue):
                value = decimalValue;
                return true;
            case float floatValue:
                value = Convert.ToDecimal(floatValue);
                return true;
            case int intValue:
                value = intValue;
                return true;
            case long longValue:
                value = longValue;
                return true;
            default:
                if (decimal.TryParse(raw.ToString(), out var parsed))
                {
                    value = parsed;
                    return true;
                }

                error = $"invalid '{key}'";
                return false;
        }
    }
}
