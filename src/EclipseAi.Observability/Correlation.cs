using System.Text.RegularExpressions;

namespace EclipseAi.Observability;

public static partial class Correlation
{
    private static readonly Regex s_allowed = AllowedRegex();

    public static string NewId() => Guid.NewGuid().ToString("N");

    public static string FromHeaderOrNew(string? incoming)
    {
        if (string.IsNullOrWhiteSpace(incoming))
        {
            return NewId();
        }

        var trimmed = incoming.Trim();
        return IsValid(trimmed) ? trimmed : NewId();
    }

    public static string ToSafeFileName(string correlationId)
    {
        return IsValid(correlationId) ? correlationId : NewId();
    }

    private static bool IsValid(string value)
    {
        return s_allowed.IsMatch(value);
    }

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$", RegexOptions.Compiled)]
    private static partial Regex AllowedRegex();
}

public static class CorrelationScope
{
    private static readonly AsyncLocal<string?> s_current = new();

    public static string? Current => s_current.Value;

    public static IDisposable Push(string correlationId)
    {
        var previous = s_current.Value;
        s_current.Value = correlationId;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        private readonly string? _previous = previous;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            s_current.Value = _previous;
            _disposed = true;
        }
    }
}