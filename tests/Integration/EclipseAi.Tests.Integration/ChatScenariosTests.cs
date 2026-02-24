using System.Net.Http.Json;
using System.Text.Json;
using EclipseAi.AI;
using EclipseAi.Connectors.Erp;
using EclipseAi.Domain;
using EclipseAi.Observability;
using Gateway.Functions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
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
        CleanupIdempotencyStore(factory.IdempotencyDirectory);
        var firstResponse = await PostChatAsync("Create a draft order for ACME: 10x ITEM-123, ship tomorrow.");
        var secondResponse = await PostChatAsync("Create a draft order for ACME: 10x ITEM-123, ship tomorrow.");

        AssertStableContract(firstResponse);
        AssertStableContract(secondResponse);
        Assert.Equal("CreateDraftSalesOrder", firstResponse.ToolCalls.Single().GetProperty("name").GetString());
        Assert.Equal("CreateDraftSalesOrder", secondResponse.ToolCalls.Single().GetProperty("name").GetString());
        Assert.Contains("I created draft sales order", firstResponse.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reused it to avoid creating a duplicate order", secondResponse.Answer, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public async Task IncomingCorrelationId_IsPropagatedToResponseAndErp()
    {
        const string inboundCorrelationId = "corr-from-client-123";
        var response = await PostChatAsync(
            "Do we have ITEM-123 in warehouse MAD? What's available and ETA?",
            inboundCorrelationId);

        Assert.Equal(inboundCorrelationId, response.CorrelationId);
        Assert.Equal(inboundCorrelationId, factory.FakeErp.InventoryCorrelationIds.Last());
        Assert.True(factory.AuditStore.Contains(inboundCorrelationId));
    }

    private async Task<ChatApiResponse> PostChatAsync(string message, string? correlationId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(new { message })
        };

        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            request.Headers.TryAddWithoutValidation("x-correlation-id", correlationId);
        }

        var httpResponse = await _client.SendAsync(request);
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

    private static void CleanupIdempotencyStore(string? idempotencyDirectory = null)
    {
        var root = Directory.GetCurrentDirectory();
        if (!string.IsNullOrWhiteSpace(idempotencyDirectory))
        {
            TestDirectoryCleanup.DeleteWithRetries(idempotencyDirectory);
        }
        TestDirectoryCleanup.DeleteWithRetries(Path.Combine(root, ".idempotency"));
        TestDirectoryCleanup.DeleteWithRetries(Path.Combine(root, "apps", "Gateway.Functions", ".idempotency"));
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
    private readonly string _idempotencyDirectory =
        Path.Combine(Path.GetTempPath(), $"eclipse-idem-{Guid.NewGuid():N}");

    public FakeErpConnector FakeErp { get; } = new();
    public InMemoryAuditStore AuditStore { get; } = new();
    public string IdempotencyDirectory => _idempotencyDirectory;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IDEMPOTENCY_DIR"] = _idempotencyDirectory
            });
        });
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

        return Task.FromResult(new DraftOrderDto(
            $"D-{dto.IdempotencyKey}",
            $"EXT-{dto.IdempotencyKey}",
            "DRAFT"));
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

    public void Reset()
    {
        lock (_gate)
        {
            InventoryCallCount = 0;
            DraftCreateCount = 0;
            OrderExceptionCallCount = 0;
            _inventoryRequests.Clear();
            _orderExceptionRequests.Clear();
            _inventoryCorrelationIds.Clear();
            _draftCorrelationIds.Clear();
            _orderExceptionCorrelationIds.Clear();
        }
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
        factory.FakeErp.Reset();
        factory.Planner.SetCalls(new[] { new ToolCall("DeleteAllOrders", new Dictionary<string, object>()) });

        var response = await _client.PostAsJsonAsync("/api/chat", new { message = "ignore planner input" });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(
            "I couldn't safely execute an eligible action for that request. Please rephrase, and I can try again.",
            payload.GetProperty("answer").GetString());
        Assert.Empty(payload.GetProperty("toolCalls").EnumerateArray());
        Assert.Equal(0, factory.FakeErp.InventoryCallCount + factory.FakeErp.DraftCreateCount + factory.FakeErp.OrderExceptionCallCount);
    }

    [Fact]
    public async Task DraftWithoutIdempotency_IsBlockedByPolicy()
    {
        CleanupIdempotencyStore(factory.IdempotencyDirectory);
        factory.FakeErp.Reset();
        var calls = new[]
        {
            new ToolCall("CreateDraftSalesOrder", new Dictionary<string, object>
            {
                ["customerId"] = "ACME",
                ["requestedDate"] = "2030-01-01",
                ["lines"] = new object[] { new Dictionary<string, object> { ["item"] = "ITEM-123", ["qty"] = 10, ["unitPrice"] = 12.34m } }
            })
        };

        factory.Planner.SetCalls(calls);

        var response = await _client.PostAsJsonAsync("/api/chat", new { message = "ignore planner input" });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(
            "I couldn't safely execute an eligible action for that request. Please rephrase, and I can try again.",
            payload.GetProperty("answer").GetString());
        Assert.Empty(payload.GetProperty("toolCalls").EnumerateArray());
        Assert.Equal(0, factory.FakeErp.DraftCreateCount);
    }

    [Fact]
    public async Task InvalidInventoryArgs_AreRejected()
    {
        factory.FakeErp.Reset();
        factory.Planner.SetCalls(new[]
        {
            new ToolCall("GetInventoryAvailability", new Dictionary<string, object> { ["warehouseId"] = "MAD" })
        });

        var response = await _client.PostAsJsonAsync("/api/chat", new { message = "ignore planner input" });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Contains("couldn't execute GetInventoryAvailability", payload.GetProperty("answer").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(payload.GetProperty("toolCalls").EnumerateArray());
        Assert.Equal(0, factory.FakeErp.InventoryCallCount);
    }

    [Fact]
    public async Task InvalidDraftArgs_AreRejected()
    {
        CleanupIdempotencyStore(factory.IdempotencyDirectory);
        factory.FakeErp.Reset();
        factory.Planner.SetCalls(new[]
        {
            new ToolCall("CreateDraftSalesOrder", new Dictionary<string, object>
            {
                ["customerId"] = "ACME",
                ["requestedDate"] = "2030-01-01",
                ["idempotencyKey"] = "idem-invalid-lines",
                ["lines"] = Array.Empty<object>()
            })
        });

        var response = await _client.PostAsJsonAsync("/api/chat", new { message = "ignore planner input" });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Contains("couldn't execute CreateDraftSalesOrder", payload.GetProperty("answer").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(payload.GetProperty("toolCalls").EnumerateArray());
        Assert.Equal(0, factory.FakeErp.DraftCreateCount);
    }

    [Fact]
    public async Task InvalidOrderExceptionArgs_AreRejected()
    {
        factory.FakeErp.Reset();
        factory.Planner.SetCalls(new[]
        {
            new ToolCall("ExplainOrderException", new Dictionary<string, object>())
        });

        var response = await _client.PostAsJsonAsync("/api/chat", new { message = "ignore planner input" });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Contains("couldn't execute ExplainOrderException", payload.GetProperty("answer").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(payload.GetProperty("toolCalls").EnumerateArray());
        Assert.Equal(0, factory.FakeErp.OrderExceptionCallCount);
    }

    [Fact]
    public async Task DraftWithSameIdempotencyKeyDifferentPayload_IsBlocked()
    {
        CleanupIdempotencyStore(factory.IdempotencyDirectory);
        factory.FakeErp.Reset();
        var idempotencyKey = $"idem-conflict-{Guid.NewGuid():N}";
        var firstCall = new ToolCall("CreateDraftSalesOrder", new Dictionary<string, object>
        {
            ["customerId"] = "ACME",
            ["requestedDate"] = "2030-01-01",
            ["idempotencyKey"] = idempotencyKey,
            ["lines"] = new object[] { new Dictionary<string, object> { ["item"] = "ITEM-123", ["qty"] = 10, ["unitPrice"] = 12.34m } }
        });
        var secondCall = new ToolCall("CreateDraftSalesOrder", new Dictionary<string, object>
        {
            ["customerId"] = "ACME",
            ["requestedDate"] = "2030-01-01",
            ["idempotencyKey"] = idempotencyKey,
            ["lines"] = new object[] { new Dictionary<string, object> { ["item"] = "ITEM-123", ["qty"] = 11, ["unitPrice"] = 12.34m } }
        });

        factory.Planner.SetCalls([firstCall]);
        var firstResponse = await _client.PostAsJsonAsync("/api/chat", new { message = "ignore planner input" });
        firstResponse.EnsureSuccessStatusCode();

        factory.Planner.SetCalls([secondCall]);
        var secondResponse = await _client.PostAsJsonAsync("/api/chat", new { message = "ignore planner input" });
        secondResponse.EnsureSuccessStatusCode();

        var secondPayload = await secondResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("was reused with a different payload", secondPayload.GetProperty("answer").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, factory.FakeErp.DraftCreateCount);
    }

    [Fact]
    public async Task AuditPayload_RedactsSensitiveNestedFields()
    {
        CleanupIdempotencyStore(factory.IdempotencyDirectory);
        factory.FakeErp.Reset();
        var calls = new[]
        {
            new ToolCall("CreateDraftSalesOrder", new Dictionary<string, object>
            {
                ["customerId"] = "ACME",
                ["customerName"] = "Alice Sensitive",
                ["requestedDate"] = "2030-01-01",
                ["idempotencyKey"] = "idem-audit-redact-001",
                ["lines"] = new object[] { new Dictionary<string, object> { ["item"] = "ITEM-123", ["qty"] = 10, ["unitPrice"] = 12.34m } }
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

    private static void CleanupIdempotencyStore(string? idempotencyDirectory = null)
    {
        var root = Directory.GetCurrentDirectory();
        if (!string.IsNullOrWhiteSpace(idempotencyDirectory))
        {
            TestDirectoryCleanup.DeleteWithRetries(idempotencyDirectory);
        }
        TestDirectoryCleanup.DeleteWithRetries(Path.Combine(root, ".idempotency"));
        TestDirectoryCleanup.DeleteWithRetries(Path.Combine(root, "apps", "Gateway.Functions", ".idempotency"));
    }
}

internal static class TestDirectoryCleanup
{
    public static void DeleteWithRetries(string directory, int maxAttempts = 5, int delayMs = 50)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(delayMs);
            }
        }

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public sealed class PolicyChatApiFactory : WebApplicationFactory<Program>
{
    private readonly string _idempotencyDirectory =
        Path.Combine(Path.GetTempPath(), $"eclipse-idem-{Guid.NewGuid():N}");

    public MutablePlanner Planner { get; } = new();
    public FakeErpConnector FakeErp { get; } = new();
    public InMemoryAuditStore AuditStore { get; } = new();
    public string IdempotencyDirectory => _idempotencyDirectory;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IDEMPOTENCY_DIR"] = _idempotencyDirectory
            });
        });
        builder.ConfigureTestServices(services =>
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

public sealed class OpenAiModeOffBehaviorTests(OpenAiOffFactory factory) : IClassFixture<OpenAiOffFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task OpenAiModeOff_UsesDeterministicPlannerFlow()
    {
        var response = await _client.PostAsJsonAsync("/api/chat", new { message = "Do we have ITEM-123 in warehouse MAD?" });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("GetInventoryAvailability", payload.GetProperty("toolCalls")[0].GetProperty("name").GetString());
        Assert.Contains("ITEM-123", payload.GetProperty("answer").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, factory.FakeErp.InventoryCallCount);
    }
}

public sealed class OpenAiSummaryBehaviorTests(OpenAiSummaryFactory factory) : IClassFixture<OpenAiSummaryFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task OpenAiSummarizeEnabled_UsesInjectedDeterministicSummary()
    {
        var response = await _client.PostAsJsonAsync("/api/chat", new { message = "Why is SO-456 delayed and what should I do?" });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Contains("AI summary deterministic", payload.GetProperty("answer").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, factory.FakeErp.OrderExceptionCallCount);
        Assert.Equal("SO-456", factory.FakeErp.OrderExceptionRequests.Single());
        AssertGovernedSummaryData(factory.OpenAiClient.LastSummarizeData);
    }

    private static void AssertGovernedSummaryData(IReadOnlyDictionary<string, object>? data)
    {
        Assert.NotNull(data);
        Assert.Contains("holds", data.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("backorderedSkus", data.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("arOverdueDays", data.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("warehouse", data.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("customerName", data.Keys, StringComparer.OrdinalIgnoreCase);
    }
}

public sealed class OpenAiOffFactory : WebApplicationFactory<Program>
{
    private readonly string _idempotencyDirectory =
        Path.Combine(Path.GetTempPath(), $"eclipse-idem-{Guid.NewGuid():N}");

    public FakeErpConnector FakeErp { get; } = new();
    public InMemoryAuditStore AuditStore { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OPENAI_API_KEY"] = "demo-key",
                ["OPENAI_MODE"] = "off",
                ["OPENAI_SUMMARIZE"] = "1",
                ["IDEMPOTENCY_DIR"] = _idempotencyDirectory
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IErpConnector>();
            services.RemoveAll<IAuditStore>();
            services.AddSingleton<IErpConnector>(FakeErp);
            services.AddSingleton<IAuditStore>(AuditStore);
        });
    }
}

public sealed class OpenAiSummaryFactory : WebApplicationFactory<Program>
{
    private readonly string _idempotencyDirectory =
        Path.Combine(Path.GetTempPath(), $"eclipse-idem-{Guid.NewGuid():N}");

    public FakeErpConnector FakeErp { get; } = new();
    public InMemoryAuditStore AuditStore { get; } = new();
    public DeterministicOpenAiClient OpenAiClient { get; } = new(
        [new ToolCall("ExplainOrderException", new Dictionary<string, object> { ["orderId"] = "SO-456" })],
        "AI summary deterministic");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OPENAI_API_KEY"] = "demo-key",
                ["OPENAI_MODE"] = "real",
                ["OPENAI_SUMMARIZE"] = "1",
                ["IDEMPOTENCY_DIR"] = _idempotencyDirectory
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IErpConnector>();
            services.RemoveAll<IAuditStore>();
            services.RemoveAll<IOpenAiClient>();
            services.AddSingleton<IErpConnector>(FakeErp);
            services.AddSingleton<IAuditStore>(AuditStore);
            services.AddSingleton<IOpenAiClient>(OpenAiClient);
        });
    }
}

