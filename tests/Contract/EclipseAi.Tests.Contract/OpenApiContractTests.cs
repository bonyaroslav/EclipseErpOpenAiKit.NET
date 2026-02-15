using System.Text.Json;

namespace EclipseAi.Tests.Contract;

public class OpenApiContractTests
{
    [Fact]
    public void SampleContract_ContainsRequiredScenarioPaths()
    {
        var contractPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "contracts", "eclipse.sample.openapi.json"));
        Assert.True(File.Exists(contractPath));

        var json = File.ReadAllText(contractPath);
        using var doc = JsonDocument.Parse(json);

        var paths = doc.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/inventory/{itemId}", out _));
        Assert.True(paths.TryGetProperty("/draftSalesOrders", out _));
        Assert.True(paths.TryGetProperty("/orderException/{orderId}", out _));
    }
}
