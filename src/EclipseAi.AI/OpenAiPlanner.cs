using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EclipseAi.Domain;

namespace EclipseAi.AI;

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
        using var req = CreateRequest(settings, new
        {
            model = settings.Model,
            input = message,
            tool_choice = "auto",
            tools = OpenAiToolSchema.Build()
        });

        using var res = await httpClient.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return OpenAiResponseParser.ParseToolCalls(doc.RootElement);
    }

    public async Task<string?> SummarizeOrderExceptionAsync(
        string orderId,
        string summaryCode,
        IReadOnlyDictionary<string, object> data,
        OpenAiPlannerSettings settings,
        CancellationToken ct)
    {
        if (!settings.EnableSummarization)
        {
            return null;
        }

        var dataJson = JsonSerializer.Serialize(data);
        using var req = CreateRequest(settings, new
        {
            model = settings.Model,
            input = $"Summarize order exception in one sentence. OrderId={orderId}; SummaryCode={summaryCode}; Data={dataJson}"
        });

        using var res = await httpClient.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return OpenAiResponseParser.ParseTextOutput(doc.RootElement);
    }

    private static HttpRequestMessage CreateRequest(OpenAiPlannerSettings settings, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.BaseUri, "responses"));
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {settings.ApiKey}");
        request.Content = JsonContent.Create(payload, options: s_jsonOptions);
        return request;
    }
}

internal static class OpenAiToolSchema
{
    public static object[] Build()
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
}

internal static class OpenAiResponseParser
{
    public static IReadOnlyList<ToolCall> ParseToolCalls(JsonElement root)
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

            var name = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var argsJson = item.TryGetProperty("arguments", out var argsElement) ? argsElement.GetString() : "{}";
            var args = ParseArgs(argsJson ?? "{}");
            calls.Add(new ToolCall(name, args));
        }

        return calls;
    }

    public static string? ParseTextOutput(JsonElement root)
    {
        if (!root.TryGetProperty("output_text", out var outputText))
        {
            return null;
        }

        return outputText.GetString();
    }

    private static IReadOnlyDictionary<string, object> ParseArgs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, object>();
        }

        var args = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            args[property.Name] = ConvertValue(property.Value)!;
        }

        return args;
    }

    private static object? ConvertValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt32(out var intValue) => intValue,
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => value.EnumerateArray().Select(ConvertValue).ToArray(),
            JsonValueKind.Object => value.EnumerateObject().ToDictionary(p => p.Name, p => ConvertValue(p.Value)!),
            _ => null
        };
    }
}
