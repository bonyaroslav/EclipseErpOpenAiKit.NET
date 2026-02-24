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
        Assert.True(paths.TryGetProperty("/orders/draft", out _));
        Assert.True(paths.TryGetProperty("/orders/{orderId}/exception-context", out _));
    }

    [Fact]
    public void SampleContract_DraftSchemas_MatchCurrentInforDtoShape()
    {
        var contractPath = FindContractPath();
        var json = File.ReadAllText(contractPath);
        using var doc = JsonDocument.Parse(json);

        var schemas = doc.RootElement
            .GetProperty("components")
            .GetProperty("schemas");

        var createDraft = schemas.GetProperty("CreateDraftOrderDto");
        AssertRequiredContains(createDraft, "customerId", "requestedDate", "lines", "idempotencyKey");
        Assert.False(RequiredContains(createDraft, "shipDate"));

        var line = schemas.GetProperty("DraftLineDto");
        AssertRequiredContains(line, "item", "qty", "unitPrice");
        Assert.False(RequiredContains(line, "sku"));

        var draft = schemas.GetProperty("DraftOrderDto");
        AssertRequiredContains(draft, "draftId", "status");
        Assert.False(RequiredContains(draft, "warnings"));
        Assert.True(draft.GetProperty("properties").TryGetProperty("externalOrderNumber", out _));
    }

    private static void AssertRequiredContains(JsonElement schema, params string[] expected)
    {
        foreach (var name in expected)
        {
            Assert.True(RequiredContains(schema, name), $"Expected required field '{name}'.");
        }
    }

    private static bool RequiredContains(JsonElement schema, string name)
    {
        return schema.GetProperty("required")
            .EnumerateArray()
            .Any(x => string.Equals(x.GetString(), name, StringComparison.Ordinal));
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
