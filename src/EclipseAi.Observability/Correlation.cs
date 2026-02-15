namespace EclipseAi.Observability;

public static class Correlation
{
    public static string NewId() => Guid.NewGuid().ToString("N");
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
