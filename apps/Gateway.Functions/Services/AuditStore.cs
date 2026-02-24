using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EclipseAi.Connectors.Erp;
using EclipseAi.Observability;

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

        var safeCorrelationId = Correlation.ToSafeFileName(correlationId);
        var filePath = Path.Combine(directory, $"{safeCorrelationId}.json");
        var json = JsonSerializer.Serialize(payload, s_jsonOptions);
        await File.WriteAllTextAsync(filePath, json, ct);
    }
}

public sealed class IdempotencyCache
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _directory = Path.Combine(Directory.GetCurrentDirectory(), ".idempotency");

    public IdempotencyReservation ReserveDraft(string key, string payloadHash)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return new IdempotencyReservation(IdempotencyStatus.Conflict, null, null, null);
        }

        var gate = _locks.GetOrAdd(key, _ => new object());
        lock (gate)
        {
            Directory.CreateDirectory(_directory);
            var path = GetPath(key);
            if (TryLoad(path, out var existing))
            {
                return EvaluateExisting(existing, payloadHash);
            }

            var pending = new IdempotencyRecord(key, payloadHash, null, null, null, "pending", DateTimeOffset.UtcNow);
            if (TryWriteNew(path, pending))
            {
                return new IdempotencyReservation(IdempotencyStatus.Reserved, null, null, null);
            }

            if (TryLoad(path, out existing))
            {
                return EvaluateExisting(existing, payloadHash);
            }

            return new IdempotencyReservation(IdempotencyStatus.Conflict, null, null, null);
        }
    }

    public void CompleteDraft(string key, string payloadHash, DraftOrderDto draft)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(draft.DraftId))
        {
            return;
        }

        var gate = _locks.GetOrAdd(key, _ => new object());
        lock (gate)
        {
            Directory.CreateDirectory(_directory);
            var path = GetPath(key);
            var record = new IdempotencyRecord(
                key,
                payloadHash,
                draft.DraftId,
                draft.ExternalOrderNumber,
                draft.Status,
                "completed",
                DateTimeOffset.UtcNow);
            var json = JsonSerializer.Serialize(record, s_jsonOptions);
            File.WriteAllText(path, json);
        }
    }

    public void FailDraft(string key, string payloadHash)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var gate = _locks.GetOrAdd(key, _ => new object());
        lock (gate)
        {
            var path = GetPath(key);
            if (!TryLoad(path, out var existing))
            {
                return;
            }

            if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.Equals(existing.Status, "pending", StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
            }
        }
    }

    public static string ComputePayloadHash(CreateDraftOrderDto dto)
    {
        var payload = new DraftPayload(dto.CustomerId, dto.RequestedDate, dto.Lines);
        var json = JsonSerializer.Serialize(payload);
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes);
    }

    private static IdempotencyReservation EvaluateExisting(IdempotencyRecord existing, string payloadHash)
    {
        if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.OrdinalIgnoreCase))
        {
            return new IdempotencyReservation(IdempotencyStatus.Conflict, null, null, null);
        }

        if (!string.IsNullOrWhiteSpace(existing.DraftId))
        {
            return new IdempotencyReservation(
                IdempotencyStatus.Existing,
                existing.DraftId,
                existing.ExternalOrderNumber,
                existing.DraftStatus);
        }

        if (string.Equals(existing.Status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            return new IdempotencyReservation(IdempotencyStatus.InProgress, null, null, null);
        }

        return new IdempotencyReservation(IdempotencyStatus.Conflict, null, null, null);
    }

    private string GetPath(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_directory, $"{hash}.json");
    }

    private static bool TryLoad(string path, out IdempotencyRecord record)
    {
        record = null!;
        if (!File.Exists(path))
        {
            return false;
        }

        var json = File.ReadAllText(path);
        record = JsonSerializer.Deserialize<IdempotencyRecord>(json) ?? null!;
        return record is not null;
    }

    private static bool TryWriteNew(string path, IdempotencyRecord record)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, record, s_jsonOptions);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}

public sealed record IdempotencyReservation(
    IdempotencyStatus Status,
    string? DraftId,
    string? ExternalOrderNumber,
    string? DraftStatus);

public enum IdempotencyStatus
{
    Reserved,
    Existing,
    InProgress,
    Conflict
}

public sealed record IdempotencyRecord(
    string Key,
    string PayloadHash,
    string? DraftId,
    string? ExternalOrderNumber,
    string? DraftStatus,
    string Status,
    DateTimeOffset UpdatedUtc);

public sealed record DraftPayload(string CustomerId, string RequestedDate, IReadOnlyList<DraftLineDto> Lines);