public sealed class DeterministicOpenAiClient(IReadOnlyList<ToolCall> calls, string summary) : IOpenAiClient
{
    private readonly object _gate = new();
    private IReadOnlyDictionary<string, object>? _lastSummarizeData;

    public IReadOnlyDictionary<string, object>? LastSummarizeData
    {
        get
        {
            lock (_gate)
            {
                return _lastSummarizeData;
            }
        }
    }

    public Task<IReadOnlyList<ToolCall>> PlanToolsAsync(string message, OpenAiPlannerSettings settings, CancellationToken ct)
    {
        return Task.FromResult(calls);
    }

    public Task<string?> SummarizeOrderExceptionAsync(
        string orderId,
        string summaryCode,
        IReadOnlyDictionary<string, object> data,
        OpenAiPlannerSettings settings,
        CancellationToken ct)
    {
        lock (_gate)
        {
            _lastSummarizeData = new Dictionary<string, object>(data, StringComparer.OrdinalIgnoreCase);
        }

        return Task.FromResult<string?>(summary);
    }
}

public sealed class InforChatScenariosTests(InforChatApiFactory factory) : IClassFixture<InforChatApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task DraftSalesOrderScenario_Infor_IsIdempotent()
    {
        CleanupIdempotencyStore(factory.IdempotencyDirectory);
        factory.InforServer.Reset();

        var first = await PostChatAsync("Create a draft order for ACME: 10x ITEM-123, ship tomorrow.");
        var second = await PostChatAsync("Create a draft order for ACME: 10x ITEM-123, ship tomorrow.");

        AssertStableContract(first);
        AssertStableContract(second);
        Assert.Equal(1, factory.InforServer.DraftCallCount);
        Assert.Equal(first.CorrelationId, factory.InforServer.DraftCorrelationIds.Single());
        Assert.True(factory.AuditStore.Contains(first.CorrelationId));
        Assert.True(factory.AuditStore.Contains(second.CorrelationId));
    }

    [Fact]
    public async Task OrderExceptionScenario_Infor_UsesAllowlistedEvidenceOnly()
    {
        factory.InforServer.Reset();
        var response = await PostChatAsync("Why is SO-456 delayed and what should I do?");

        AssertStableContract(response);
        Assert.Equal("ExplainOrderException", response.ToolCalls.Single().GetProperty("name").GetString());
        AssertEvidenceContains(response.Evidence, "holds", "backorderedSkus", "arOverdueDays", "warehouse");
        Assert.DoesNotContain(response.Evidence, x => x.GetProperty("path").GetString() == "customerName");
        Assert.Equal(1, factory.InforServer.ExceptionCallCount);
        Assert.Equal(response.CorrelationId, factory.InforServer.ExceptionCorrelationIds.Single());
    }

    private async Task<ChatApiResponse> PostChatAsync(string message, string? correlationId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(new { message })
        };

        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            request.Headers.TryAddWithoutValidation("x-correlation-id", correlationId);
        }

        var httpResponse = await _client.SendAsync(request);
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

    private static void CleanupIdempotencyStore(string? idempotencyDirectory = null)
    {
        var root = Directory.GetCurrentDirectory();
        if (!string.IsNullOrWhiteSpace(idempotencyDirectory))
        {
            TestDirectoryCleanup.DeleteWithRetries(idempotencyDirectory);
        }
        TestDirectoryCleanup.DeleteWithRetries(Path.Combine(root, ".idempotency"));
        TestDirectoryCleanup.DeleteWithRetries(Path.Combine(root, "apps", "Gateway.Functions", ".idempotency"));
    }

    private sealed record ChatApiResponse(
        string CorrelationId,
        string Answer,
        IReadOnlyList<JsonElement> ToolCalls,
        IReadOnlyList<JsonElement> Evidence,
        string AuditRef);
}

