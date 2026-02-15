using Microsoft.AspNetCore.Http.HttpResults;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/inventory/{itemId}", (string itemId, string warehouse) =>
{
    var dto = new { itemId = itemId.ToUpperInvariant(), warehouseId = warehouse.ToUpperInvariant(), availableQty = 27, etaUtc = DateTime.UtcNow.AddHours(18).ToString("O") };
    return Results.Ok(dto);
});

app.MapPost("/draftSalesOrders", async (HttpRequest req) =>
{
    var payload = await req.ReadFromJsonAsync<Dictionary<string, object>>() ?? new();
    var idem = payload.TryGetValue("idempotencyKey", out var v) ? v?.ToString() : "missing";
    var dto = new { draftId = $"D-{idem}", status = "draft", warnings = new [] { "ETA for one line may be +2d" } };
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
            ["holds"] = new [] { "CREDIT_HOLD" },
            ["backorderedSkus"] = new [] { "ITEM-123" },
            ["arOverdueDays"] = 14,
            ["warehouse"] = "MAD"
        }
    };
    return Results.Ok(dto);
});

app.MapGet("/health", () => Results.Ok(new { ok = true }));

app.Run();
