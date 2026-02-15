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
