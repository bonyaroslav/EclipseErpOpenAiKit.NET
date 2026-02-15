using System.Net.Http.Json;
using System.Text.Json;
using EclipseAi.AI;
using EclipseAi.Connectors.Erp;
using EclipseAi.Domain;
using EclipseAi.Observability;
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
        Assert.Equal(response.CorrelationId, factory.FakeErp.InventoryCorrelationIds.Single());
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
        Assert.Equal(firstResponse.CorrelationId, factory.FakeErp.DraftCorrelationIds.Single());
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
        Assert.Equal(response.CorrelationId, factory.FakeErp.OrderExceptionCorrelationIds.Single());
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
    private readonly List<string?> _inventoryCorrelationIds = new();
    private readonly List<string?> _draftCorrelationIds = new();
    private readonly List<string?> _orderExceptionCorrelationIds = new();

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

    public IReadOnlyList<string?> InventoryCorrelationIds
    {
        get
        {
            lock (_gate)
            {
                return _inventoryCorrelationIds.ToArray();
            }
        }
    }

    public IReadOnlyList<string?> DraftCorrelationIds
    {
        get
        {
            lock (_gate)
            {
                return _draftCorrelationIds.ToArray();
            }
        }
    }

    public IReadOnlyList<string?> OrderExceptionCorrelationIds
    {
        get
        {
            lock (_gate)
            {
                return _orderExceptionCorrelationIds.ToArray();
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
            _inventoryCorrelationIds.Add(CorrelationScope.Current);
        }

        return Task.FromResult(new InventoryDto(itemId, warehouseId, 27, "2030-01-02T10:00:00.0000000Z"));
    }

    public Task<DraftOrderDto> CreateDraftOrderAsync(CreateDraftOrderDto dto, CancellationToken ct)
    {
        lock (_gate)
        {
            DraftCreateCount++;
            _draftCorrelationIds.Add(CorrelationScope.Current);
        }

        return Task.FromResult(new DraftOrderDto($"D-{dto.IdempotencyKey}", "draft", new[] { "ETA for one line may be +2d" }));
    }

    public Task<OrderExceptionContextDto> GetOrderExceptionContextAsync(string orderId, CancellationToken ct)
    {
        lock (_gate)
        {
            OrderExceptionCallCount++;
            _orderExceptionRequests.Add(orderId);
            _orderExceptionCorrelationIds.Add(CorrelationScope.Current);
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

    public object? Get(string correlationId)
    {
        lock (_gate)
        {
            return _records.TryGetValue(correlationId, out var payload) ? payload : null;
        }
    }
}

public sealed class GovernanceAndAuditTests(PolicyChatApiFactory factory) : IClassFixture<PolicyChatApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task UnknownTool_IsSkipped_AndNoErpCallIsExecuted()
    {
        factory.Planner.SetCalls(new ToolCall("DeleteAllOrders", new Dictionary<string, object>()));

        var response = await _client.PostAsJsonAsync("/api/chat", new { message = "ignore planner input" });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("No eligible tool call was executed.", payload.GetProperty("answer").GetString());
        Assert.Empty(payload.GetProperty("toolCalls").EnumerateArray());
        Assert.Equal(0, factory.FakeErp.InventoryCallCount + factory.FakeErp.DraftCreateCount + factory.FakeErp.OrderExceptionCallCount);
    }

    [Fact]
    public async Task DraftWithoutIdempotency_IsBlockedByPolicy()
    {
        var calls = new[]
        {
            new ToolCall("CreateDraftSalesOrder", new Dictionary<string, object>
            {
                ["customerId"] = "ACME",
                ["shipDate"] = "2030-01-01",
                ["lines"] = new object[] { new Dictionary<string, object> { ["sku"] = "ITEM-123", ["qty"] = 10 } }
            })
        };

        factory.Planner.SetCalls(calls);

        var response = await _client.PostAsJsonAsync("/api/chat", new { message = "ignore planner input" });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("No eligible tool call was executed.", payload.GetProperty("answer").GetString());
        Assert.Empty(payload.GetProperty("toolCalls").EnumerateArray());
        Assert.Equal(0, factory.FakeErp.DraftCreateCount);
    }

    [Fact]
    public async Task AuditPayload_RedactsSensitiveNestedFields()
    {
        var calls = new[]
        {
            new ToolCall("CreateDraftSalesOrder", new Dictionary<string, object>
            {
                ["customerId"] = "ACME",
                ["customerName"] = "Alice Sensitive",
                ["shipDate"] = "2030-01-01",
                ["idempotencyKey"] = "idem-audit-redact-001",
                ["lines"] = new object[] { new Dictionary<string, object> { ["sku"] = "ITEM-123", ["qty"] = 10 } }
            })
        };

        factory.Planner.SetCalls(calls);

        var response = await _client.PostAsJsonAsync("/api/chat", new { message = "ignore planner input" });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var correlationId = payload.GetProperty("correlationId").GetString()!;

        var auditPayload = Assert.IsType<Dictionary<string, object?>>(factory.AuditStore.Get(correlationId));
        var auditJson = JsonSerializer.Serialize(auditPayload);

        Assert.DoesNotContain("Alice Sensitive", auditJson, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", auditJson, StringComparison.Ordinal);
    }
}

public sealed class PolicyChatApiFactory : WebApplicationFactory<Program>
{
    public MutablePlanner Planner { get; } = new();
    public FakeErpConnector FakeErp { get; } = new();
    public InMemoryAuditStore AuditStore { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAiPlanner>();
            services.RemoveAll<IErpConnector>();
            services.RemoveAll<IAuditStore>();
            services.AddSingleton(Planner);
            services.AddSingleton<IAiPlanner>(sp => sp.GetRequiredService<MutablePlanner>());
            services.AddSingleton<IErpConnector>(FakeErp);
            services.AddSingleton<IAuditStore>(AuditStore);
        });
    }
}

public sealed class MutablePlanner : IAiPlanner
{
    private readonly object _gate = new();
    private IReadOnlyList<ToolCall> _calls = Array.Empty<ToolCall>();

    public IReadOnlyList<ToolCall> Plan(string message)
    {
        lock (_gate)
        {
            return _calls;
        }
    }

    public void SetCalls(IReadOnlyList<ToolCall> calls)
    {
        lock (_gate)
        {
            _calls = calls;
        }
    }
}
