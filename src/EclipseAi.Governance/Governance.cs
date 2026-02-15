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
        if (input is not IReadOnlyDictionary<string, object?> map)
        {
            return input;
        }

        var output = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in map)
        {
            output[kvp.Key] = IsSensitive(kvp.Key) ? "[REDACTED]" : kvp.Value;
        }

        return output;
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
