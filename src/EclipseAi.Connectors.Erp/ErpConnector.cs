using System.Net.Http.Json;
using EclipseAi.Observability;

namespace EclipseAi.Connectors.Erp;

public interface IErpConnector
{
    Task<InventoryDto> GetInventoryAsync(string itemId, string warehouseId, CancellationToken ct);
    Task<DraftOrderDto> CreateDraftOrderAsync(CreateDraftOrderDto dto, CancellationToken ct);
    Task<OrderExceptionContextDto> GetOrderExceptionContextAsync(string orderId, CancellationToken ct);
}

public sealed class HttpErpConnector(HttpClient http) : IErpConnector
{
    public async Task<InventoryDto> GetInventoryAsync(string itemId, string warehouseId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/inventory/{itemId}?warehouse={warehouseId}");
        TryAddCorrelationHeader(req);
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<InventoryDto>(cancellationToken: ct))!;
    }

    public async Task<DraftOrderDto> CreateDraftOrderAsync(CreateDraftOrderDto dto, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/draftSalesOrders")
        {
            Content = JsonContent.Create(dto)
        };

        TryAddCorrelationHeader(req);
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<DraftOrderDto>(cancellationToken: ct))!;
    }

    public async Task<OrderExceptionContextDto> GetOrderExceptionContextAsync(string orderId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/orderException/{orderId}");
        TryAddCorrelationHeader(req);
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<OrderExceptionContextDto>(cancellationToken: ct))!;
    }

    private static void TryAddCorrelationHeader(HttpRequestMessage req)
    {
        if (!string.IsNullOrWhiteSpace(CorrelationScope.Current))
        {
            req.Headers.TryAddWithoutValidation("x-correlation-id", CorrelationScope.Current);
        }
    }
}

public sealed record InventoryDto(string ItemId, string WarehouseId, int AvailableQty, string EtaUtc);
public sealed record CreateDraftOrderDto(string CustomerId, string ShipDate, IReadOnlyList<DraftLineDto> Lines, string IdempotencyKey);
public sealed record DraftLineDto(string Sku, int Qty);
public sealed record DraftOrderDto(string DraftId, string Status, IReadOnlyList<string> Warnings);
public sealed record OrderExceptionContextDto(string OrderId, string SummaryCode, IReadOnlyDictionary<string, object> Data);
