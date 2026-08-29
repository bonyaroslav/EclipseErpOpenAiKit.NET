using System.Net;
using System.Text;
using System.Text.Json;
using EclipseAi.AI;
using EclipseAi.Connectors.Erp;
using EclipseAi.Domain;
using EclipseAi.Governance;
using EclipseAi.Observability;

namespace EclipseAi.Tests.Unit;

public class PlannerTests
{
    [Fact]
    public void PlannerFactory_WithoutApiKey_UsesFakePlanner()
    {
        var planner = PlannerFactory.Create(openAiApiKey: null, openAiMode: "real");

        Assert.IsType<FakePlanner>(planner);
    }

    [Fact]
    public async Task PlannerFactory_WithApiKey_EmulatedMode_UsesOpenAiPlanner()
    {
        var planner = PlannerFactory.Create(openAiApiKey: "demo-key", openAiMode: "emulated");

        Assert.IsType<OpenAiPlanner>(planner);
        var call = Assert.Single(await planner.PlanAsync("Do we have ITEM-123 in warehouse MAD?", CancellationToken.None));
        Assert.Equal("GetInventoryAvailability", call.Name);
    }

    [Fact]
    public void PlannerFactory_WithApiKey_OffMode_UsesFakePlanner()
    {
        var planner = PlannerFactory.Create(openAiApiKey: "demo-key", openAiMode: "off");

        Assert.IsType<FakePlanner>(planner);
    }

    [Fact]
    public async Task OpenAiPlanner_RealMode_UsesOpenAiClientToolCalls()
    {
        var client = new StubOpenAiClient(
            [new ToolCall("GetInventoryAvailability", new Dictionary<string, object> { ["itemId"] = "ITEM-777", ["warehouseId"] = "DAL" })]);
        var planner = PlannerFactory.Create(
            openAiApiKey: "demo-key",
            openAiMode: "real",
            openAiClient: client,
            fallbackPlanner: new FakePlanner());

        var call = Assert.Single(await planner.PlanAsync("any", CancellationToken.None));
        Assert.Equal("GetInventoryAvailability", call.Name);
        Assert.Equal("ITEM-777", call.Args["itemId"]);
        Assert.Equal("DAL", call.Args["warehouseId"]);
    }

    [Fact]
    public async Task OpenAiPlanner_RealMode_FallsBackWhenClientFails()
    {
        var planner = PlannerFactory.Create(
            openAiApiKey: "demo-key",
            openAiMode: "real",
            openAiClient: new ThrowingOpenAiClient(),
            fallbackPlanner: new FakePlanner());

        var call = Assert.Single(await planner.PlanAsync("Do we have ITEM-123 in warehouse MAD?", CancellationToken.None));
        Assert.Equal("GetInventoryAvailability", call.Name);
    }

    [Fact]
    public async Task OpenAiPlanner_RealMode_PropagatesCallerCancellationWithoutFallback()
    {
        var fallback = new CountingPlanner();
        var client = new CancellationAwareOpenAiClient();
        var planner = PlannerFactory.Create(
            openAiApiKey: "demo-key",
            openAiMode: "real",
            openAiClient: client,
            fallbackPlanner: fallback);
        using var cts = new CancellationTokenSource();

        var planning = planner.PlanAsync("any", cts.Token);
        await client.PlanStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => planning);

