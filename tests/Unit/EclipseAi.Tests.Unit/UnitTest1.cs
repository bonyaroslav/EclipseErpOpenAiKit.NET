using EclipseAi.AI;
using EclipseAi.Governance;

namespace EclipseAi.Tests.Unit;

public class PlannerTests
{
    [Fact]
    public void Plan_InventoryMessage_UsesInventoryTool()
    {
        var planner = new FakePlanner();

        var calls = planner.Plan("Do we have ITEM-123 in warehouse MAD?");

        var call = Assert.Single(calls);
        Assert.Equal("GetInventoryAvailability", call.Name);
        Assert.Equal("ITEM-123", call.Args["itemId"]);
        Assert.Equal("MAD", call.Args["warehouseId"]);
    }

    [Fact]
    public void Plan_DraftMessage_UsesDeterministicShipDate()
    {
        var planner = new FakePlanner();

        var calls = planner.Plan("Create a draft order for ACME: 10x ITEM-123");

        var call = Assert.Single(calls);
        Assert.Equal("CreateDraftSalesOrder", call.Name);
        Assert.Equal("2030-01-01", call.Args["shipDate"]);
        Assert.Equal("demo-key-001", call.Args["idempotencyKey"]);
    }

    [Fact]
    public void Plan_OrderExceptionMessage_UsesExceptionTool()
    {
        var planner = new FakePlanner();

        var calls = planner.Plan("Why is SO-456 delayed?");

        var call = Assert.Single(calls);
        Assert.Equal("ExplainOrderException", call.Name);
        Assert.Equal("SO-456", call.Args["orderId"]);
    }
}

public class GovernanceTests
{
    [Fact]
    public void ToolPolicy_RejectsUnknownTool()
    {
        Assert.False(ToolPolicy.IsAllowed("DeleteAllOrders"));
    }

    [Fact]
    public void ToolPolicy_RequiresIdempotencyKeyForDraftWrite()
    {
        var args = new Dictionary<string, object>();

        Assert.False(ToolPolicy.IsDraftWriteAllowed("CreateDraftSalesOrder", args));
    }

    [Fact]
    public void Redactor_RedactsSensitiveFieldNames()
    {
        var redactor = new MapRedactor();
        var payload = new Dictionary<string, object?>
        {
            ["customerName"] = "Alice",
            ["warehouse"] = "MAD"
        };

        var redacted = Assert.IsType<Dictionary<string, object?>>(redactor.Redact(payload));
        Assert.Equal("[REDACTED]", redacted["customerName"]);
        Assert.Equal("MAD", redacted["warehouse"]);
    }

    [Fact]
    public void Redactor_RedactsSensitiveFieldNamesRecursively()
    {
        var redactor = new MapRedactor();
        var payload = new Dictionary<string, object?>
        {
            ["toolCalls"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["name"] = "CreateDraftSalesOrder",
                    ["args"] = new Dictionary<string, object>
                    {
                        ["customerName"] = "Alice"
                    }
                }
            }
        };

        var redacted = Assert.IsType<Dictionary<string, object?>>(redactor.Redact(payload));
        var toolCalls = Assert.IsType<object[]>(redacted["toolCalls"]);
        var firstCall = Assert.IsType<Dictionary<string, object?>>(toolCalls[0]);
        var args = Assert.IsType<Dictionary<string, object?>>(firstCall["args"]);
        Assert.Equal("[REDACTED]", args["customerName"]);
    }
}
