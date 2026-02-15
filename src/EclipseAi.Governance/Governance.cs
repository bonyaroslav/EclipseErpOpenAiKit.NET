namespace EclipseAi.Governance;

public interface IRedactor
{
    object Redact(object input);
}

public sealed class NoopRedactor : IRedactor
{
    public object Redact(object input) => input;
}
