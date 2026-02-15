using System.Collections.Concurrent;
using System.Text.Json;

namespace Gateway.Functions;

public interface IAuditStore
{
    Task WriteAsync(string correlationId, object payload, CancellationToken ct);
}

public sealed class FileAuditStore : IAuditStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task WriteAsync(string correlationId, object payload, CancellationToken ct)
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), ".audit");
        Directory.CreateDirectory(directory);

        var filePath = Path.Combine(directory, $"{correlationId}.json");
        var json = JsonSerializer.Serialize(payload, s_jsonOptions);
        await File.WriteAllTextAsync(filePath, json, ct);
    }
}

public sealed class IdempotencyCache
{
    private readonly ConcurrentDictionary<string, string> _draftIds = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string key, out string draftId)
    {
        return _draftIds.TryGetValue(key, out draftId!);
    }

    public void Set(string key, string draftId)
    {
        _draftIds[key] = draftId;
    }
}
