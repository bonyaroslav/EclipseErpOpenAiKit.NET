namespace EclipseAi.AI;

public sealed class NoopOrderExceptionSummarizer : IOrderExceptionSummarizer
{
    public Task<string?> SummarizeAsync(
        string orderId,
        string summaryCode,
        IReadOnlyDictionary<string, object> data,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(null);
    }
}

public sealed class DeterministicOrderExceptionSummarizer : IOrderExceptionSummarizer
{
    public Task<string?> SummarizeAsync(
        string orderId,
        string summaryCode,
        IReadOnlyDictionary<string, object> data,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(BuildDefaultSummary(orderId, summaryCode));
    }

    internal static string BuildDefaultSummary(string orderId, string summaryCode)
    {
        return $"Order {orderId} delayed ({summaryCode}).";
    }
}

public sealed class OpenAiOrderExceptionSummarizer(IOpenAiClient client, OpenAiPlannerSettings settings) : IOrderExceptionSummarizer
{
    public async Task<string?> SummarizeAsync(
        string orderId,
        string summaryCode,
        IReadOnlyDictionary<string, object> data,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            return await client.SummarizeOrderExceptionAsync(orderId, summaryCode, data, settings, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            ct.ThrowIfCancellationRequested();
            return DeterministicOrderExceptionSummarizer.BuildDefaultSummary(orderId, summaryCode);
        }
    }
}