        Assert.Equal(0, fallback.CallCount);
    }

    [Fact]
    public void SummarizerFactory_WithoutKey_ReturnsNoop()
    {
        var summarizer = PlannerFactory.CreateSummarizer(openAiApiKey: null, openAiMode: "real", enableSummarization: true);

        Assert.IsType<NoopOrderExceptionSummarizer>(summarizer);
    }

    [Fact]
    public void SummarizerFactory_OffMode_ReturnsNoop()
    {
        var summarizer = PlannerFactory.CreateSummarizer(openAiApiKey: "demo-key", openAiMode: "off", enableSummarization: true);

        Assert.IsType<NoopOrderExceptionSummarizer>(summarizer);
    }

    [Fact]
    public async Task SummarizerFactory_EmulatedMode_ReturnsDeterministicSummarizer()
    {
        var summarizer = PlannerFactory.CreateSummarizer(openAiApiKey: "demo-key", openAiMode: "emulated", enableSummarization: true);

        Assert.IsType<DeterministicOrderExceptionSummarizer>(summarizer);
        Assert.Equal(
            "Order SO-456 delayed (BACKORDER).",
            await summarizer.SummarizeAsync("SO-456", "BACKORDER", new Dictionary<string, object>(), CancellationToken.None));
    }

    [Fact]
    public async Task Summarizer_RealMode_UsesOpenAiClientAndFallsBackOnError()
    {
        var summarizer = PlannerFactory.CreateSummarizer(
            openAiApiKey: "demo-key",
            openAiMode: "real",
            enableSummarization: true,
            openAiClient: new StubOpenAiClient([], "AI summary"));

        Assert.Equal(
            "AI summary",
            await summarizer.SummarizeAsync("SO-456", "BACKORDER", new Dictionary<string, object>(), CancellationToken.None));

        var fallbackSummarizer = PlannerFactory.CreateSummarizer(
            openAiApiKey: "demo-key",
            openAiMode: "real",
            enableSummarization: true,
            openAiClient: new ThrowingOpenAiClient());

        Assert.Equal(
            "Order SO-456 delayed (BACKORDER).",
            await fallbackSummarizer.SummarizeAsync("SO-456", "BACKORDER", new Dictionary<string, object>(), CancellationToken.None));
    }

    [Fact]
    public async Task Summarizer_RealMode_PropagatesCallerCancellationWithoutFallback()
    {
        var client = new CancellationAwareOpenAiClient();
        var summarizer = PlannerFactory.CreateSummarizer(
            openAiApiKey: "demo-key",
            openAiMode: "real",
            enableSummarization: true,
            openAiClient: client);
        using var cts = new CancellationTokenSource();

        var summarizing = summarizer.SummarizeAsync(
            "SO-456",
            "BACKORDER",
            new Dictionary<string, object>(),
            cts.Token);
        await client.SummaryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => summarizing);
    }

    [Fact]
    public async Task Summarizer_RealMode_CancellationWinsOverNonCancellationFailure()
    {
        var summarizer = PlannerFactory.CreateSummarizer(
            openAiApiKey: "demo-key",
            openAiMode: "real",
            enableSummarization: true,
            openAiClient: new ThrowingOpenAiClient());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => summarizer.SummarizeAsync(
                "SO-456",
                "BACKORDER",
                new Dictionary<string, object>(),
                cts.Token));
    }

    [Fact]
    public async Task Plan_InventoryMessage_UsesInventoryTool()
    {
        var planner = new FakePlanner();

        var calls = await planner.PlanAsync("Do we have ITEM-123 in warehouse MAD?", CancellationToken.None);

        var call = Assert.Single(calls);
        Assert.Equal("GetInventoryAvailability", call.Name);
        Assert.Equal("ITEM-123", call.Args["itemId"]);
        Assert.Equal("MAD", call.Args["warehouseId"]);
    }

    [Fact]
    public async Task Plan_DraftMessage_UsesDeterministicRequestedDate()
    {
        var planner = new FakePlanner();

        var calls = await planner.PlanAsync("Create a draft order for ACME: 10x ITEM-123", CancellationToken.None);

        var call = Assert.Single(calls);
        Assert.Equal("CreateDraftSalesOrder", call.Name);
        Assert.Equal("2030-01-01", call.Args["requestedDate"]);
        Assert.Equal("demo-key-001", call.Args["idempotencyKey"]);
        var lines = Assert.IsType<object[]>(call.Args["lines"]);
        var line = Assert.IsType<Dictionary<string, object>>(lines.Single());
        Assert.Equal("ITEM-123", line["item"]);
        Assert.Equal(10, line["qty"]);
        Assert.Equal(42.5m, line["unitPrice"]);
    }

    [Fact]
    public async Task Plan_OrderExceptionMessage_UsesExceptionTool()
    {
        var planner = new FakePlanner();

        var calls = await planner.PlanAsync("Why is SO-456 delayed?", CancellationToken.None);

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

public class CorrelationTests
{
    [Fact]
    public void Correlation_FromHeaderOrNew_UsesIncomingHeaderWhenPresent()
    {
        var correlationId = Correlation.FromHeaderOrNew(" corr-client-1 ");

        Assert.Equal("corr-client-1", correlationId);
    }

    [Fact]
    public void Correlation_FromHeaderOrNew_InvalidHeader_GeneratesNewId()
    {
        var correlationId = Correlation.FromHeaderOrNew("../evil");

        Assert.Matches("^[a-f0-9]{32}$", correlationId);
    }

    [Fact]
    public void CorrelationScope_PushesAndRestoresCurrentId()
    {
        Assert.Null(CorrelationScope.Current);

        using (CorrelationScope.Push("corr-1"))
        {
            Assert.Equal("corr-1", CorrelationScope.Current);

            using (CorrelationScope.Push("corr-2"))
            {
                Assert.Equal("corr-2", CorrelationScope.Current);
            }

            Assert.Equal("corr-1", CorrelationScope.Current);
        }

        Assert.Null(CorrelationScope.Current);
    }
}

public class ErpConnectorTests
{
    [Fact]
    public async Task GetInventoryAsync_AddsCorrelationHeader()
    {
        using var handler = new CapturingHandler(_ =>
            JsonResponse("""{"itemId":"ITEM-123","warehouseId":"MAD","availableQty":27,"etaUtc":"2030-01-02T10:00:00.0000000Z"}"""));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5080") };
        var connector = new HttpErpConnector(client);

        using (CorrelationScope.Push("corr-inv-1"))
        {
            _ = await connector.GetInventoryAsync("ITEM-123", "MAD", CancellationToken.None);
        }

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.True(request.Headers.TryGetValues("x-correlation-id", out var values));
        Assert.Equal("corr-inv-1", Assert.Single(values));
    }

    [Fact]
    public async Task CreateDraftOrderAsync_AddsCorrelationHeader()
    {
        using var handler = new CapturingHandler(_ =>
            JsonResponse("""{"draftId":"D-1","status":"draft","warnings":[]}"""));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5080") };
        var connector = new HttpErpConnector(client);
        var dto = new CreateDraftOrderDto(
            "ACME",
            "2030-01-01",
            new[] { new DraftLineDto("ITEM-123", 10, 12.34m) },
            "idem-1");

        using (CorrelationScope.Push("corr-draft-1"))
        {
            _ = await connector.CreateDraftOrderAsync(dto, CancellationToken.None);
        }

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.True(request.Headers.TryGetValues("x-correlation-id", out var values));
        Assert.Equal("corr-draft-1", Assert.Single(values));
    }

    [Fact]
    public async Task GetOrderExceptionContextAsync_AddsCorrelationHeader()
    {
        using var handler = new CapturingHandler(_ =>
            JsonResponse("""{"orderId":"SO-456","summaryCode":"BACKORDER","data":{"holds":["CREDIT_HOLD"]}}"""));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5080") };
        var connector = new HttpErpConnector(client);

        using (CorrelationScope.Push("corr-so-1"))
        {
            _ = await connector.GetOrderExceptionContextAsync("SO-456", CancellationToken.None);
        }

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.True(request.Headers.TryGetValues("x-correlation-id", out var values));
        Assert.Equal("corr-so-1", Assert.Single(values));
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;
        private readonly List<HttpRequestMessage> _requests = new();

        public IReadOnlyList<HttpRequestMessage> Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Add(request);
            return Task.FromResult(_responder(request));
        }
    }
}

