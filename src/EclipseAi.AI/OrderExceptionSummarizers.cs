namespace EclipseAi.AI;

public sealed class NoopOrderExceptionSummarizer : IOrderExceptionSummarizer
{
    public string? Summarize(string orderId, string summaryCode, IReadOnlyDictionary<string, object> data)
    {
        return null;
    }
}

public sealed class DeterministicOrderExceptionSummarizer : IOrderExceptionSummarizer
{
    public string? Summarize(string orderId, string summaryCode, IReadOnlyDictionary<string, object> data)
    {
        return BuildDefaultSummary(orderId, summaryCode);
    }

    internal static string BuildDefaultSummary(string orderId, string summaryCode)
    {
        return $"Order {orderId} delayed ({summaryCode}).";
    }
}

public sealed class OpenAiOrderExceptionSummarizer(IOpenAiClient client, OpenAiPlannerSettings settings) : IOrderExceptionSummarizer
{
    public string? Summarize(string orderId, string summaryCode, IReadOnlyDictionary<string, object> data)
    {
        try
        {
            return client.SummarizeOrderExceptionAsync(orderId, summaryCode, data, settings, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            return DeterministicOrderExceptionSummarizer.BuildDefaultSummary(orderId, summaryCode);
        }
    }
}
