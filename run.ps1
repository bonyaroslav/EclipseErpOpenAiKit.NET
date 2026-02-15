param(
  [Parameter(Mandatory = $false)]
  [string]$SolutionName = $(Split-Path -Leaf (Get-Location)),

  [Parameter(Mandatory = $false)]
  [string]$TargetFramework = "net10.0"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-Command($name) {
  if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
    throw "Required command '$name' not found in PATH."
  }
}

function Write-File($path, $content) {
  $dir = Split-Path $path -Parent
  if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
  Set-Content -Path $path -Value $content -Encoding UTF8
}

Write-Host "Scaffolding: $SolutionName ($TargetFramework)" -ForegroundColor Cyan

Assert-Command dotnet

# Core folders
$folders = @(
  "apps/Gateway.Functions",
  "apps/Gateway.Api",
  "src/EclipseAi.Domain",
  "src/EclipseAi.AI",
  "src/EclipseAi.Connectors.Erp",
  "src/EclipseAi.Governance",
  "src/EclipseAi.Observability",
  "mocks/Mock.Erp",
  "tests/Unit/EclipseAi.Tests.Unit",
  "tests/Contract",
  "tests/Integration",
  "contracts",
  "docs/diagrams",
  "examples"
)

$folders | ForEach-Object { New-Item -ItemType Directory -Force -Path $_ | Out-Null }

# Root files
Write-File ".gitignore" @"
bin/
obj/
.vs/
.idea/
*.user
*.suo
*.cache
*.log
.audit/
.azurite/
"@

Write-File "plan.md" @"
# $SolutionName — plan.md (MVP)

## Definition of Done
A new user can clone this repo on Windows 11 and run a deterministic end-to-end demo locally:
- Azure Functions host runs locally
- /api/chat triggers at least one ERP connector call to Mock ERP
- response includes correlationId + toolCalls + evidence + auditRef
- no OpenAI key required (offline FakePlanner)
- optional OpenAI mode via OPENAI_API_KEY

## Scenarios (E2E)
1) Inventory availability (read)
2) Draft sales order (draft-only, idempotent)
3) Order Exception Copilot (summary + reasons w/ evidence + next actions)

## Key decisions
- Azure Functions (.NET isolated) is the primary host
- Contract-first connector (OpenAPI stub now; replace with real later)
- Draft-only write posture by default
- Field allowlists + redaction before any AI call/audit
- Correlation IDs + audit events for every request
"@

Write-File "docs/threat-model.md" @"
# Threat model (small, practical)

- Prompt injection: only registered tools; validate tool args server-side.
- Data exfiltration: field allowlists + redaction before any AI call.
- Unsafe writes: draft-only default; commit disabled without explicit configuration + approval provider.
- Secrets: env vars / user-secrets only; no secrets in repo.
- Auditability: correlationId everywhere + audit events per ERP/tool call.
"@

Write-File "docs/scenarios.md" @"
# Scenarios (spec)

## 1) Inventory availability
Input: ""Do we have ITEM-123 in warehouse MAD? What’s available and ETA?""
Tool: GetInventoryAvailability(itemId, warehouseId)
ERP: GET /inventory/{itemId}?warehouse={warehouseId}

## 2) Draft sales order (guarded)
Input: ""Create a draft order for ACME: 10x ITEM-123, ship tomorrow.""
Tool: CreateDraftSalesOrder(customerId, lines, shipDate, idempotencyKey)
ERP: POST /draftSalesOrders
Rules: allowlist + idempotency required + draft-only

## 3) Order Exception Copilot (WOW)
Input: ""Why is SO-456 delayed and what should I do?""
ERP calls: /salesOrders/{id}, /holds, /lines, /inventory, /customers/{id}/arSummary
Output: Summary + Reasons (with evidence) + Next actions + optional draft customer message
"@

# Solution + projects
dotnet new sln -n $SolutionName | Out-Null

# Libraries
dotnet new classlib -n EclipseAi.Domain -o src/EclipseAi.Domain -f $TargetFramework | Out-Null
dotnet new classlib -n EclipseAi.AI -o src/EclipseAi.AI -f $TargetFramework | Out-Null
dotnet new classlib -n EclipseAi.Connectors.Erp -o src/EclipseAi.Connectors.Erp -f $TargetFramework | Out-Null
dotnet new classlib -n EclipseAi.Governance -o src/EclipseAi.Governance -f $TargetFramework | Out-Null
dotnet new classlib -n EclipseAi.Observability -o src/EclipseAi.Observability -f $TargetFramework | Out-Null

