using System.Text.RegularExpressions;
using EclipseAi.Domain;

namespace EclipseAi.AI;

public sealed partial class FakePlanner : IAiPlanner
{
    private const string DemoShipDate = "2030-01-01";
    private const string DemoIdempotencyKey = "demo-key-001";

    private static readonly Regex s_salesOrderRegex = SalesOrderRegex();
    private static readonly Regex s_itemRegex = ItemRegex();
    private static readonly Regex s_warehouseRegex = WarehouseRegex();

    public IReadOnlyList<ToolCall> Plan(string message)
    {
        var orderExceptionCall = TryCreateOrderExceptionCall(message);
        if (orderExceptionCall is not null)
        {
            return [orderExceptionCall];
        }

        if (message.Contains("draft", StringComparison.OrdinalIgnoreCase))
        {
            return [CreateDraftOrderCall(message)];
        }

        return [CreateInventoryCall(message)];
    }

    private static ToolCall? TryCreateOrderExceptionCall(string message)
    {
        var salesOrder = s_salesOrderRegex.Match(message).Value;
        if (string.IsNullOrWhiteSpace(salesOrder))
        {
            return null;
        }

        return new ToolCall(
            "ExplainOrderException",
            new Dictionary<string, object>
            {
                ["orderId"] = salesOrder.ToUpperInvariant(),
                ["role"] = "customer_support"
            });
    }

    private static ToolCall CreateDraftOrderCall(string message)
    {
        var item = s_itemRegex.Match(message).Value;
        var sku = string.IsNullOrWhiteSpace(item) ? "ITEM-123" : item.ToUpperInvariant();

        return new ToolCall(
            "CreateDraftSalesOrder",
            new Dictionary<string, object>
            {
                ["customerId"] = "ACME",
                ["lines"] = new object[] { new Dictionary<string, object> { ["sku"] = sku, ["qty"] = 10 } },
                ["shipDate"] = DemoShipDate,
                ["idempotencyKey"] = DemoIdempotencyKey
            });
    }

    private static ToolCall CreateInventoryCall(string message)
    {
        var item = s_itemRegex.Match(message).Value;
        var warehouse = s_warehouseRegex.Match(message).Value;
        if (string.IsNullOrWhiteSpace(warehouse))
        {
            warehouse = "MAD";
        }

        return new ToolCall(
            "GetInventoryAvailability",
            new Dictionary<string, object>
            {
                ["itemId"] = string.IsNullOrWhiteSpace(item) ? "ITEM-123" : item.ToUpperInvariant(),
                ["warehouseId"] = warehouse.ToUpperInvariant()
            });
    }

    [GeneratedRegex(@"SO-\d+", RegexOptions.IgnoreCase)]
    private static partial Regex SalesOrderRegex();

    [GeneratedRegex(@"ITEM-\d+", RegexOptions.IgnoreCase)]
    private static partial Regex ItemRegex();

    [GeneratedRegex(@"\b([A-Z]{3})\b")]
    private static partial Regex WarehouseRegex();
}