public class OpenAiClientLoggingTests
{
    private static readonly SemaphoreSlim s_logEnvGate = new(1, 1);

    [Fact]
    public async Task PlanToolsAsync_WhenPayloadLoggingFlagHasWhitespace_LogsRequestAndResponsePayloads()
    {
        await s_logEnvGate.WaitAsync();
        try
        {
            var originalEnv = Environment.GetEnvironmentVariable("OPENAI_LOG_PAYLOADS");
            var originalOut = Console.Out;
            using var writer = new StringWriter();

            try
            {
                Environment.SetEnvironmentVariable("OPENAI_LOG_PAYLOADS", "  \"1\"  ");
                Console.SetOut(writer);

                using var handler = new CapturingHandler(_ =>
                    JsonResponse("""{"output":[]}"""));
                using var client = new HttpClient(handler);
                var openAiClient = new HttpOpenAiClient(client);

                _ = await openAiClient.PlanToolsAsync("hello", new OpenAiPlannerSettings { ApiKey = "demo-key" }, CancellationToken.None);

                var logs = writer.ToString();
                Assert.Contains("openai_request endpoint=responses operation=plan_tools attempt=1 payload=", logs);
                Assert.Contains("openai_response endpoint=responses operation=plan_tools attempt=1 status=200 body=", logs);
            }
            finally
            {
                Console.SetOut(originalOut);
                Environment.SetEnvironmentVariable("OPENAI_LOG_PAYLOADS", originalEnv);
            }
        }
        finally
        {
            s_logEnvGate.Release();
        }
    }