# Mock ERP (minimal ASP.NET Core)
dotnet new web -n Mock.Erp -o mocks/Mock.Erp -f $TargetFramework | Out-Null

# Unit tests
dotnet new xunit -n EclipseAi.Tests.Unit -o tests/Unit/EclipseAi.Tests.Unit -f $TargetFramework | Out-Null

# Add references (libs -> tests)
dotnet add tests/Unit/EclipseAi.Tests.Unit/EclipseAi.Tests.Unit.csproj reference src/EclipseAi.Domain/EclipseAi.Domain.csproj | Out-Null
dotnet add tests/Unit/EclipseAi.Tests.Unit/EclipseAi.Tests.Unit.csproj reference src/EclipseAi.AI/EclipseAi.AI.csproj | Out-Null

# Domain models (minimal)
Write-File "src/EclipseAi.Domain/Models.cs" @"
namespace EclipseAi.Domain;

public sealed record ChatRequest(string Message);

public sealed record ToolCall(string Name, IReadOnlyDictionary<string, object> Args);

public sealed record Evidence(string Source, string Path, object? Value);

public sealed record ChatResponse(
    string CorrelationId,
    string Answer,
    IReadOnlyList<ToolCall> ToolCalls,
    IReadOnlyList<Evidence> Evidence,
    string AuditRef
);
"@

# AI planner abstraction + deterministic FakePlanner
Write-File "src/EclipseAi.AI/Planner.cs" @"
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
"@

# Connector (calls Mock ERP; later you replace with Eclipse)
Write-File "src/EclipseAi.Connectors.Erp/ErpConnector.cs" @"
using System.Net.Http.Json;

namespace EclipseAi.Connectors.Erp;

public interface IErpConnector
{
    Task<InventoryDto> GetInventoryAsync(string itemId, string warehouseId, CancellationToken ct);
    Task<DraftOrderDto> CreateDraftOrderAsync(CreateDraftOrderDto dto, CancellationToken ct);
    Task<OrderExceptionContextDto> GetOrderExceptionContextAsync(string orderId, CancellationToken ct);
}

public sealed class HttpErpConnector(HttpClient http) : IErpConnector
{
    public Task<InventoryDto> GetInventoryAsync(string itemId, string warehouseId, CancellationToken ct)
        => http.GetFromJsonAsync<InventoryDto>($"/inventory/{itemId}?warehouse={warehouseId}", ct)!;

    public async Task<DraftOrderDto> CreateDraftOrderAsync(CreateDraftOrderDto dto, CancellationToken ct)
    {
        var res = await http.PostAsJsonAsync("/draftSalesOrders", dto, ct);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<DraftOrderDto>(cancellationToken: ct))!;
    }

    public Task<OrderExceptionContextDto> GetOrderExceptionContextAsync(string orderId, CancellationToken ct)
        => http.GetFromJsonAsync<OrderExceptionContextDto>($"/orderException/{orderId}", ct)!;
}

public sealed record InventoryDto(string ItemId, string WarehouseId, int AvailableQty, string EtaUtc);
public sealed record CreateDraftOrderDto(string CustomerId, string ShipDate, IReadOnlyList<DraftLineDto> Lines, string IdempotencyKey);
public sealed record DraftLineDto(string Sku, int Qty);
public sealed record DraftOrderDto(string DraftId, string Status, IReadOnlyList<string> Warnings);
public sealed record OrderExceptionContextDto(string OrderId, string SummaryCode, IReadOnlyDictionary<string, object> Data);
"@

# Governance + Observability placeholders (kept minimal for scaffold)
Write-File "src/EclipseAi.Governance/Governance.cs" @"
namespace EclipseAi.Governance;

public interface IRedactor
{
    object Redact(object input);
}

public sealed class NoopRedactor : IRedactor
{
    public object Redact(object input) => input;
}
"@

Write-File "src/EclipseAi.Observability/Correlation.cs" @"
namespace EclipseAi.Observability;

