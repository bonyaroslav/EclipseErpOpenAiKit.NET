namespace EclipseAi.Observability;

public static class Correlation
{
    public static string NewId() => Guid.NewGuid().ToString(""N"");
}
