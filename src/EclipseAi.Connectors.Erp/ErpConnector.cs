using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
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

public interface IInforTokenClient
{
    Task<string> GetAccessTokenAsync(CancellationToken ct);
}

public sealed record InforTokenClientSettings(
    string ClientId,
    string ClientSecret,
    string? Scope,
    string TokenEndpoint = "/oauth/token");

public sealed class InforTokenClient(HttpClient http, InforTokenClientSettings settings, Func<DateTimeOffset>? utcNow = null)
    : IInforTokenClient
{
    private static readonly TimeSpan s_refreshSkew = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    private string? _accessToken;
    private DateTimeOffset _expiresAtUtc;

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (IsTokenValid())
        {
            return _accessToken!;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (IsTokenValid())
            {
                return _accessToken!;
            }

            var token = await RequestTokenAsync(ct);
            _accessToken = token.Token;
            _expiresAtUtc = token.ExpiresAtUtc;
            return _accessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsTokenValid()
    {
        if (string.IsNullOrWhiteSpace(_accessToken))
        {
            return false;
        }

        return _utcNow() < _expiresAtUtc - s_refreshSkew;
    }

    private async Task<CachedAccessToken> RequestTokenAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, settings.TokenEndpoint)
        {
            Content = BuildTokenContent()
        };
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            throw new InforApiException(
                $"Infor token request failed with status {(int)res.StatusCode}.",
                res.StatusCode);
        }

        var payload = await res.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct)
            ?? throw new InforApiException("Infor token response was empty.", res.StatusCode);

        var expiresAtUtc = _utcNow().AddSeconds(Math.Max(payload.ExpiresIn, 1));
        return new CachedAccessToken(payload.AccessToken, expiresAtUtc);
    }

    private FormUrlEncodedContent BuildTokenContent()
    {
        var fields = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = settings.ClientId,
            ["client_secret"] = settings.ClientSecret
        };

        if (!string.IsNullOrWhiteSpace(settings.Scope))
        {
            fields["scope"] = settings.Scope!;
        }

        return new FormUrlEncodedContent(fields);
    }

    private sealed record CachedAccessToken(string Token, DateTimeOffset ExpiresAtUtc);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}

public sealed class InforApiClient(HttpClient http, IInforTokenClient tokenClient)
{
    public async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        await AddHeadersAsync(req, ct);
        using var res = await http.SendAsync(req, ct);
        return await ReadResponseAsync<T>(res, path, ct);
    }

    public async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body)
        };
        await AddHeadersAsync(req, ct);
        using var res = await http.SendAsync(req, ct);
        return await ReadResponseAsync<T>(res, path, ct);
    }

    private async Task AddHeadersAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var token = await tokenClient.GetAccessTokenAsync(ct);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (!string.IsNullOrWhiteSpace(CorrelationScope.Current))
        {
            req.Headers.TryAddWithoutValidation("x-correlation-id", CorrelationScope.Current);
        }
    }

    private static async Task<T> ReadResponseAsync<T>(HttpResponseMessage res, string path, CancellationToken ct)
    {
        if (!res.IsSuccessStatusCode)
        {
            throw new InforApiException(
                $"Infor API request failed with status {(int)res.StatusCode} for {path}.",
                res.StatusCode);
        }

        var payload = await res.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
        return payload ?? throw new InforApiException(
            $"Infor API response was empty for {path}.",
            res.StatusCode);
    }
}

public sealed class InforApiException : Exception
{
    public InforApiException(string message, HttpStatusCode? statusCode) : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }
}

public sealed class InforErpConnector(InforApiClient api) : IErpConnector
{
    public Task<InventoryDto> GetInventoryAsync(string itemId, string warehouseId, CancellationToken ct)
    {
        return api.GetAsync<InventoryDto>($"/inventory/{itemId}?warehouse={warehouseId}", ct);
    }

    public Task<DraftOrderDto> CreateDraftOrderAsync(CreateDraftOrderDto dto, CancellationToken ct)
    {
        return api.PostAsync<DraftOrderDto>("/orders/draft", dto, ct);
    }

    public Task<OrderExceptionContextDto> GetOrderExceptionContextAsync(string orderId, CancellationToken ct)
    {
        return api.GetAsync<OrderExceptionContextDto>($"/orders/{orderId}/exception-context", ct);
    }
}

public sealed record InventoryDto(string ItemId, string WarehouseId, int AvailableQty, string EtaUtc);
public sealed record CreateDraftOrderDto(string CustomerId, string RequestedDate, IReadOnlyList<DraftLineDto> Lines, string IdempotencyKey);
public sealed record DraftLineDto(string Item, int Qty, decimal UnitPrice);
public sealed record DraftOrderDto(string DraftId, string? ExternalOrderNumber, string Status);
public sealed record OrderExceptionContextDto(string OrderId, string SummaryCode, IReadOnlyDictionary<string, object> Data);
