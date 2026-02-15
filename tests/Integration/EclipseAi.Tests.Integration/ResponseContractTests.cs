using EclipseAi.Domain;

namespace EclipseAi.Tests.Integration;

public class ResponseContractTests
{
    [Fact]
    public void ChatResponse_ContainsRequiredContractFields()
    {
        var response = new ChatResponse(
            "corr-1",
            "ok",
            new List<ToolCall> { new("GetInventoryAvailability", new Dictionary<string, object>()) },
            new List<Evidence> { new("erp.inventory", "itemId", "ITEM-123") },
            ".audit/corr-1.json");

        Assert.False(string.IsNullOrWhiteSpace(response.CorrelationId));
        Assert.False(string.IsNullOrWhiteSpace(response.Answer));
        Assert.NotEmpty(response.ToolCalls);
        Assert.NotEmpty(response.Evidence);
        Assert.False(string.IsNullOrWhiteSpace(response.AuditRef));
    }
}
