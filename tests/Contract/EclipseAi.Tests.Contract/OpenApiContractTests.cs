using System.Text.Json;

namespace EclipseAi.Tests.Contract;

public class OpenApiContractTests
{
    [Fact]
    public void SampleContract_ContainsRequiredScenarioPaths()
    {
        var contractPath = FindContractPath();
        Assert.True(File.Exists(contractPath));

        var json = File.ReadAllText(contractPath);
        using var doc = JsonDocument.Parse(json);

        var paths = doc.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/inventory/{itemId}", out _));
        Assert.True(paths.TryGetProperty("/draftSalesOrders", out _));
        Assert.True(paths.TryGetProperty("/orderException/{orderId}", out _));
    }

    private static string FindContractPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "contracts", "eclipse.sample.openapi.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "contracts", "eclipse.sample.openapi.json");
    }
}
