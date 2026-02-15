using System.Net;
using System.Text;
using EclipseAi.AI;
using EclipseAi.Connectors.Erp;
using EclipseAi.Governance;
using EclipseAi.Observability;

namespace EclipseAi.Tests.Unit;

public class PlannerTests
{
    [Fact]
    public void PlannerFactory_WithoutApiKey_UsesFakePlanner()
    {
        var planner = PlannerFactory.Create(openAiApiKey: null);

        Assert.IsType<FakePlanner>(planner);
    }

    [Fact]
    public void PlannerFactory_WithApiKey_UsesOpenAiPlannerEmulation()
    {
        var planner = PlannerFactory.Create(openAiApiKey: "demo-key");

        Assert.IsType<OpenAiPlanner>(planner);
        var call = Assert.Single(planner.Plan("Do we have ITEM-123 in warehouse MAD?"));
        Assert.Equal("GetInventoryAvailability", call.Name);
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