public static class Correlation
{
    public static string NewId() => Guid.NewGuid().ToString(""N"");
}
"@

# Mock ERP implementation (deterministic endpoints)
Write-File "mocks/Mock.Erp/Program.cs" @"
using Microsoft.AspNetCore.Http.HttpResults;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet(""/inventory/{itemId}"", (string itemId, string warehouse) =>
{
    var dto = new { itemId = itemId.ToUpperInvariant(), warehouseId = warehouse.ToUpperInvariant(), availableQty = 27, etaUtc = DateTime.UtcNow.AddHours(18).ToString(""O"") };
    return Results.Ok(dto);
});

app.MapPost(""/draftSalesOrders"", async (HttpRequest req) =>
{
    var payload = await req.ReadFromJsonAsync<Dictionary<string, object>>() ?? new();
    var idem = payload.TryGetValue(""idempotencyKey"", out var v) ? v?.ToString() : ""missing"";
    var dto = new { draftId = $""D-{idem}"", status = ""draft"", warnings = new [] { ""ETA for one line may be +2d"" } };
    return Results.Ok(dto);
});

app.MapGet(""/orderException/{orderId}"", (string orderId) =>
{
    var dto = new
    {
        orderId = orderId.ToUpperInvariant(),
        summaryCode = ""BACKORDER_HOLD_AR"",
        data = new Dictionary<string, object>
        {
            [""holds""] = new [] { ""CREDIT_HOLD"" },
            [""backorderedSkus""] = new [] { ""ITEM-123"" },
            [""arOverdueDays""] = 14,
            [""warehouse""] = ""MAD""
        }
    };
    return Results.Ok(dto);
});

app.MapGet(""/health"", () => Results.Ok(new { ok = true }));

app.Run();
"@

# Dockerfile for Mock ERP (optional; compose uses it)
Write-File "mocks/Mock.Erp/Dockerfile" @"
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish mocks/Mock.Erp/Mock.Erp.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT [""dotnet"", ""Mock.Erp.dll""]
"@

# docker-compose (Azurite + Mock ERP)
Write-File "docker-compose.yml" @"
services:
  azurite:
    image: mcr.microsoft.com/azure-storage/azurite
    container_name: azurite
    ports:
      - ""10000:10000""
      - ""10001:10001""
      - ""10002:10002""
    volumes:
      - ./.azurite:/data

  mock-erp:
    build:
      context: .
      dockerfile: mocks/Mock.Erp/Dockerfile
    container_name: mock-erp
    ports:
      - ""5080:8080""
    environment:
      - ASPNETCORE_URLS=http://+:8080
"@

# dev.ps1 helper
Write-File "dev.ps1" @"
param([string]$cmd = ""help"")

function Up { docker compose up -d }
function Down { docker compose down }
function RunFunc {
  if (-not (Get-Command func -ErrorAction SilentlyContinue)) {
    throw ""Azure Functions Core Tools ('func') not found. Install Core Tools, then re-run.""
  }
  Push-Location apps/Gateway.Functions
  func start
  Pop-Location
}
function Demo {
  \$base = ""http://localhost:7071/api/chat""
  Write-Host ""Assumes Functions host is running. If not, run: .\\dev.ps1 run"" -ForegroundColor Yellow
  \$msgs = @(
    ""Do we have ITEM-123 in warehouse MAD? What's available and ETA?"",
    ""Create a draft order for ACME: 10x ITEM-123, ship tomorrow."",
    ""Why is SO-456 delayed and what should I do?""
  )
  foreach (\$m in \$msgs) {
    Write-Host ""---"" -ForegroundColor DarkGray
    Write-Host \$m -ForegroundColor Cyan
    \$res = Invoke-RestMethod -Method Post -Uri \$base -ContentType ""application/json"" -Body (@{ message = \$m } | ConvertTo-Json)
    \$res | ConvertTo-Json -Depth 10
  }
}
function TestAll { dotnet test }

switch ($cmd) {
  ""up"" { Up }
  ""down"" { Down }
  ""run"" { RunFunc }
  ""demo"" { Demo }
  ""test"" { TestAll }
  default {
    Write-Host ""Commands: up | down | run | demo | test""
  }
}
"@

