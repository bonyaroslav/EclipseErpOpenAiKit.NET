using System.Net;
using System.Text;
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
    public void PlannerFactory_WithApiKey_EmulatedMode_UsesOpenAiPlanner()
    {
        var planner = PlannerFactory.Create(openAiApiKey: "demo-key", openAiMode: "emulated");

        Assert.IsType<OpenAiPlanner>(planner);
        var call = Assert.Single(planner.Plan("Do we have ITEM-123 in warehouse MAD?"));
        Assert.Equal("GetInventoryAvailability", call.Name);
    }

    [Fact]
    public void PlannerFactory_WithApiKey_OffMode_UsesFakePlanner()
    {
        var planner = PlannerFactory.Create(openAiApiKey: "demo-key", openAiMode: "off");

        Assert.IsType<FakePlanner>(planner);
    }

    [Fact]
    public void OpenAiPlanner_RealMode_UsesOpenAiClientToolCalls()
    {
        var client = new StubOpenAiClient(
            [new ToolCall("GetInventoryAvailability", new Dictionary<string, object> { ["itemId"] = "ITEM-777", ["warehouseId"] = "DAL" })]);
        var planner = PlannerFactory.Create(
            openAiApiKey: "demo-key",
            openAiMode: "real",
            openAiClient: client,
            fallbackPlanner: new FakePlanner());

        var call = Assert.Single(planner.Plan("any"));
        Assert.Equal("GetInventoryAvailability", call.Name);
        Assert.Equal("ITEM-777", call.Args["itemId"]);
        Assert.Equal("DAL", call.Args["warehouseId"]);
    }

    [Fact]
    public void OpenAiPlanner_RealMode_FallsBackWhenClientFails()
    {
        var planner = PlannerFactory.Create(
            openAiApiKey: "demo-key",
            openAiMode: "real",
            openAiClient: new ThrowingOpenAiClient(),
            fallbackPlanner: new FakePlanner());

        var call = Assert.Single(planner.Plan("Do we have ITEM-123 in warehouse MAD?"));
        Assert.Equal("GetInventoryAvailability", call.Name);
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
    public void SummarizerFactory_EmulatedMode_ReturnsDeterministicSummarizer()
    {
        var summarizer = PlannerFactory.CreateSummarizer(openAiApiKey: "demo-key", openAiMode: "emulated", enableSummarization: true);

        Assert.IsType<DeterministicOrderExceptionSummarizer>(summarizer);
        Assert.Equal(
            "Order SO-456 delayed (BACKORDER).",
            summarizer.Summarize("SO-456", "BACKORDER", new Dictionary<string, object>()));
    }

    [Fact]
    public void Summarizer_RealMode_UsesOpenAiClientAndFallsBackOnError()
    {
        var summarizer = PlannerFactory.CreateSummarizer(
            openAiApiKey: "demo-key",
            openAiMode: "real",
            enableSummarization: true,
            openAiClient: new StubOpenAiClient([], "AI summary"));

        Assert.Equal("AI summary", summarizer.Summarize("SO-456", "BACKORDER", new Dictionary<string, object>()));

        var fallbackSummarizer = PlannerFactory.CreateSummarizer(
            openAiApiKey: "demo-key",
            openAiMode: "real",
            enableSummarization: true,
            openAiClient: new ThrowingOpenAiClient());

        Assert.Equal(
            "Order SO-456 delayed (BACKORDER).",
            fallbackSummarizer.Summarize("SO-456", "BACKORDER", new Dictionary<string, object>()));
    }

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

public class CorrelationTests
{
    [Fact]
    public void Correlation_FromHeaderOrNew_UsesIncomingHeaderWhenPresent()
    {
        var correlationId = Correlation.FromHeaderOrNew(" corr-client-1 ");

        Assert.Equal("corr-client-1", correlationId);
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
        var dto = new CreateDraftOrderDto("ACME", "2030-01-01", new[] { new DraftLineDto("ITEM-123", 10) }, "idem-1");

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
