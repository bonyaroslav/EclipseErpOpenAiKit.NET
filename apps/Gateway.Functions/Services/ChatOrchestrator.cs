using EclipseAi.AI;
using EclipseAi.Domain;
using EclipseAi.Governance;
using EclipseAi.Observability;

namespace Gateway.Functions;

public sealed class ChatOrchestrator(
    IAiPlanner planner,
    IEnumerable<IChatToolHandler> handlers,
    IRedactor redactor,
    IAuditStore auditStore,
    ILogger<ChatOrchestrator> logger)
{
    private readonly Dictionary<string, IChatToolHandler> _handlers = handlers
        .GroupBy(static h => h.ToolName, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(static g => g.Key, static g => g.First(), StringComparer.OrdinalIgnoreCase);

    public async Task<ChatResponse> HandleAsync(ChatRequest request, string? incomingCorrelationId, CancellationToken ct)
    {
        var message = request?.Message ?? string.Empty;
        var correlationId = Correlation.FromHeaderOrNew(incomingCorrelationId);
        using var correlationScope = CorrelationScope.Push(correlationId);

        var plannedCalls = planner.Plan(message);
        var executedCalls = new List<ToolCall>();
        var evidence = new List<Evidence>();
        var answerParts = new List<string>();

        foreach (var plannedCall in plannedCalls)
        {
            if (!ToolPolicy.IsAllowed(plannedCall.Name))
            {
                continue;
            }

            if (!ToolPolicy.IsDraftWriteAllowed(plannedCall.Name, plannedCall.Args))
            {
                continue;
            }

            if (!_handlers.TryGetValue(plannedCall.Name, out var handler))
            {
                continue;
            }

            var result = await handler.ExecuteAsync(plannedCall, ct);
            if (!string.IsNullOrWhiteSpace(result.AnswerPart))
            {
                answerParts.Add(result.AnswerPart);
            }

            if (result.Executed)
            {
                executedCalls.Add(plannedCall);
            }

            if (result.Evidence.Count > 0)
            {
                evidence.AddRange(result.Evidence);
            }
        }

        if (answerParts.Count == 0)
        {
            answerParts.Add(ChatResponseText.NoEligibleToolCall());
        }

        var response = new ChatResponse(
            correlationId,
            string.Join(" ", answerParts),
            executedCalls,
            evidence,
            $".audit/{correlationId}.json");

        var auditToolCalls = response.ToolCalls
            .Select(static call => new Dictionary<string, object?>
            {
                ["name"] = call.Name,
                ["args"] = call.Args
            })
            .ToArray();
        var auditEvidence = response.Evidence
            .Select(static item => new Dictionary<string, object?>
            {
                ["source"] = item.Source,
                ["path"] = item.Path,
                ["value"] = item.Value
            })
            .ToArray();

        var redactedPayload = redactor.Redact(new Dictionary<string, object?>
        {
            ["correlationId"] = response.CorrelationId,
            ["answer"] = response.Answer,
            ["toolCalls"] = auditToolCalls,
            ["evidence"] = auditEvidence,
            ["auditRef"] = response.AuditRef
        });

        await auditStore.WriteAsync(correlationId, redactedPayload, ct);
        TryLogInformation("chat_completed correlationId={CorrelationId} toolCalls={ToolCallCount}", correlationId, executedCalls.Count);

        return response;
    }

    private void TryLogInformation(string message, params object[] args)
    {
        try
        {
            logger.LogInformation(message, args);
        }
        catch
        {
            // Keep chat flow resilient when host logging sinks are unavailable.
        }
    }
}