# Unit test example (proves deterministic planner and guardrail hook)
Write-File "tests/Unit/EclipseAi.Tests.Unit/PlannerTests.cs" @"
using EclipseAi.AI;

namespace EclipseAi.Tests.Unit;

public class PlannerTests
{
    [Fact]
    public void Inventory_message_plans_inventory_tool()
    {
        var p = new FakePlanner();
        var calls = p.Plan(""Do we have ITEM-123 in MAD?"");
        Assert.Single(calls);
        Assert.Equal(""GetInventoryAvailability"", calls[0].Name);
    }

    [Fact]
    public void Draft_message_plans_draft_tool()
    {
        var p = new FakePlanner();
        var calls = p.Plan(""Create a draft order for ACME: 10x ITEM-123"");
        Assert.Single(calls);
        Assert.Equal(""CreateDraftSalesOrder"", calls[0].Name);
    }

    [Fact]
    public void SO_message_plans_exception_tool()
    {
        var p = new FakePlanner();
        var calls = p.Plan(""Why is SO-456 delayed?"");
        Assert.Single(calls);
        Assert.Equal(""ExplainOrderException"", calls[0].Name);
    }
}
"@

# Add everything to solution
dotnet sln $SolutionName.sln add src/EclipseAi.Domain/EclipseAi.Domain.csproj | Out-Null
dotnet sln $SolutionName.sln add src/EclipseAi.AI/EclipseAi.AI.csproj | Out-Null
dotnet sln $SolutionName.sln add src/EclipseAi.Connectors.Erp/EclipseAi.Connectors.Erp.csproj | Out-Null
dotnet sln $SolutionName.sln add src/EclipseAi.Governance/EclipseAi.Governance.csproj | Out-Null
dotnet sln $SolutionName.sln add src/EclipseAi.Observability/EclipseAi.Observability.csproj | Out-Null
dotnet sln $SolutionName.sln add mocks/Mock.Erp/Mock.Erp.csproj | Out-Null
dotnet sln $SolutionName.sln add tests/Unit/EclipseAi.Tests.Unit/EclipseAi.Tests.Unit.csproj | Out-Null

# Wire lib references (AI -> Domain, Connector -> Domain optional)
dotnet add src/EclipseAi.AI/EclipseAi.AI.csproj reference src/EclipseAi.Domain/EclipseAi.Domain.csproj | Out-Null

dotnet add src/EclipseAi.Connectors.Erp/EclipseAi.Connectors.Erp.csproj reference src/EclipseAi.Domain/EclipseAi.Domain.csproj | Out-Null

