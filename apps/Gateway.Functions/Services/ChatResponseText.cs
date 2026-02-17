namespace Gateway.Functions;

internal static class ChatResponseText
{
    public static string InventoryAvailability(string itemId, string warehouseId, int availableQty, string etaUtc)
        => $"Certainly. {itemId} is available in {warehouseId}. We currently have {availableQty} units, and the next ETA is {etaUtc}.";

    public static string DraftCreated(string draftId)
        => $"Done. I created draft sales order {draftId}. It remains in draft mode and will not be committed automatically.";

    public static string DraftAlreadyCreated(string draftId)
        => $"I found an existing draft for the same request: {draftId}. I reused it to avoid creating a duplicate order.";

    public static string DraftInProgress(string idempotencyKey)
        => $"A draft creation request is already in progress for idempotency key '{idempotencyKey}'. Please try again in a moment.";

    public static string IdempotencyKeyConflict(string idempotencyKey)
        => $"The idempotency key '{idempotencyKey}' was reused with a different payload. Please provide a new key and retry.";

    public static string OrderExceptionSummary(string orderId, string summaryCode, string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return $"I checked order {orderId}. It is currently delayed with code {summaryCode}.";
        }

        return $"I checked order {orderId}. {summary.Trim()}";
    }

    public static string NoEligibleToolCall()
        => "I couldn't safely execute an eligible action for that request. Please rephrase, and I can try again.";

    public static string RejectedToolCall(string toolName, string reason)
        => $"I couldn't execute {toolName} because the arguments were invalid ({reason}).";
}
