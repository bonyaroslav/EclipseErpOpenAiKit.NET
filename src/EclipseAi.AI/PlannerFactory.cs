namespace EclipseAi.AI;

public static class PlannerFactory
{
    private const string ModeOff = "off";
    private const string ModeReal = "real";
    private const string ModeEmulated = "emulated";

    public static IAiPlanner Create(
        string? openAiApiKey,
        string? openAiMode = null,
        IOpenAiClient? openAiClient = null,
        IAiPlanner? fallbackPlanner = null,
        Action<string>? onFallback = null,
        Action<string>? onDecision = null)
    {
        var fallback = fallbackPlanner ?? new FakePlanner();
        if (!CanUseOpenAi(openAiApiKey, openAiMode))
        {
            return fallback;
        }

        var settings = BuildSettings(openAiApiKey!, openAiMode, enableSummarization: false);
        return new OpenAiPlanner(openAiClient ?? HttpOpenAiClient.CreateDefault(), fallback, settings, onFallback, onDecision);
    }

    public static IOrderExceptionSummarizer CreateSummarizer(
        string? openAiApiKey,
        string? openAiMode = null,
        bool enableSummarization = false,
        IOpenAiClient? openAiClient = null)
    {
        if (!enableSummarization || !CanUseOpenAi(openAiApiKey, openAiMode))
        {
            return new NoopOrderExceptionSummarizer();
        }

        if (!IsRealMode(openAiMode))
        {
            return new DeterministicOrderExceptionSummarizer();
        }

        var settings = BuildSettings(openAiApiKey!, openAiMode, enableSummarization: true);
        return new OpenAiOrderExceptionSummarizer(openAiClient ?? HttpOpenAiClient.CreateDefault(), settings);
    }

    private static bool CanUseOpenAi(string? openAiApiKey, string? openAiMode)
    {
        if (string.IsNullOrWhiteSpace(openAiApiKey))
        {
            return false;
        }

        return !IsMode(openAiMode, ModeOff);
    }

    private static OpenAiPlannerSettings BuildSettings(string openAiApiKey, string? openAiMode, bool enableSummarization)
    {
        return new OpenAiPlannerSettings
        {
            ApiKey = openAiApiKey.Trim(),
            EmulateToolCalling = !IsRealMode(openAiMode),
            EnableSummarization = enableSummarization
        };
    }

    private static bool IsRealMode(string? openAiMode)
    {
        return IsMode(openAiMode, ModeReal);
    }

    private static bool IsMode(string? openAiMode, string expectedMode)
    {
        var mode = string.IsNullOrWhiteSpace(openAiMode) ? ModeEmulated : openAiMode.Trim();
        return mode.Equals(expectedMode, StringComparison.OrdinalIgnoreCase);
    }
}