    [Fact]
    public async Task PlanToolsAsync_WhenPayloadLoggingFlagDisabled_DoesNotLogPayloads()
    {
        await s_logEnvGate.WaitAsync();
        try
        {
            var originalEnv = Environment.GetEnvironmentVariable("OPENAI_LOG_PAYLOADS");
            var originalOut = Console.Out;
            using var writer = new StringWriter();

            try
            {
                Environment.SetEnvironmentVariable("OPENAI_LOG_PAYLOADS", "0");
                Console.SetOut(writer);

                using var handler = new CapturingHandler(_ =>
                    JsonResponse("""{"output":[]}"""));
                using var client = new HttpClient(handler);
                var openAiClient = new HttpOpenAiClient(client);

                _ = await openAiClient.PlanToolsAsync("hello", new OpenAiPlannerSettings { ApiKey = "demo-key" }, CancellationToken.None);

                var logs = writer.ToString();
                Assert.DoesNotContain("openai_request endpoint=responses operation=plan_tools attempt=1 payload=", logs);
                Assert.DoesNotContain("openai_response endpoint=responses operation=plan_tools attempt=1 status=200 body=", logs);
            }
            finally
            {
                Console.SetOut(originalOut);
                Environment.SetEnvironmentVariable("OPENAI_LOG_PAYLOADS", originalEnv);
            }
        }
        finally
        {
            s_logEnvGate.Release();
        }
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }
}

public class OpenAiClientRequestTests
{
    [Fact]
    public async Task PlanToolsAsync_SendsNonStoredStrictClosedToolSchemas()
    {
        string? requestJson = null;
        using var handler = new CapturingHandler(async request =>
        {
            requestJson = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""{"output":[]}""");
        });
        using var client = new HttpClient(handler);
        var openAiClient = new HttpOpenAiClient(client);

        _ = await openAiClient.PlanToolsAsync(
            "hello",
            new OpenAiPlannerSettings { ApiKey = "demo-key" },
            CancellationToken.None);

        using var payload = JsonDocument.Parse(Assert.IsType<string>(requestJson));
        var root = payload.RootElement;
        Assert.Equal("gpt-5-mini", root.GetProperty("model").GetString());
        Assert.Equal("hello", root.GetProperty("input").GetString());
        Assert.Equal("auto", root.GetProperty("tool_choice").GetString());
        Assert.False(root.GetProperty("store").GetBoolean());

        var tools = root.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(3, tools.Length);
        Assert.All(tools, static tool => Assert.Equal("function", tool.GetProperty("type").GetString()));

        var inventory = GetTool(tools, "GetInventoryAvailability");
        AssertExactPropertyTypes(
            inventory.GetProperty("parameters"),
            new Dictionary<string, string>
            {
                ["itemId"] = "string",
                ["warehouseId"] = "string"
            });

        var draft = GetTool(tools, "CreateDraftSalesOrder");
        AssertExactPropertyTypes(
            draft.GetProperty("parameters"),
            new Dictionary<string, string>
            {
                ["customerId"] = "string",
                ["requestedDate"] = "string",
                ["idempotencyKey"] = "string",
                ["lines"] = "array"
            });
        AssertExactPropertyTypes(
            draft.GetProperty("parameters").GetProperty("properties").GetProperty("lines").GetProperty("items"),
            new Dictionary<string, string>
            {
                ["item"] = "string",
                ["qty"] = "integer",
                ["unitPrice"] = "number"
            });

        var exception = GetTool(tools, "ExplainOrderException");
        AssertExactPropertyTypes(
            exception.GetProperty("parameters"),
            new Dictionary<string, string> { ["orderId"] = "string" });

        foreach (var tool in tools)
        {
            Assert.True(tool.GetProperty("strict").GetBoolean());
            AssertClosedStrictObjectSchema(tool.GetProperty("parameters"));
        }
    }

    [Fact]
    public async Task SummarizeOrderExceptionAsync_DisablesResponseStorage()
    {
        string? requestJson = null;
        using var handler = new CapturingHandler(async request =>
        {
            requestJson = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""{"output_text":"summary"}""");
        });
        using var client = new HttpClient(handler);
        var openAiClient = new HttpOpenAiClient(client);

