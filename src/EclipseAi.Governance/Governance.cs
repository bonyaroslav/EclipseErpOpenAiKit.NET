namespace EclipseAi.Governance;

public static class ToolPolicy
{
    private const string DraftSalesOrderTool = "CreateDraftSalesOrder";
    private const string IdempotencyKeyField = "idempotencyKey";

    private static readonly HashSet<string> s_allowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "GetInventoryAvailability",
        DraftSalesOrderTool,
        "ExplainOrderException"
    };

    public static bool IsAllowed(string toolName) => s_allowlist.Contains(toolName);

    public static bool IsDraftWriteAllowed(string toolName, IReadOnlyDictionary<string, object> args)
    {
        if (!IsDraftWriteTool(toolName))
        {
            return true;
        }

        return TryGetNonEmptyString(args, IdempotencyKeyField, out _);
    }

    private static bool IsDraftWriteTool(string toolName)
    {
        return toolName.Equals(DraftSalesOrderTool, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetNonEmptyString(
        IReadOnlyDictionary<string, object> args,
        string key,
        out string value)
    {
        value = string.Empty;
        if (!args.TryGetValue(key, out var raw))
        {
            return false;
        }

        var text = raw?.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        value = text;
        return true;
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
    private const string RedactedValue = "[REDACTED]";
    private static readonly string[] s_sensitiveTokens =
    {
        "pii", "margin", "cost", "price", "email", "phone", "name"
    };

    public object Redact(object input) => RedactCore(input)!;

    private static object? RedactCore(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (TryGetMapEntries(value, out var mapEntries))
        {
            return RedactMap(mapEntries);
        }

        if (TryGetSequence(value, out var sequence))
        {
            return sequence.Select(RedactCore).ToArray();
        }

        return value;
    }

    private static Dictionary<string, object?> RedactMap(IEnumerable<KeyValuePair<string, object?>> map)
    {
        var output = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in map)
        {
            output[kvp.Key] = IsSensitive(kvp.Key) ? RedactedValue : RedactCore(kvp.Value);
        }

        return output;
    }

    private static bool TryGetMapEntries(object value, out IEnumerable<KeyValuePair<string, object?>> mapEntries)
    {
        if (value is IReadOnlyDictionary<string, object?> roMap)
        {
            mapEntries = roMap;
            return true;
        }

        if (value is IReadOnlyDictionary<string, object> roMapObj)
        {
            mapEntries = roMapObj.Select(static kvp => new KeyValuePair<string, object?>(kvp.Key, kvp.Value));
            return true;
        }

        if (value is IDictionary<string, object?> map)
        {
            mapEntries = map;
            return true;
        }

        if (value is IDictionary<string, object> mapObj)
        {
            mapEntries = mapObj.Select(static kvp => new KeyValuePair<string, object?>(kvp.Key, kvp.Value));
            return true;
        }

        mapEntries = Array.Empty<KeyValuePair<string, object?>>();
        return false;
    }

    private static bool TryGetSequence(object value, out IEnumerable<object?> sequence)
    {
        if (value is IEnumerable<object?> objectSequence && value is not string)
        {
            sequence = objectSequence;
            return true;
        }

        sequence = Array.Empty<object?>();
        return false;
    }

    private static bool IsSensitive(string key)
    {
        foreach (var token in s_sensitiveTokens)
        {
            if (key.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
