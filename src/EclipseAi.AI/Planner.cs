using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EclipseAi.Domain;

namespace EclipseAi.AI;

public interface IAiPlanner
{
    IReadOnlyList<ToolCall> Plan(string message);
}

public static class PlannerFactory
{
    public static IAiPlanner Create(
        string? openAiApiKey,
        string? openAiMode = null,
        IOpenAiClient? openAiClient = null,
        IAiPlanner? fallbackPlanner = null)
    {
        var fallback = fallbackPlanner ?? new FakePlanner();
        if (string.IsNullOrWhiteSpace(openAiApiKey))
        {
            return fallback;
        }

        var mode = string.IsNullOrWhiteSpace(openAiMode) ? "emulated" : openAiMode.Trim();
        if (mode.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }

        var settings = new OpenAiPlannerSettings
        {
            ApiKey = openAiApiKey.Trim(),
            EmulateToolCalling = !mode.Equals("real", StringComparison.OrdinalIgnoreCase)
        };

        return new OpenAiPlanner(openAiClient ?? HttpOpenAiClient.CreateDefault(), fallback, settings);
    }
}

public sealed class OpenAiPlanner(IOpenAiClient client, IAiPlanner fallbackPlanner, OpenAiPlannerSettings settings) : IAiPlanner
{
    public IReadOnlyList<ToolCall> Plan(string message)
    {
        if (settings.EmulateToolCalling)
        {
            return fallbackPlanner.Plan(message);
        }

        try
        {
            var calls = client.PlanToolsAsync(message, settings, CancellationToken.None).GetAwaiter().GetResult();
            return calls.Count > 0 ? calls : fallbackPlanner.Plan(message);
        }
        catch
        {
            return fallbackPlanner.Plan(message);
        }
    }
}

public sealed class OpenAiPlannerSettings
{
    public required string ApiKey { get; init; }
    public string Model { get; init; } = "gpt-4.1-mini";
    public Uri BaseUri { get; init; } = new("https://api.openai.com/v1/");
    public bool EnableSummarization { get; init; } = false;
    public bool EmulateToolCalling { get; init; } = true;
}

public interface IOpenAiClient
{
    Task<IReadOnlyList<ToolCall>> PlanToolsAsync(string message, OpenAiPlannerSettings settings, CancellationToken ct);
}

public sealed class HttpOpenAiClient(HttpClient httpClient) : IOpenAiClient
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static HttpOpenAiClient CreateDefault()
    {
        return new HttpOpenAiClient(new HttpClient());
    }

    public async Task<IReadOnlyList<ToolCall>> PlanToolsAsync(string message, OpenAiPlannerSettings settings, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.BaseUri, "responses"));
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {settings.ApiKey}");
        req.Content = JsonContent.Create(new
        {
            model = settings.Model,
            input = message,
            tool_choice = "auto",
            tools = BuildToolsSchema()
        }, options: s_jsonOptions);

        using var res = await httpClient.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return ParseToolCalls(doc.RootElement);
    }

    private static object[] BuildToolsSchema()
    {
        return
        [
            new
            {
                type = "function",
                name = "GetInventoryAvailability",
                description = "Get inventory availability for an item in a warehouse.",
                parameters = new
                {
                    type = "object",
                    required = new[] { "itemId", "warehouseId" },
                    properties = new Dictionary<string, object>
                    {
                        ["itemId"] = new { type = "string" },
                        ["warehouseId"] = new { type = "string" }
                    }
                }
            },
            new
            {
                type = "function",
                name = "CreateDraftSalesOrder",
                description = "Create a draft sales order with idempotency key.",
                parameters = new
                {
                    type = "object",
                    required = new[] { "customerId", "shipDate", "lines", "idempotencyKey" },
                    properties = new Dictionary<string, object>
                    {
                        ["customerId"] = new { type = "string" },
                        ["shipDate"] = new { type = "string" },
                        ["idempotencyKey"] = new { type = "string" },
                        ["lines"] = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                required = new[] { "sku", "qty" },
                                properties = new Dictionary<string, object>
                                {
                                    ["sku"] = new { type = "string" },
                                    ["qty"] = new { type = "integer" }
                                }
                            }
                        }
                    }
                }
            },
            new
            {
                type = "function",
                name = "ExplainOrderException",
                description = "Analyze order exception reasons for a sales order.",
                parameters = new
                {
                    type = "object",
                    required = new[] { "orderId" },
                    properties = new Dictionary<string, object>
                    {
                        ["orderId"] = new { type = "string" }
                    }
                }
            }
        ];
    }

    private static IReadOnlyList<ToolCall> ParseToolCalls(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ToolCall>();
        }

        var calls = new List<ToolCall>();
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var type) || type.GetString() != "function_call")
            {
                continue;
            }

            var name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var argsJson = item.TryGetProperty("arguments", out var argsEl) ? argsEl.GetString() : "{}";
            var args = ParseArgs(argsJson ?? "{}");
            calls.Add(new ToolCall(name, args));
        }

        return calls;
    }

    private static IReadOnlyDictionary<string, object> ParseArgs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, object>();
        }

        var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            map[prop.Name] = ConvertValue(prop.Value)!;
        }

        return map;
    }

    private static object? ConvertValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt32(out var intVal) => intVal,
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => value.EnumerateArray().Select(ConvertValue).ToArray(),
            JsonValueKind.Object => value.EnumerateObject().ToDictionary(p => p.Name, p => ConvertValue(p.Value)!),
            _ => null
        };
    }
}

public sealed class FakePlanner : IAiPlanner
{
    private const string DemoShipDate = "2030-01-01";

    private static readonly Regex SoRegex = new(@"SO-\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ItemRegex = new(@"ITEM-\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WhRegex = new(@"\b([A-Z]{3})\b", RegexOptions.Compiled);

    public IReadOnlyList<ToolCall> Plan(string message)
    {
        var so = SoRegex.Match(message).Value;
        if (!string.IsNullOrWhiteSpace(so))
        {
            return
            [
                new ToolCall("ExplainOrderException", new Dictionary<string, object>
                {
                    ["orderId"] = so.ToUpperInvariant(),
                    ["role"] = "customer_support"
                })
            ];
        }

        if (message.Contains("draft", StringComparison.OrdinalIgnoreCase))
        {
            var item = ItemRegex.Match(message).Value;
            return
            [
                new ToolCall("CreateDraftSalesOrder", new Dictionary<string, object>
                {
                    ["customerId"] = "ACME",
                    ["lines"] = new object[] { new Dictionary<string, object>{{"sku", string.IsNullOrWhiteSpace(item) ? "ITEM-123" : item.ToUpperInvariant()}, {"qty", 10}} },
                    ["shipDate"] = DemoShipDate,
                    ["idempotencyKey"] = "demo-key-001"
                })
            ];
        }

        var invItem = ItemRegex.Match(message).Value;
        var wh = WhRegex.Match(message).Value;
        if (string.IsNullOrWhiteSpace(wh))
        {
            wh = "MAD";
        }

        return
        [
            new ToolCall("GetInventoryAvailability", new Dictionary<string, object>
            {
                ["itemId"] = string.IsNullOrWhiteSpace(invItem) ? "ITEM-123" : invItem.ToUpperInvariant(),
                ["warehouseId"] = wh.ToUpperInvariant()
            })
        ];
    }
}