        _ = await openAiClient.SummarizeOrderExceptionAsync(
            "SO-456",
            "BACKORDER",
            new Dictionary<string, object>(),
            new OpenAiPlannerSettings { ApiKey = "demo-key", EnableSummarization = true },
            CancellationToken.None);

        using var payload = JsonDocument.Parse(Assert.IsType<string>(requestJson));
        Assert.False(payload.RootElement.GetProperty("store").GetBoolean());
    }

    [Fact]
    public async Task PlanToolsAsync_CancelsInFlightOutboundRequest()
    {
        using var handler = new CancellationObservingHandler();
        using var client = new HttpClient(handler);
        var openAiClient = new HttpOpenAiClient(client);
        using var cts = new CancellationTokenSource();

        var planning = openAiClient.PlanToolsAsync(
            "hello",
            new OpenAiPlannerSettings { ApiKey = "demo-key" },
            cts.Token);
        await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => planning);
        await handler.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task PlanToolsAsync_CancellationDuringRetryDelay_PreventsAnotherAttempt()
    {
        using var handler = new RetryableResponseHandler();
        using var client = new HttpClient(handler);
        var openAiClient = new HttpOpenAiClient(client);
        using var cts = new CancellationTokenSource();

        var planning = openAiClient.PlanToolsAsync(
            "hello",
            new OpenAiPlannerSettings { ApiKey = "demo-key" },
            cts.Token);
        await handler.FirstResponseSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => planning);
        Assert.Equal(1, handler.CallCount);
    }

    private static void AssertClosedStrictObjectSchema(JsonElement schema)
    {
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());

        var propertyNames = schema.GetProperty("properties")
            .EnumerateObject()
            .Select(static property => property.Name)
            .Order()
            .ToArray();
        var requiredNames = schema.GetProperty("required")
            .EnumerateArray()
            .Select(static item => item.GetString())
            .Order()
            .ToArray();
        Assert.Equal(propertyNames, requiredNames);

        foreach (var property in schema.GetProperty("properties").EnumerateObject())
        {
            if (property.Value.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == "object")
            {
                AssertClosedStrictObjectSchema(property.Value);
            }

            if (property.Value.TryGetProperty("items", out var items)
                && items.TryGetProperty("type", out var itemType)
                && itemType.GetString() == "object")
            {
                AssertClosedStrictObjectSchema(items);
            }
        }
    }

    private static JsonElement GetTool(JsonElement[] tools, string name)
    {
        return Assert.Single(tools, tool => tool.GetProperty("name").GetString() == name);
    }

    private static void AssertExactPropertyTypes(
        JsonElement schema,
        IReadOnlyDictionary<string, string> expectedTypes)
    {
        var properties = schema.GetProperty("properties");
        Assert.Equal(expectedTypes.Count, properties.EnumerateObject().Count());
        foreach (var expected in expectedTypes)
        {
            Assert.Equal(expected.Value, properties.GetProperty(expected.Key).GetProperty("type").GetString());
        }
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return responder(request);
        }
    }

    private sealed class CancellationObservingHandler : HttpMessageHandler
    {
        public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }
    }

    private sealed class RetryableResponseHandler : HttpMessageHandler
    {
        public TaskCompletionSource FirstResponseSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            FirstResponseSent.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        }
    }
}

internal sealed class StubOpenAiClient(IReadOnlyList<ToolCall> calls, string? summary = null) : IOpenAiClient
{
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
        return Task.FromResult(summary);
    }
}

internal sealed class ThrowingOpenAiClient : IOpenAiClient
{
    public Task<IReadOnlyList<ToolCall>> PlanToolsAsync(string message, OpenAiPlannerSettings settings, CancellationToken ct)
    {
        throw new InvalidOperationException("simulated openai failure");
    }

    public Task<string?> SummarizeOrderExceptionAsync(
        string orderId,
        string summaryCode,
        IReadOnlyDictionary<string, object> data,
        OpenAiPlannerSettings settings,
        CancellationToken ct)
    {
        throw new InvalidOperationException("simulated openai failure");
    }
}

internal sealed class CancellationAwareOpenAiClient : IOpenAiClient
{
    public TaskCompletionSource PlanStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SummaryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<IReadOnlyList<ToolCall>> PlanToolsAsync(
        string message,
        OpenAiPlannerSettings settings,
        CancellationToken ct)
    {
        PlanStarted.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return Array.Empty<ToolCall>();
    }