# Azure Functions project (requires Core Tools)
if (Get-Command func -ErrorAction SilentlyContinue) {
  Write-Host "Creating Azure Functions project (dotnet-isolated)..." -ForegroundColor Cyan
  Push-Location apps/Gateway.Functions
  func init . --worker-runtime dotnet-isolated --target-framework $TargetFramework | Out-Null

  # Minimal chat function (kept small; uses FakePlanner + Mock ERP connector)
  Write-File "ChatFunction.cs" @"
using System.Net;
using System.Text.Json;
using EclipseAi.AI;
using EclipseAi.Connectors.Erp;
using EclipseAi.Domain;
using EclipseAi.Observability;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

public sealed class ChatFunction(IAiPlanner planner, IErpConnector erp)
{
    [Function(""chat"")]
    public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, ""post"", Route = ""chat"")] HttpRequestData req)
    {
        var correlationId = req.Headers.TryGetValues(""x-correlation-id"", out var values)
            ? values.FirstOrDefault() ?? Correlation.NewId()
            : Correlation.NewId();

        var body = await new StreamReader(req.Body).ReadToEndAsync();
        var chatReq = JsonSerializer.Deserialize<ChatRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new ChatRequest("""");

        var toolCalls = planner.Plan(chatReq.Message);
        var evidence = new List<Evidence>();
        string answer;

        // For scaffold: execute first tool call only.
        var call = toolCalls.FirstOrDefault();
        if (call is null)
        {
            answer = ""No tool call planned."";
        }
        else if (call.Name == ""GetInventoryAvailability"")
        {
            var itemId = call.Args[""itemId""].ToString()!;
            var wh = call.Args[""warehouseId""].ToString()!;
            var inv = await erp.GetInventoryAsync(itemId, wh, CancellationToken.None);
            evidence.Add(new Evidence(""erp"", ""inventory.availableQty"", inv.AvailableQty));
            evidence.Add(new Evidence(""erp"", ""inventory.etaUtc"", inv.EtaUtc));
            answer = $""Available: {inv.AvailableQty} in {inv.WarehouseId}. ETA: {inv.EtaUtc}."";
        }
        else if (call.Name == ""CreateDraftSalesOrder"")
        {
            // NOTE: policy enforcement is a TODO; scaffold only.
            var customerId = call.Args[""customerId""].ToString()!;
            var shipDate = call.Args[""shipDate""].ToString()!;
            var idk = call.Args[""idempotencyKey""].ToString()!;
            var lines = new List<DraftLineDto> { new(""ITEM-123"", 10) };

            var draft = await erp.CreateDraftOrderAsync(new CreateDraftOrderDto(customerId, shipDate, lines, idk), CancellationToken.None);
            evidence.Add(new Evidence(""erp"", ""draft.draftId"", draft.DraftId));
            answer = $""Draft created: {draft.DraftId}. Status: {draft.Status}. Warnings: {string.Join("", "", draft.Warnings)}"";
        }
        else if (call.Name == ""ExplainOrderException"")
        {
            var orderId = call.Args[""orderId""].ToString()!;
            var ctx = await erp.GetOrderExceptionContextAsync(orderId, CancellationToken.None);
            evidence.Add(new Evidence(""erp"", ""orderException.summaryCode"", ctx.SummaryCode));
            answer = $""Order {ctx.OrderId} exception: {ctx.SummaryCode}. Next actions: review holds, check backorders, contact customer if needed."";
        }
        else
        {
            answer = $""Tool not implemented: {call.Name}"";
        }

        // Minimal audit: write to local file
        Directory.CreateDirectory("".audit"");
        var auditPath = Path.Combine("".audit"", $""audit-{DateTime.UtcNow:yyyyMMdd}.jsonl"");
        await File.AppendAllTextAsync(auditPath, JsonSerializer.Serialize(new {
            ts = DateTime.UtcNow,
            correlationId,
            tool = call?.Name,
            args = call?.Args
        }) + Environment.NewLine);

        var resp = req.CreateResponse(HttpStatusCode.OK);
        var payload = new ChatResponse(
            CorrelationId: correlationId,
            Answer: answer,
            ToolCalls: toolCalls.ToList(),
            Evidence: evidence,
            AuditRef: auditPath
        );
        await resp.WriteAsJsonAsync(payload);
        return resp;
    }
}
"@

  # Program.cs DI wiring
  Write-File "Program.cs" @"
using EclipseAi.AI;
using EclipseAi.Connectors.Erp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddSingleton<IAiPlanner, FakePlanner>();

        services.AddHttpClient<IErpConnector, HttpErpConnector>(http =>
        {
            var baseUrl = Environment.GetEnvironmentVariable(""MOCK_ERP_BASEURL"") ?? ""http://localhost:5080"";
            http.BaseAddress = new Uri(baseUrl);
        });
    })
    .Build();

host.Run();
"@

  # local.settings.json for local run
  Write-File "local.settings.json" @"
{
  ""IsEncrypted"": false,
  ""Values"": {
    ""AzureWebJobsStorage"": ""UseDevelopmentStorage=true"",
    ""FUNCTIONS_WORKER_RUNTIME"": ""dotnet-isolated"",
    ""MOCK_ERP_BASEURL"": ""http://localhost:5080""
  }
}
"@

  Pop-Location
} else {
  Write-Warning "Azure Functions Core Tools ('func') not found. Functions project created as folder only. Install Core Tools then run:"
  Write-Warning "  cd apps/Gateway.Functions; func init . --worker-runtime dotnet-isolated --target-framework $TargetFramework"
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "Next:" -ForegroundColor Cyan
Write-Host "  1) .\dev.ps1 up"
Write-Host "  2) .\dev.ps1 run    (in another terminal)"
Write-Host "  3) .\dev.ps1 demo"
Write-Host "  4) .\dev.ps1 test"
