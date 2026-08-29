using EclipseAi.Domain;

namespace EclipseAi.AI;

public interface IAiPlanner
{
    Task<IReadOnlyList<ToolCall>> PlanAsync(string message, CancellationToken ct);
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
    Task<string?> SummarizeAsync(
        string orderId,
        string summaryCode,
        IReadOnlyDictionary<string, object> data,
        CancellationToken ct);
}

public sealed class OpenAiPlannerSettings
{
    public required string ApiKey { get; init; }
    public string Model { get; init; } = "gpt-5-mini";
    public Uri BaseUri { get; init; } = new("https://api.openai.com/v1/");
    public bool EnableSummarization { get; init; }
    public bool EmulateToolCalling { get; init; } = true;
}
