using System.Net.Http.Json;
using System.Text.Json;
using EclipseAi.Connectors.Erp;
using Gateway.Functions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EclipseAi.Tests.Integration;

public sealed class ChatScenariosTests(ChatApiFactory factory) : IClassFixture<ChatApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task InventoryAvailabilityScenario_ReturnsContractAndEvidence()
    {
        var response = await PostChatAsync("Do we have ITEM-123 in warehouse MAD? What's available and ETA?");

        AssertStableContract(response);
        Assert.Equal("GetInventoryAvailability", response.ToolCalls.Single().GetProperty("name").GetString());
        AssertEvidenceContains(response.Evidence, "itemId", "warehouseId", "availableQty", "etaUtc");
        Assert.True(factory.AuditStore.Contains(response.CorrelationId));

        Assert.Equal(1, factory.FakeErp.InventoryCallCount);
        Assert.Equal(("ITEM-123", "MAD"), factory.FakeErp.InventoryRequests.Single());
    }

    [Fact]
    public async Task DraftSalesOrderScenario_IsIdempotentForSamePlannerKey()
    {
        var firstResponse = await PostChatAsync("Create a draft order for ACME: 10x ITEM-123, ship tomorrow.");
        var secondResponse = await PostChatAsync("Create a draft order for ACME: 10x ITEM-123, ship tomorrow.");

        AssertStableContract(firstResponse);
        AssertStableContract(secondResponse);
        Assert.Equal("CreateDraftSalesOrder", firstResponse.ToolCalls.Single().GetProperty("name").GetString());
        Assert.Equal("CreateDraftSalesOrder", secondResponse.ToolCalls.Single().GetProperty("name").GetString());
        Assert.Contains("Draft created:", firstResponse.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("idempotent replay", secondResponse.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, factory.FakeErp.DraftCreateCount);
        Assert.True(factory.AuditStore.Contains(firstResponse.CorrelationId));
        Assert.True(factory.AuditStore.Contains(secondResponse.CorrelationId));
    }

    [Fact]
    public async Task OrderExceptionScenario_UsesAllowlistedEvidenceOnly()
    {
        var response = await PostChatAsync("Why is SO-456 delayed and what should I do?");

        AssertStableContract(response);
        Assert.Equal("ExplainOrderException", response.ToolCalls.Single().GetProperty("name").GetString());
        Assert.Contains("SO-456", response.Answer, StringComparison.OrdinalIgnoreCase);
        AssertEvidenceContains(response.Evidence, "holds", "backorderedSkus", "arOverdueDays", "warehouse");
        Assert.DoesNotContain(response.Evidence, x => x.GetProperty("path").GetString() == "customerName");
        Assert.True(factory.AuditStore.Contains(response.CorrelationId));

        Assert.Equal(1, factory.FakeErp.OrderExceptionCallCount);
        Assert.Equal("SO-456", factory.FakeErp.OrderExceptionRequests.Single());
    }

    private async Task<ChatApiResponse> PostChatAsync(string message)
    {
        var httpResponse = await _client.PostAsJsonAsync("/api/chat", new { message });
        httpResponse.EnsureSuccessStatusCode();

        var payload = await httpResponse.Content.ReadFromJsonAsync<JsonElement>();
        var toolCalls = payload.GetProperty("toolCalls").EnumerateArray().ToList();
        var evidence = payload.GetProperty("evidence").EnumerateArray().ToList();

        return new ChatApiResponse(
            payload.GetProperty("correlationId").GetString()!,
            payload.GetProperty("answer").GetString()!,
            toolCalls,
            evidence,
            payload.GetProperty("auditRef").GetString()!);
    }

    private static void AssertStableContract(ChatApiResponse response)
    {
        Assert.False(string.IsNullOrWhiteSpace(response.CorrelationId));
        Assert.False(string.IsNullOrWhiteSpace(response.Answer));
        Assert.NotEmpty(response.ToolCalls);
        Assert.NotEmpty(response.Evidence);
        Assert.Equal($".audit/{response.CorrelationId}.json", response.AuditRef);
    }

    private static void AssertEvidenceContains(IReadOnlyList<JsonElement> evidence, params string[] expectedPaths)
    {
        var actualPaths = evidence
            .Select(static x => x.GetProperty("path").GetString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var expectedPath in expectedPaths)
        {
            Assert.Contains(expectedPath, actualPaths);
        }
    }

    private sealed record ChatApiResponse(
        string CorrelationId,
        string Answer,
        IReadOnlyList<JsonElement> ToolCalls,
        IReadOnlyList<JsonElement> Evidence,
        string AuditRef);
}

public sealed class ChatApiFactory : WebApplicationFactory<Program>
{
    public FakeErpConnector FakeErp { get; } = new();
    public InMemoryAuditStore AuditStore { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IErpConnector>();
            services.RemoveAll<IAuditStore>();
            services.AddSingleton<IErpConnector>(FakeErp);
            services.AddSingleton<IAuditStore>(AuditStore);
        });
    }
}

public sealed class FakeErpConnector : IErpConnector
{
    private readonly object _gate = new();
    private readonly List<(string ItemId, string WarehouseId)> _inventoryRequests = new();
    private readonly List<string> _orderExceptionRequests = new();

    public int InventoryCallCount { get; private set; }
    public int DraftCreateCount { get; private set; }
    public int OrderExceptionCallCount { get; private set; }

    public IReadOnlyList<(string ItemId, string WarehouseId)> InventoryRequests
    {
        get
        {
            lock (_gate)
            {
                return _inventoryRequests.ToArray();
            }
        }
    }

    public IReadOnlyList<string> OrderExceptionRequests
    {
        get
        {
            lock (_gate)
            {
                return _orderExceptionRequests.ToArray();
            }
        }
    }

    public Task<InventoryDto> GetInventoryAsync(string itemId, string warehouseId, CancellationToken ct)
    {
        lock (_gate)
        {
            InventoryCallCount++;
            _inventoryRequests.Add((itemId, warehouseId));
        }

        return Task.FromResult(new InventoryDto(itemId, warehouseId, 27, "2030-01-02T10:00:00.0000000Z"));
    }

    public Task<DraftOrderDto> CreateDraftOrderAsync(CreateDraftOrderDto dto, CancellationToken ct)
    {
        lock (_gate)
        {
            DraftCreateCount++;
        }

        return Task.FromResult(new DraftOrderDto($"D-{dto.IdempotencyKey}", "draft", new[] { "ETA for one line may be +2d" }));
    }

    public Task<OrderExceptionContextDto> GetOrderExceptionContextAsync(string orderId, CancellationToken ct)
    {
        lock (_gate)
        {
            OrderExceptionCallCount++;
            _orderExceptionRequests.Add(orderId);
        }

        var data = new Dictionary<string, object>
        {
            ["holds"] = new[] { "CREDIT_HOLD" },
            ["backorderedSkus"] = new[] { "ITEM-123" },
            ["arOverdueDays"] = 14,
            ["warehouse"] = "MAD",
            ["customerName"] = "Alice"
        };

        return Task.FromResult(new OrderExceptionContextDto(orderId, "BACKORDER_HOLD_AR", data));
    }
}

public sealed class InMemoryAuditStore : IAuditStore
{
    private readonly Dictionary<string, object> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public Task WriteAsync(string correlationId, object payload, CancellationToken ct)
    {
        lock (_gate)
        {
            _records[correlationId] = payload;
        }

        return Task.CompletedTask;
    }

    public bool Contains(string correlationId)
    {
        lock (_gate)
        {
            return _records.ContainsKey(correlationId);
        }
    }
}
