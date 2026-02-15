namespace EclipseAi.Governance;

public static class ToolPolicy
{
    private static readonly HashSet<string> s_allowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "GetInventoryAvailability",
        "CreateDraftSalesOrder",
        "ExplainOrderException"
    };

    public static bool IsAllowed(string toolName)
    {
        return s_allowlist.Contains(toolName);
    }

    public static bool IsDraftWriteAllowed(string toolName, IReadOnlyDictionary<string, object> args)
    {
        if (!toolName.Equals("CreateDraftSalesOrder", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!args.TryGetValue("idempotencyKey", out var idempotencyValue))
        {
            return false;
        }

        var idempotencyKey = idempotencyValue?.ToString();
        return !string.IsNullOrWhiteSpace(idempotencyKey);
    }
}

public interface IRedactor
{
    object Redact(object input);
}

public sealed class NoopRedactor : IRedactor
{
    public object Redact(object input) => input;
}

public sealed class MapRedactor : IRedactor
{
    private static readonly string[] SensitiveTokens =
    {
        "pii", "margin", "cost", "price", "email", "phone", "name"
    };

    public object Redact(object input)
    {
        if (input is IReadOnlyDictionary<string, object> roMapObj)
        {
            return RedactMap(roMapObj.Select(static kvp => new KeyValuePair<string, object?>(kvp.Key, kvp.Value)));
        }

        if (input is IReadOnlyDictionary<string, object?> roMap)
        {
            return RedactMap(roMap);
        }

        if (input is IDictionary<string, object> mapObj)
        {
            return RedactMap(mapObj.Select(static kvp => new KeyValuePair<string, object?>(kvp.Key, kvp.Value)));
        }

        if (input is IDictionary<string, object?> map)
        {
            return RedactMap(map);
        }

        if (input is IEnumerable<object?> sequence && input is not string)
        {
            return sequence.Select(RedactUnknown).ToArray();
        }

        return input;
    }

    private static Dictionary<string, object?> RedactMap(IEnumerable<KeyValuePair<string, object?>> map)
    {
        var output = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in map)
        {
            output[kvp.Key] = IsSensitive(kvp.Key) ? "[REDACTED]" : RedactUnknown(kvp.Value);
        }

        return output;
    }

    private static object? RedactUnknown(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is IReadOnlyDictionary<string, object?> roMap)
        {
            return RedactMap(roMap);
        }

        if (value is IReadOnlyDictionary<string, object> roMapObj)
        {
            return RedactMap(roMapObj.Select(static kvp => new KeyValuePair<string, object?>(kvp.Key, kvp.Value)));
        }

        if (value is IDictionary<string, object?> map)
        {
            return RedactMap(map);
        }

        if (value is IDictionary<string, object> mapObj)
        {
            return RedactMap(mapObj.Select(static kvp => new KeyValuePair<string, object?>(kvp.Key, kvp.Value)));
        }

        if (value is IEnumerable<object?> sequence && value is not string)
        {
            return sequence.Select(RedactUnknown).ToArray();
        }

        return value;
    }

    private static bool IsSensitive(string key)
    {
        foreach (var token in SensitiveTokens)
        {
            if (key.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