    public async Task<string?> SummarizeOrderExceptionAsync(
        string orderId,
        string summaryCode,
        IReadOnlyDictionary<string, object> data,
        OpenAiPlannerSettings settings,
        CancellationToken ct)
    {
        SummaryStarted.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return null;
    }
}

internal sealed class CountingPlanner : IAiPlanner
{
    public int CallCount { get; private set; }

    public Task<IReadOnlyList<ToolCall>> PlanAsync(string message, CancellationToken ct)
    {
        CallCount++;
        return Task.FromResult<IReadOnlyList<ToolCall>>(Array.Empty<ToolCall>());
    }
}

public class InforTokenClientTests
{
    [Fact]
    public async Task TokenClient_CachesToken_UntilExpiry()
    {
        var clock = new ManualClock(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var handler = new SequencedHandler(
            JsonResponse("""{"access_token":"token-1","expires_in":120}"""),
            JsonResponse("""{"access_token":"token-2","expires_in":120}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new InforTokenClient(
            http,
            new InforTokenClientSettings("client-id", "client-secret", null, "/oauth/token"),
            clock.UtcNow);

        var first = await client.GetAccessTokenAsync(CancellationToken.None);
        var second = await client.GetAccessTokenAsync(CancellationToken.None);

        Assert.Equal("token-1", first);
        Assert.Equal("token-1", second);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task TokenClient_RefreshesAfterExpiry()
    {
        var clock = new ManualClock(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var handler = new SequencedHandler(
            JsonResponse("""{"access_token":"token-1","expires_in":90}"""),
            JsonResponse("""{"access_token":"token-2","expires_in":90}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new InforTokenClient(
            http,
            new InforTokenClientSettings("client-id", "client-secret", null, "/oauth/token"),
            clock.UtcNow);

        var first = await client.GetAccessTokenAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(61));
        var second = await client.GetAccessTokenAsync(CancellationToken.None);

        Assert.Equal("token-1", first);
        Assert.Equal("token-2", second);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task TokenClient_Error_DoesNotLeakSecret()
    {
        using var handler = new SequencedHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("unauthorized")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new InforTokenClient(
            http,
            new InforTokenClientSettings("client-id", "super-secret", null, "/oauth/token"),
            () => DateTimeOffset.UtcNow);

        var ex = await Assert.ThrowsAsync<InforApiException>(() => client.GetAccessTokenAsync(CancellationToken.None));

        Assert.DoesNotContain("super-secret", ex.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}

public class InforApiClientTests
{
    [Fact]
    public async Task InforApiClient_AddsBearerAndCorrelationHeaders()
    {
        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var tokenClient = new StubTokenClient("token-abc");
        var api = new InforApiClient(http, tokenClient);

        using (CorrelationScope.Push("corr-123"))
        {
            _ = await api.GetAsync<Dictionary<string, object>>("/orders/123", CancellationToken.None);
        }

        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("token-abc", request.Headers.Authorization?.Parameter);
        Assert.True(request.Headers.TryGetValues("x-correlation-id", out var values));
        Assert.Equal("corr-123", Assert.Single(values));
    }

    [Fact]
    public async Task InforApiClient_NonSuccess_ThrowsSafeException()
    {
        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var tokenClient = new StubTokenClient("token-secret");
        var api = new InforApiClient(http, tokenClient);

        var ex = await Assert.ThrowsAsync<InforApiException>(() =>
            api.GetAsync<Dictionary<string, object>>("/orders/123", CancellationToken.None));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.DoesNotContain("token-secret", ex.Message, StringComparison.Ordinal);
    }

    private sealed class StubTokenClient(string token) : IInforTokenClient
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct)
        {
            return Task.FromResult(token);
        }
    }
}

internal sealed class ManualClock(DateTimeOffset initial)
{
    private DateTimeOffset _now = initial;

    public DateTimeOffset UtcNow() => _now;

    public void Advance(TimeSpan delta)
    {
        _now = _now.Add(delta);
    }
}

internal sealed class SequencedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
{
    private readonly HttpResponseMessage[] _responses = responses;
    private int _callCount;

    public int RequestCount => _callCount;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var index = Math.Min(_callCount, _responses.Length - 1);
        var response = _responses.Length == 0
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : _responses[index];
        _callCount++;
        return Task.FromResult(response);
    }
}

internal sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;
    private readonly List<HttpRequestMessage> _requests = new();

    public IReadOnlyList<HttpRequestMessage> Requests => _requests;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Add(request);
        return Task.FromResult(_responder(request));
    }
}