public sealed class InforChatApiFactory : WebApplicationFactory<Program>
{
    private readonly string _idempotencyDirectory =
        Path.Combine(Path.GetTempPath(), $"eclipse-idem-{Guid.NewGuid():N}");

    public FakeInforServer InforServer { get; } = new();
    public InMemoryAuditStore AuditStore { get; } = new();
    public string IdempotencyDirectory => _idempotencyDirectory;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IDEMPOTENCY_DIR"] = _idempotencyDirectory
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IErpConnector>();
            services.RemoveAll<IAuditStore>();
            services.RemoveAll<IInforTokenClient>();
            services.RemoveAll<InforApiClient>();

            services.AddSingleton<IAuditStore>(AuditStore);
            services.AddSingleton<IInforTokenClient>(_ =>
                new InforTokenClient(
                    InforServer.CreateClient(),
                    new InforTokenClientSettings("client-id", "client-secret", null, "/oauth/token")));
            services.AddSingleton(sp => new InforApiClient(
                InforServer.CreateClient(),
                sp.GetRequiredService<IInforTokenClient>()));
            services.AddSingleton<IErpConnector, InforErpConnector>();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            InforServer.Dispose();
        }
    }
}

public sealed class FakeInforServer : IDisposable
{
    private readonly object _gate = new();
    private readonly WebApplication _app;
    private readonly TestServer _server;
    private readonly List<string?> _draftCorrelationIds = new();
    private readonly List<string?> _exceptionCorrelationIds = new();

