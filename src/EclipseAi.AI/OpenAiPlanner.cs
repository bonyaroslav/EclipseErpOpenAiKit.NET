using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using EclipseAi.Domain;

namespace EclipseAi.AI;

public sealed class OpenAiPlanner(
    IOpenAiClient client,
    IAiPlanner fallbackPlanner,
    OpenAiPlannerSettings settings,
    Action<string>? onFallback = null,
    Action<string>? onDecision = null) : IAiPlanner
{
    public IReadOnlyList<ToolCall> Plan(string message)
    {
        if (settings.EmulateToolCalling)
        {
            onFallback?.Invoke("reason=emulated_mode");
            var fallbackCalls = fallbackPlanner.Plan(message);
            onDecision?.Invoke(BuildDecisionDetails("fallback", "emulated_mode", fallbackCalls));
            return fallbackCalls;
        }

        try
        {
            var calls = client.PlanToolsAsync(message, settings, CancellationToken.None).GetAwaiter().GetResult();
            if (calls.Count > 0)
            {
                onDecision?.Invoke(BuildDecisionDetails("openai", "tool_calls", calls));
                return calls;
            }

            onFallback?.Invoke("reason=no_tool_calls");
            var fallbackCalls = fallbackPlanner.Plan(message);
            onDecision?.Invoke(BuildDecisionDetails("fallback", "no_tool_calls", fallbackCalls));
            return fallbackCalls;
        }
        catch (Exception ex)
        {
            var safeMessage = SanitizeForLog(ex.Message);
            onFallback?.Invoke($"reason=openai_exception exception_type={ex.GetType().Name} exception_message={safeMessage}");
            var fallbackCalls = fallbackPlanner.Plan(message);
            onDecision?.Invoke(BuildDecisionDetails("fallback", "openai_exception", fallbackCalls));
            return fallbackCalls;
        }
    }

    private static string BuildDecisionDetails(string source, string reason, IReadOnlyList<ToolCall> calls)
    {
        var tools = calls.Count == 0
            ? "none"
            : string.Join(",", calls.Select(static c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase));
        return $"source={source} reason={reason} tool_calls={calls.Count} tools={tools}";
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "empty";
        }

        var compact = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length <= 256 ? compact : compact[..256];
    }
}

