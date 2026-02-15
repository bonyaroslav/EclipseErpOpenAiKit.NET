using System.Text.RegularExpressions;
using EclipseAi.Domain;

namespace EclipseAi.AI;

public interface IAiPlanner
{
    IReadOnlyList<ToolCall> Plan(string message);
}

public sealed class FakePlanner : IAiPlanner
{
    private static readonly Regex SoRegex = new(@"SO-\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ItemRegex = new(@"ITEM-\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WhRegex = new(@"\b([A-Z]{3})\b", RegexOptions.Compiled);

    public IReadOnlyList<ToolCall> Plan(string message)
    {
        // Extremely small deterministic planner:
        // - if message mentions SO-### => order exception copilot
        // - if message contains "draft" => create draft order
        // - else => inventory availability

        var so = SoRegex.Match(message).Value;
        if (!string.IsNullOrWhiteSpace(so))
        {
            return new[]
            {
                new ToolCall("ExplainOrderException", new Dictionary<string, object>
                {
                    ["orderId"] = so.ToUpperInvariant(),
                    ["role"] = "customer_support"
                })
            };
        }

        if (message.Contains("draft", StringComparison.OrdinalIgnoreCase))
        {
            var item = ItemRegex.Match(message).Value;
            return new[]
            {
                new ToolCall("CreateDraftSalesOrder", new Dictionary<string, object>
                {
                    ["customerId"] = "ACME",
                    ["lines"] = new object[]{ new Dictionary<string, object>{{"sku", string.IsNullOrWhiteSpace(item) ? "ITEM-123" : item.ToUpperInvariant()}, {"qty", 10}} },
                    ["shipDate"] = DateTime.UtcNow.Date.AddDays(1).ToString("yyyy-MM-dd"),
                    ["idempotencyKey"] = "demo-key-001"
                })
            };
        }

        var invItem = ItemRegex.Match(message).Value;
        var wh = WhRegex.Match(message).Value;
        if (string.IsNullOrWhiteSpace(wh)) wh = "MAD";

        return new[]
        {
            new ToolCall("GetInventoryAvailability", new Dictionary<string, object>
            {
                ["itemId"] = string.IsNullOrWhiteSpace(invItem) ? "ITEM-123" : invItem.ToUpperInvariant(),
                ["warehouseId"] = wh.ToUpperInvariant()
            })
        };
    }
}
