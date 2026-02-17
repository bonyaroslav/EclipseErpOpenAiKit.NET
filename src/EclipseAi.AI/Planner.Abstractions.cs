using EclipseAi.Domain;

namespace EclipseAi.AI;

public interface IAiPlanner
{
    IReadOnlyList<ToolCall> Plan(string message);
}

public interface IOpenAiClient
{
    Task<IReadOnlyList<ToolCall>> PlanToolsAsync(string message, OpenAiPlannerSettings settings, CancellationToken ct);
    Task<string?> SummarizeOrderExceptionAsync(
        string orderId,
        string summaryCode,
        IReadOnlyDictionary<string, object> data,
        OpenAiPlannerSettings settings,
        CancellationToken ct);
}

public interface IOrderExceptionSummarizer
{
    string? Summarize(string orderId, string summaryCode, IReadOnlyDictionary<string, object> data);
}

public sealed class OpenAiPlannerSettings
{
    public required string ApiKey { get; init; }
    public string Model { get; init; } = "gpt-5-mini";
    public Uri BaseUri { get; init; } = new("https://api.openai.com/v1/");
    public bool EnableSummarization { get; init; }
    public bool EmulateToolCalling { get; init; } = true;
}
