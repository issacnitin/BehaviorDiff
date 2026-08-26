using System.Text.Json;
using System.Text.Json.Serialization;

namespace RealDiff.Mcp;

/// <summary>
/// One analysis, persisted under .realdiff/runs/&lt;run_id&gt;/ so a run survives a server restart.
/// The engine's own artifacts are copied in beside this; nothing here re-derives them.
/// </summary>
public sealed class RunRecord
{
    [JsonPropertyName("runId")] public string RunId { get; set; } = string.Empty;

    /// <summary>queued | running | complete | refused | failed</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "queued";

    [JsonPropertyName("phase")] public string Phase { get; set; } = "queued";

    [JsonPropertyName("progress")] public int Progress { get; set; }

    [JsonPropertyName("error")] public string? Error { get; set; }

    [JsonPropertyName("repoPath")] public string RepoPath { get; set; } = string.Empty;

    [JsonPropertyName("baseRef")] public string BaseRef { get; set; } = string.Empty;

    [JsonPropertyName("prRef")] public string PrRef { get; set; } = string.Empty;

    [JsonPropertyName("exitCode")] public int? ExitCode { get; set; }

    [JsonPropertyName("startedUtc")] public DateTimeOffset StartedUtc { get; set; }

    [JsonPropertyName("completedUtc")] public DateTimeOffset? CompletedUtc { get; set; }
}

public static class RunStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Root { get; set; } =
        Path.Combine(Directory.GetCurrentDirectory(), ".realdiff", "runs");

    public static string Directory_(string runId) => Path.Combine(Root, runId);

    public static RunRecord Create(string repoPath, string baseRef, string prRef)
    {
        var record = new RunRecord
        {
            RunId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6],
            RepoPath = repoPath,
            BaseRef = baseRef,
            PrRef = prRef,
            StartedUtc = DateTimeOffset.UtcNow,
        };

        System.IO.Directory.CreateDirectory(Directory_(record.RunId));
        Save(record);
        return record;
    }

    public static void Save(RunRecord record)
    {
        System.IO.Directory.CreateDirectory(Directory_(record.RunId));
        File.WriteAllText(Path.Combine(Directory_(record.RunId), "status.json"), JsonSerializer.Serialize(record, Json));
    }

    public static RunRecord? Load(string runId)
    {
        string path = Path.Combine(Directory_(runId), "status.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(path)) : null;
    }

    public static JsonDocument? LoadArtifact(string runId, string fileName)
    {
        string path = Path.Combine(Directory_(runId), fileName);
        return File.Exists(path) ? JsonDocument.Parse(File.ReadAllText(path)) : null;
    }
}