public sealed class HttpOpenAiClient(HttpClient httpClient) : IOpenAiClient
{
    private const int MaxRetries = 5;
    private const int DefaultBaseDelaySec = 1;
    private const int DefaultMaxDelaySec = 60;

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
        var payload = new
        {
            model = settings.Model,
            input = message,
            tool_choice = "auto",
            tools = OpenAiToolSchema.Build()
        };
        var payloadJson = JsonSerializer.Serialize(payload, s_jsonOptions);
        var responseJson = await SendResponsesRequestWithRetryAsync("plan_tools", settings, payloadJson, ct);
        using var doc = JsonDocument.Parse(responseJson);
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
        var payload = new
        {
            model = settings.Model,
            input = $"Summarize order exception in one sentence. OrderId={orderId}; SummaryCode={summaryCode}; Data={dataJson}"
        };
        var payloadJson = JsonSerializer.Serialize(payload, s_jsonOptions);
        var responseJson = await SendResponsesRequestWithRetryAsync("summarize_order_exception", settings, payloadJson, ct);
        using var doc = JsonDocument.Parse(responseJson);
        return OpenAiResponseParser.ParseTextOutput(doc.RootElement);
    }

    private async Task<string> SendResponsesRequestWithRetryAsync(
        string operation,
        OpenAiPlannerSettings settings,
        string payloadJson,
        CancellationToken ct)
    {
        var baseDelaySec = ReadDelaySeconds("OPENAI_RETRY_BASE_DELAY_SEC", DefaultBaseDelaySec);
        var maxDelaySec = ReadDelaySeconds("OPENAI_RETRY_MAX_DELAY_SEC", DefaultMaxDelaySec);

        for (var attempt = 0; ; attempt++)
        {
            using var req = CreateRequest(settings, payloadJson);
            TryLogPayload(
                "openai_request endpoint=responses operation={Operation} attempt={Attempt} payload={Payload}",
                payloadJson,
                null,
                operation,
                attempt + 1);

            try
            {
                using var res = await httpClient.SendAsync(req, ct);
                var responseJson = await res.Content.ReadAsStringAsync(ct);
                TryLogPayload(
                    "openai_response endpoint=responses operation={Operation} attempt={Attempt} status={Status} body={Body}",
                    responseJson,
                    (int)res.StatusCode,
                    operation,
                    attempt + 1);

                if (res.IsSuccessStatusCode)
                {
                    if (attempt > 0)
                    {
                        TryLogRetrySucceeded(operation, attempt + 1, (int)res.StatusCode);
                    }
                    return responseJson;
                }

                if (!IsRetryableStatusCode((int)res.StatusCode) || attempt >= MaxRetries)
                {
                    throw new HttpRequestException(
                        $"OpenAI request failed with status {(int)res.StatusCode}. body={SanitizeForLog(responseJson)}",
                        null,
                        res.StatusCode);
                }

                var (delay, source) = ComputeRetryDelay(res.Headers.RetryAfter, attempt, baseDelaySec, maxDelaySec);
                TryLogRetry(operation, attempt + 1, "http_status", (int)res.StatusCode, delay, source, null);
                await Task.Delay(delay, ct);
            }
            catch (Exception ex) when (IsRetryableException(ex, ct) && attempt < MaxRetries)
            {
                var delay = ComputeExponentialBackoffWithJitter(attempt, baseDelaySec, maxDelaySec);
                TryLogRetry(operation, attempt + 1, "exception", null, delay, "exponential_jitter", ex.GetType().Name);
                await Task.Delay(delay, ct);
            }
        }
    }

    private static HttpRequestMessage CreateRequest(OpenAiPlannerSettings settings, string payloadJson)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.BaseUri, "responses"));
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {settings.ApiKey}");
        request.Content = new StringContent(payloadJson, System.Text.Encoding.UTF8, "application/json");
        return request;
    }

    private static bool IsPayloadLoggingEnabled()
    {
        return IsTruthyFlag(Environment.GetEnvironmentVariable("OPENAI_LOG_PAYLOADS"));
    }

    internal static bool IsTruthyFlag(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var normalized = raw.Trim();
        if (normalized.Length >= 2)
        {
            var startsWithQuote = normalized[0] is '"' or '\'';
            var endsWithQuote = normalized[^1] is '"' or '\'';
            if (startsWithQuote && endsWithQuote)
            {
                normalized = normalized[1..^1].Trim();
            }
        }

        return normalized.Equals("1", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("true", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private void TryLogPayload(
        string messageTemplate,
        string content,
        int? statusCode = null,
        string? operation = null,
        int? attempt = null)
    {
        if (!IsPayloadLoggingEnabled())
        {
            return;
        }

        var compact = content.Replace('\r', ' ').Replace('\n', ' ').Trim();
        var bounded = compact.Length <= 4000 ? compact : compact[..4000];

        if (statusCode.HasValue)
        {
            var message = messageTemplate
                .Replace("{Operation}", operation ?? "unknown", StringComparison.Ordinal)
                .Replace("{Attempt}", attempt?.ToString() ?? "0", StringComparison.Ordinal)
                .Replace("{Status}", statusCode.Value.ToString(), StringComparison.Ordinal)
                .Replace("{Body}", bounded, StringComparison.Ordinal)
                .Replace("{Payload}", bounded, StringComparison.Ordinal);
            Console.WriteLine($"info: OpenAiDiagnostics[0]{Environment.NewLine}      {message}");
            return;
        }

        var payloadMessage = messageTemplate
            .Replace("{Operation}", operation ?? "unknown", StringComparison.Ordinal)
            .Replace("{Attempt}", attempt?.ToString() ?? "0", StringComparison.Ordinal)
            .Replace("{Payload}", bounded, StringComparison.Ordinal)
            .Replace("{Body}", bounded, StringComparison.Ordinal);
        Console.WriteLine($"info: OpenAiDiagnostics[0]{Environment.NewLine}      {payloadMessage}");
    }

    private static bool IsRetryableStatusCode(int statusCode)
    {
        return statusCode == 408 || statusCode == 429 || statusCode >= 500;
    }

    private static bool IsRetryableException(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return false;
        }

        return ex is HttpRequestException || ex is TaskCanceledException;
    }

    private static (TimeSpan Delay, string Source) ComputeRetryDelay(
        RetryConditionHeaderValue? retryAfter,
        int attempt,
        int baseDelaySec,
        int maxDelaySec)
    {
        if (retryAfter?.Delta is TimeSpan delta)
        {
            return (delta > TimeSpan.Zero ? delta : TimeSpan.Zero, "retry_after");
        }

        if (retryAfter?.Date is DateTimeOffset date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return (delay > TimeSpan.Zero ? delay : TimeSpan.Zero, "retry_after");
        }

        return (ComputeExponentialBackoffWithJitter(attempt, baseDelaySec, maxDelaySec), "exponential_jitter");
    }

    private static TimeSpan ComputeExponentialBackoffWithJitter(int attempt, int baseDelaySec, int maxDelaySec)
    {
        var exponential = baseDelaySec * Math.Pow(2, attempt);
        var jitter = Random.Shared.NextDouble();
        var delaySeconds = Math.Min(maxDelaySec, exponential + jitter);
        return TimeSpan.FromSeconds(delaySeconds);
    }

    private static int ReadDelaySeconds(string envName, int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(envName);
        if (!int.TryParse(raw, out var parsed) || parsed <= 0)
        {
            return defaultValue;
        }

        return parsed;
    }

    private static void TryLogRetry(
        string operation,
        int attempt,
        string reason,
        int? statusCode,
        TimeSpan delay,
        string source,
        string? exceptionType)
    {
        var status = statusCode.HasValue ? statusCode.Value.ToString() : "n/a";
        var exception = exceptionType ?? "n/a";
        Console.WriteLine(
            $"warn: OpenAiDiagnostics[0]{Environment.NewLine}      openai_retry operation={operation} attempt={attempt} reason={reason} status={status} exception_type={exception} delay_ms={(int)delay.TotalMilliseconds} delay_source={source}");
    }

    private static void TryLogRetrySucceeded(string operation, int attempts, int statusCode)
    {
        Console.WriteLine(
            $"info: OpenAiDiagnostics[0]{Environment.NewLine}      openai_retry_succeeded operation={operation} attempts={attempts} final_status={statusCode}");
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "empty";
        }

        var compact = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length <= 256 ? compact : compact[..256];
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
                    required = new[] { "customerId", "requestedDate", "lines", "idempotencyKey" },
                    properties = new Dictionary<string, object>
                    {
                        ["customerId"] = new { type = "string" },
                        ["requestedDate"] = new { type = "string" },
                        ["idempotencyKey"] = new { type = "string" },
                        ["lines"] = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                required = new[] { "item", "qty", "unitPrice" },
                                properties = new Dictionary<string, object>
                                {
                                    ["item"] = new { type = "string" },
                                    ["qty"] = new { type = "integer" },
                                    ["unitPrice"] = new { type = "number" }
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
