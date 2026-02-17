using Microsoft.AspNetCore.Http.HttpResults;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.Use(async (context, next) =>
{
    var startedAt = Stopwatch.GetTimestamp();
    var correlationId = context.Request.Headers["x-correlation-id"].FirstOrDefault();
    await next();
    var elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
    app.Logger.LogInformation(
        "mock_erp_request method={Method} path={Path} query={Query} status={StatusCode} correlationId={CorrelationId} elapsedMs={ElapsedMs:F1}",
        context.Request.Method,
        context.Request.Path.Value ?? string.Empty,
        context.Request.QueryString.Value ?? string.Empty,
        context.Response.StatusCode,
        string.IsNullOrWhiteSpace(correlationId) ? "-" : correlationId,
        elapsedMs);
});

app.MapGet("/inventory/{itemId}", (string itemId, string warehouse) =>
{
    var dto = new { itemId = itemId.ToUpperInvariant(), warehouseId = warehouse.ToUpperInvariant(), availableQty = 27, etaUtc = DateTime.UtcNow.AddHours(18).ToString("O") };
    return Results.Ok(dto);
});

app.MapPost("/draftSalesOrders", async (HttpRequest req) =>
{
    var payload = await req.ReadFromJsonAsync<Dictionary<string, object>>() ?? new();
    var idem = payload.TryGetValue("idempotencyKey", out var v) ? v?.ToString() : "missing";
    var dto = new { draftId = $"D-{idem}", status = "draft", warnings = new[] { "ETA for one line may be +2d" } };
    return Results.Ok(dto);
});

app.MapGet("/orderException/{orderId}", (string orderId) =>
{
    var dto = new
    {
        orderId = orderId.ToUpperInvariant(),
        summaryCode = "BACKORDER_HOLD_AR",
        data = new Dictionary<string, object>
        {
            ["holds"] = new[] { "CREDIT_HOLD" },
            ["backorderedSkus"] = new[] { "ITEM-123" },
            ["arOverdueDays"] = 14,
            ["warehouse"] = "MAD"
        }
    };
    return Results.Ok(dto);
});

app.MapGet("/health", () => Results.Ok(new { ok = true }));

app.Run();
