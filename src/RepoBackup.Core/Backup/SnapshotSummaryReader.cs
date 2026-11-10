using System.Text.Json;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Backup;

public static class SnapshotSummaryReader
{
    // A built-in DTO permits the serializer to skip unknown paths incrementally.
    // A JsonDocument/custom object converter would buffer an entire snapshot.
    private sealed record Row(string Id, DateTimeOffset Time, string[] Tags, string? Hostname);
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static async Task<List<SnapshotSummary>> ReadAsync(Stream stream, CancellationToken token = default)
    {
        var rows = await JsonSerializer.DeserializeAsync<List<Row>>(stream, Options, token) ?? [];
        return rows.Select(r => new SnapshotSummary(r.Id ?? throw new InvalidDataException("Snapshot has no identity."), r.Time,
            r.Tags ?? []) { Hostname = r.Hostname }).OrderByDescending(s => s.Time).ToList();
    }
}
