namespace EclipseAi.Domain;

public sealed record ChatRequest(string Message);

public sealed record ToolCall(string Name, IReadOnlyDictionary<string, object> Args);

public sealed record Evidence(string Source, string Path, object? Value);

public sealed record ChatResponse(
    string CorrelationId,
    string Answer,
    IReadOnlyList<ToolCall> ToolCalls,
    IReadOnlyList<Evidence> Evidence,
    string AuditRef
);