    public int TokenCallCount { get; private set; }
    public int DraftCallCount { get; private set; }
    public int ExceptionCallCount { get; private set; }

    public FakeInforServer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _app = builder.Build();

        _app.MapPost("/oauth/token", () =>
        {
            lock (_gate)
            {
                TokenCallCount++;
            }

            return Results.Ok(new { access_token = "token-123", expires_in = 3600 });
        });

        _app.MapPost("/orders/draft", async (HttpRequest request) =>
        {
            var payload = await request.ReadFromJsonAsync<Dictionary<string, object>>() ?? new();
            var key = payload.TryGetValue("idempotencyKey", out var raw) ? raw?.ToString() : "missing";
            var correlationId = request.Headers["x-correlation-id"].FirstOrDefault();

            lock (_gate)
            {
                DraftCallCount++;
                _draftCorrelationIds.Add(correlationId);
            }

            return Results.Ok(new
            {
                draftId = $"D-{key}",
                externalOrderNumber = $"EXT-{key}",
                status = "DRAFT"
            });
        });

        _app.MapGet("/orders/{orderId}/exception-context", (string orderId, HttpRequest request) =>
        {
            var correlationId = request.Headers["x-correlation-id"].FirstOrDefault();
            lock (_gate)
            {
                ExceptionCallCount++;
                _exceptionCorrelationIds.Add(correlationId);
            }

            var data = new Dictionary<string, object>
            {
                ["holds"] = new[] { "CREDIT_HOLD" },
                ["backorderedSkus"] = new[] { "ITEM-123" },
                ["arOverdueDays"] = 14,
                ["warehouse"] = "MAD",
                ["customerName"] = "Alice"
            };

            return Results.Ok(new { orderId, summaryCode = "BACKORDER_HOLD_AR", data });
        });

        _app.StartAsync().GetAwaiter().GetResult();
        _server = _app.GetTestServer();
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

    public IReadOnlyList<string?> ExceptionCorrelationIds
    {
        get
        {
            lock (_gate)
            {
                return _exceptionCorrelationIds.ToArray();
            }
        }
    }

    public HttpClient CreateClient()
    {
        var client = _server.CreateClient();
        client.BaseAddress = new Uri("http://localhost");
        return client;
    }

    public void Reset()
    {
        lock (_gate)
        {
            TokenCallCount = 0;
            DraftCallCount = 0;
            ExceptionCallCount = 0;
            _draftCorrelationIds.Clear();
            _exceptionCorrelationIds.Clear();
        }
    }

    public void Dispose()
    {
        _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
