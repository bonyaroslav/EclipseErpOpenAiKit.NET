using EclipseAi.AI;
using EclipseAi.Connectors.Erp;
using EclipseAi.Domain;
using EclipseAi.Governance;
using Gateway.Functions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EclipseAi.Tests.Integration;

public sealed class ChatOrchestratorTests
{
    [Fact]
    public async Task HandleAsync_InventoryFlow_ReturnsStableContract_AndWritesAudit()
    {
        var planner = new StubPlanner(
            new ToolCall(
                "GetInventoryAvailability",
                new Dictionary<string, object>
                {
                    ["itemId"] = "ITEM-123",
                    ["warehouseId"] = "MAD"
                }));
        var erp = new FakeErpConnector();
        var auditStore = new InMemoryAuditStore();
        var orchestrator = new ChatOrchestrator(
            planner,
            BuildHandlers(erp),
            new MapRedactor(),
            auditStore,
            NullLogger<ChatOrchestrator>.Instance);

        var response = await orchestrator.HandleAsync(
            new ChatRequest("Do we have ITEM-123 in warehouse MAD?"),
            incomingCorrelationId: "corr-test-1",
            CancellationToken.None);

        Assert.Equal("corr-test-1", response.CorrelationId);
        Assert.Equal("GetInventoryAvailability", response.ToolCalls.Single().Name);
        Assert.Contains(response.Evidence, e => e.Path == "itemId");
        Assert.Contains(response.Evidence, e => e.Path == "warehouseId");
        Assert.True(auditStore.Contains("corr-test-1"));
        Assert.Equal(1, erp.InventoryCallCount);
        Assert.Equal(("ITEM-123", "MAD"), erp.InventoryRequests.Single());
    }

    [Fact]
    public async Task HandleAsync_DraftFlow_ReplaysIdempotently_ForSamePayload()
    {
        CleanupIdempotencyStore();

        var planner = new StubPlanner(
            new ToolCall(
                "CreateDraftSalesOrder",
                new Dictionary<string, object>
                {
                    ["customerId"] = "ACME",
                    ["shipDate"] = "2030-01-01",
                    ["idempotencyKey"] = "idem-orchestrator-001",
                    ["lines"] = new object[]
                    {
                        new Dictionary<string, object> { ["sku"] = "ITEM-123", ["qty"] = 10 }
                    }
                }));
        var erp = new FakeErpConnector();
        var auditStore = new InMemoryAuditStore();
        var orchestrator = new ChatOrchestrator(
            planner,
            BuildHandlers(erp),
            new MapRedactor(),
            auditStore,
            NullLogger<ChatOrchestrator>.Instance);

        var first = await orchestrator.HandleAsync(
            new ChatRequest("Create draft"),
            incomingCorrelationId: "corr-draft-first",
            CancellationToken.None);
        var second = await orchestrator.HandleAsync(
            new ChatRequest("Create draft"),
            incomingCorrelationId: "corr-draft-second",
            CancellationToken.None);

        Assert.Contains("Draft created:", first.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("idempotent replay", second.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, erp.DraftCreateCount);
        Assert.True(auditStore.Contains("corr-draft-first"));
        Assert.True(auditStore.Contains("corr-draft-second"));
    }

    private static void CleanupIdempotencyStore()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), ".idempotency");
        TestDirectoryCleanup.DeleteWithRetries(directory);
    }

    private sealed class StubPlanner(params ToolCall[] calls) : IAiPlanner
    {
        public IReadOnlyList<ToolCall> Plan(string message) => calls;
    }

    private static IReadOnlyList<IChatToolHandler> BuildHandlers(FakeErpConnector erp)
    {
        return new IChatToolHandler[]
        {
            new InventoryToolHandler(erp),
            new DraftSalesOrderToolHandler(erp, new IdempotencyCache()),
            new ExplainOrderExceptionToolHandler(erp, new NoopOrderExceptionSummarizer(), new MapRedactor())
        };
    }
}
